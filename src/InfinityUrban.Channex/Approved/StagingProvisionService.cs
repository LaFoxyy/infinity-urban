using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Stayline;

public record StagingProvisionEntity(string Kind, Guid LocalId, string ExternalId, bool Created);
public record StagingProvisionResult(Guid PropertyId, string ExternalPropertyId, int CreatedProperties,
    int CreatedRoomTypes, int CreatedRatePlans, int QueuedChanges, bool AlreadyProvisioned,
    IReadOnlyList<StagingProvisionEntity> Entities);

/// <summary>
/// Publish real local property metadata to staging, then enqueue the saved calendar. An intent is
/// durably written before each non-idempotent POST; uncertain results require mapping reconciliation.
/// No speculative retry can create another property after a lost response or server restart.
/// </summary>
public sealed class StagingProvisionService(IDbContextFactory<AppDb> factory, MutationLock mutations,
    ChannexClient client, IOptions<ChannexOptions> options)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> PropertyGates = new();
    private const string UncertainMessage = "A previous Channex creation request has an unknown outcome. Check the Channex staging property, rooms and rate plans, then reconcile their IDs through mapping before trying again. An automatic retry could create duplicates.";

    public async Task<StagingProvisionResult> ProvisionAsync(Guid propertyId, CancellationToken ct = default)
    {
        if (!client.IsEnabled) throw new AppError("Configure and enable the staging API key on the server before publishing a property.", 409);
        var gate = PropertyGates.GetOrAdd(propertyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Keep mapping edits, inventory edits and the ARI worker out of a partially-created graph.
            await mutations.Gate.WaitAsync(ct);
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var property = await PmsService.PropertyAsync(db, propertyId, ct);
                var rooms = await db.RoomTypes.Where(x => x.PropertyId == propertyId).OrderBy(x => x.Id).ToListAsync(ct);
                var roomIds = rooms.Select(x => x.Id).ToArray();
                var rates = await db.RatePlans.Where(x => roomIds.Contains(x.RoomTypeId)).OrderBy(x => x.Id).ToListAsync(ct);
                PmsService.Require(rooms.Count > 0 && rates.Count > 0 && rooms.All(room => rates.Any(rate => rate.RoomTypeId == room.Id)),
                    "Add at least one room or rental unit and a rate plan for every room before publishing to Channex.");
                PmsService.Require(rooms.All(x => x.Quantity is >= 1 and <= 1000 && x.Occupancy is >= 1 and <= 30), "Review each room's inventory and occupancy before publishing.");
                PmsService.Require(rates.All(x => x.BasePrice > 0 && decimal.Round(x.BasePrice, 2) == x.BasePrice), "Every rate plan needs a positive base price with at most two decimals.");
                var rateTitles = BuildRateTitles(rooms, rates);
                if (string.IsNullOrWhiteSpace(property.ChannexId) && (rooms.Any(x => !string.IsNullOrWhiteSpace(x.ChannexId)) || rates.Any(x => !string.IsNullOrWhiteSpace(x.ChannexId))))
                    throw new AppError("This property has saved child mappings but no Channex property mapping. Reconcile the property mapping before creating more inventory.", 409);
                if (rates.Any(rate => !string.IsNullOrWhiteSpace(rate.ChannexId) && string.IsNullOrWhiteSpace(rooms.Single(room => room.Id == rate.RoomTypeId).ChannexId)))
                    throw new AppError("A mapped rate plan has an unmapped parent room. Reconcile the room mapping before publishing.", 409);
                var unknown = await db.SyncAttempts.AsNoTracking().Where(x => x.PropertyId == propertyId &&
                    (x.Status == "provision-sending" || x.Status == "provision-uncertain")).Select(x => x.Detail).ToListAsync(ct);
                var unmapped = rooms.Where(x => string.IsNullOrWhiteSpace(x.ChannexId)).Select(x => x.Id)
                    .Concat(rates.Where(x => string.IsNullOrWhiteSpace(x.ChannexId)).Select(x => x.Id)).ToHashSet();
                if (string.IsNullOrWhiteSpace(property.ChannexId)) unmapped.Add(propertyId);
                if (unknown.Any(x => Guid.TryParse(ReadDetail(x, "localEntityId"), out var id) && unmapped.Contains(id)))
                    throw new AppError(UncertainMessage, 409);
                var result = new List<StagingProvisionEntity>();
                var propertyCreated = false;
                var createdRooms = 0;
                var createdRates = 0;

                if (!string.IsNullOrWhiteSpace(property.ChannexId))
                    await VerifyPropertyAsync(property, ct);
                else
                {
                    var json = JsonSerializer.Serialize(new { property = new {
                        title = property.Name, currency = property.Currency, timezone = property.TimeZoneId,
                        property_type = property.Kind == "rental" ? "apartment" : "hotel",
                        settings = new { min_stay_type = "both", state_length = 500,
                            allow_availability_autoupdate_on_modification = false,
                            allow_availability_autoupdate_on_cancellation = false }
                    } });
                    await CreateAsync(db, propertyId, "properties", property.Id, json, id => property.ChannexId = id, ct);
                    propertyCreated = true;
                }
                result.Add(new("property", property.Id, property.ChannexId!, propertyCreated));

                // Validate every existing relationship before creating any further rooms or plans.
                foreach (var room in rooms.Where(x => !string.IsNullOrWhiteSpace(x.ChannexId)))
                    await VerifyRoomAsync(room, property.ChannexId!, ct);
                foreach (var rate in rates.Where(x => !string.IsNullOrWhiteSpace(x.ChannexId)))
                {
                    var room = rooms.Single(x => x.Id == rate.RoomTypeId);
                    if (string.IsNullOrWhiteSpace(room.ChannexId))
                        throw new AppError("A mapped rate plan has an unmapped parent room. Reconcile the room mapping before publishing.", 409);
                    await VerifyRateAsync(rate, room.ChannexId, property, ct);
                }

                foreach (var room in rooms)
                {
                    var created = string.IsNullOrWhiteSpace(room.ChannexId);
                    if (created)
                    {
                        var json = JsonSerializer.Serialize(new { room_type = new {
                            property_id = property.ChannexId, title = room.Name, count_of_rooms = room.Quantity,
                            occ_adults = room.Occupancy, occ_children = 0, occ_infants = 0,
                            default_occupancy = room.Occupancy, room_kind = "room"
                        } });
                        await CreateAsync(db, propertyId, "room_types", room.Id, json, id => room.ChannexId = id, ct);
                        createdRooms++;
                    }
                    result.Add(new("room_type", room.Id, room.ChannexId!, created));
                    foreach (var rate in rates.Where(x => x.RoomTypeId == room.Id))
                    {
                        var rateCreated = string.IsNullOrWhiteSpace(rate.ChannexId);
                        if (rateCreated)
                        {
                            var json = JsonSerializer.Serialize(new { rate_plan = new {
                                property_id = property.ChannexId, room_type_id = room.ChannexId,
                                title = rateTitles[rate.Id], currency = property.Currency,
                                sell_mode = "per_room", rate_mode = "manual",
                                options = new[] { new { occupancy = room.Occupancy, is_primary = true,
                                    rate = rate.BasePrice.ToString("0.00", CultureInfo.InvariantCulture) } }
                            } });
                            await CreateAsync(db, propertyId, "rate_plans", rate.Id, json, id => rate.ChannexId = id, ct);
                            createdRates++;
                        }
                        result.Add(new("rate_plan", rate.Id, rate.ChannexId!, rateCreated));
                    }
                }

                var fingerprint = Fingerprint(result);
                var previous = await db.SyncAttempts.AsNoTracking()
                    .Where(x => x.PropertyId == propertyId && x.Endpoint == "provision" && x.Status == "provisioned")
                    .Select(x => x.Detail).ToListAsync(ct);
                var alreadyProvisioned = previous.Any(x => ReadDetail(x, "fingerprint") == fingerprint);
                var queued = 0;
                if (!alreadyProvisioned)
                {
                    // This marker and the initial ARI outbox are one atomic local commit.
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    // Unmapped edits may have paused the worker permanently. Completing the
                    // graph resolves that cause; retain timed rate-limit/network backoff gates.
                    db.SyncGates.RemoveRange(await db.SyncGates.Where(x => x.PropertyId == propertyId &&
                        x.NextAttemptAt == DateTimeOffset.MaxValue).ToListAsync(ct));
                    queued = await PmsService.FullSyncAsync(db, property, ct);
                    db.SyncAttempts.Add(new SyncAttempt {
                        PropertyId = propertyId, Endpoint = "provision", Status = "provisioned", ChangeCount = queued,
                        TaskIds = "[]", RequestJson = "{}",
                        Detail = JsonSerializer.Serialize(new { fingerprint, externalPropertyId = property.ChannexId,
                            message = "Metadata is mapped. The saved 500-day calendar is queued for the ARI worker; its responses will contain the real task IDs." })
                    });
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                return new(propertyId, property.ChannexId!, propertyCreated ? 1 : 0, createdRooms, createdRates,
                    queued, alreadyProvisioned, result);
            }
            finally { mutations.Gate.Release(); }
        }
        finally { gate.Release(); }
    }

    private async Task CreateAsync(AppDb db, Guid propertyId, string endpoint, Guid localId, string requestJson,
        Action<string> map, CancellationToken ct)
    {
        var unresolved = await db.SyncAttempts.AsNoTracking().Where(x => x.PropertyId == propertyId && x.Endpoint == endpoint &&
            (x.Status == "provision-sending" || x.Status == "provision-uncertain")).Select(x => x.Detail).ToListAsync(ct);
        if (unresolved.Any(x => ReadDetail(x, "localEntityId") == localId.ToString("D")))
            throw new AppError(UncertainMessage, 409);
        var attempt = new SyncAttempt {
            PropertyId = propertyId, Endpoint = endpoint, Status = "provision-sending", RequestJson = Safe(requestJson),
            TaskIds = "[]", ChangeCount = 1,
            Detail = JsonSerializer.Serialize(new { operation = "create", localEntityId = localId, entityType = endpoint })
        };
        db.SyncAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        ChannexResponse response;
        try { response = await client.CreateStagingEntityAsync(endpoint, requestJson, ct); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            attempt.Status = "provision-uncertain";
            attempt.Detail = JsonSerializer.Serialize(new { operation = "create", localEntityId = localId, entityType = endpoint,
                message = UncertainMessage });
            await db.SaveChangesAsync(CancellationToken.None);
            throw new AppError(UncertainMessage, 409);
        }
        attempt.HttpStatus = response.StatusCode;
        var externalId = CreatedId(response, endpoint);
        if (response.IsSuccess && externalId is not null)
        {
            map(externalId);
            attempt.Status = "provisioned";
            attempt.Detail = JsonSerializer.Serialize(new { operation = "create", localEntityId = localId,
                entityType = endpoint, externalId, response = Safe(response.Body) });
            // Persist the remote identity even if the browser disconnects at this point.
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }
        var definitive = response.StatusCode is >= 400 and < 500 && response.StatusCode != 408;
        attempt.Status = definitive ? "provision-error" : "provision-uncertain";
        attempt.Detail = JsonSerializer.Serialize(new { operation = "create", localEntityId = localId,
            entityType = endpoint, response = Safe(response.Body), message = definitive ? "The request was rejected; correct the fields or access before retrying." : UncertainMessage });
        await db.SaveChangesAsync(CancellationToken.None);
        if (!definitive) throw new AppError(UncertainMessage, 409);
        var explanation = response.StatusCode switch {
            401 or 403 => "The staging key does not permit this operation. Check its property and API access.",
            429 => "The staging request limit was reached. Wait before trying again; previously created IDs have been saved.",
            _ => ValidationSummary(response.Body)
        };
        throw new AppError($"Channex rejected {endpoint} creation (HTTP {response.StatusCode}). {explanation}", response.StatusCode == 429 ? 429 : 400);
    }

    private async Task VerifyPropertyAsync(Property property, CancellationToken ct)
    {
        var data = await ReadEntityAsync("properties", property.ChannexId!, ct);
        var attributes = data.GetProperty("attributes");
        if (!string.Equals(JsonFields.Optional(attributes, "currency"), property.Currency, StringComparison.OrdinalIgnoreCase))
            throw new AppError($"Property currencies must match before publishing. Local currency is {property.Currency}; review the selected Channex property mapping.", 409);
    }

    private async Task VerifyRoomAsync(RoomType room, string propertyId, CancellationToken ct)
    {
        var data = await ReadEntityAsync("room_types", room.ChannexId!, ct);
        if (!Same(Relationship(data, "property", "property_id"), propertyId))
            throw new AppError("A mapped Channex room belongs to a different property. Correct the room mapping before publishing.", 409);
    }

    private async Task VerifyRateAsync(RatePlan rate, string roomId, Property property, CancellationToken ct)
    {
        var data = await ReadEntityAsync("rate_plans", rate.ChannexId!, ct);
        var attributes = data.GetProperty("attributes");
        if (!Same(Relationship(data, "property", "property_id"), property.ChannexId) || !Same(Relationship(data, "room_type", "room_type_id"), roomId))
            throw new AppError("A mapped Channex rate plan belongs to a different room or property. Correct the rate mapping before publishing.", 409);
        if (!string.Equals(JsonFields.Optional(attributes, "currency"), property.Currency, StringComparison.OrdinalIgnoreCase))
            throw new AppError("A mapped Channex rate plan has a different currency. Correct the rate mapping before publishing.", 409);
        if (JsonFields.Optional(attributes, "sell_mode") != "per_room" || JsonFields.Optional(attributes, "rate_mode") is not ("manual" or "auto"))
            throw new AppError("This PMS publishes independent per-room prices. Map a manual or auto per-room Channex rate plan before publishing.", 409);
    }

    private async Task<JsonElement> ReadEntityAsync(string endpoint, string externalId, CancellationToken ct)
    {
        if (!Guid.TryParse(externalId, out var id)) throw new AppError("A saved Channex mapping is not a valid UUID. Correct it before publishing.", 409);
        JsonElement root;
        try { root = await client.ReadStagingJsonAsync($"{endpoint}/{id:D}", ct); }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        { throw new AppError("A saved Channex mapping could not be verified. Check the staging key access and reconcile the existing entity instead of creating a duplicate.", 409); }
        if (!root.TryGetProperty("data", out var data) || !Same(JsonFields.Optional(data, "id"), externalId) ||
            !data.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            throw new AppError("Channex returned an invalid mapped entity. Review its mapping before publishing.", 502);
        return data;
    }

    private static string? Relationship(JsonElement data, string name, string direct)
    {
        if (data.TryGetProperty("attributes", out var attributes) && JsonFields.Optional(attributes, direct) is { } value) return value;
        return data.TryGetProperty("relationships", out var relationships) && relationships.TryGetProperty(name, out var relation) &&
            relation.TryGetProperty("data", out var related) ? JsonFields.Optional(related, "id") : null;
    }
    private static bool Same(string? left, string? right) => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string? CreatedId(ChannexResponse response, string endpoint)
    {
        if (!response.IsSuccess) return null;
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object || !Guid.TryParse(JsonFields.Optional(data, "id"), out var id) || id == Guid.Empty)
                return null;
            var expectedType = endpoint switch { "properties" => "property", "room_types" => "room_type", _ => "rate_plan" };
            if (data.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != expectedType))
                return null;
            return id.ToString("D");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }
    private static string? ReadDetail(string json, string field)
    {
        try { using var doc = JsonDocument.Parse(json); return JsonFields.Optional(doc.RootElement, field); }
        catch (JsonException) { return null; }
    }
    private static Dictionary<Guid, string> BuildRateTitles(IReadOnlyList<RoomType> rooms, IReadOnlyList<RatePlan> rates)
    {
        var duplicateNames = rates.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titles = rates.ToDictionary(x => x.Id, x => duplicateNames.Contains(x.Name) ? $"{rooms.Single(r => r.Id == x.RoomTypeId).Name} · {x.Name}" : x.Name);
        PmsService.Require(titles.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == titles.Count,
            "Some rate plans would have the same Channex title. Give those rate plans distinct names before publishing.");
        return titles;
    }
    private static string Fingerprint(IEnumerable<StagingProvisionEntity> entities) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(entities.OrderBy(x => x.Kind).ThenBy(x => x.LocalId).Select(x => new { x.Kind, x.LocalId, x.ExternalId })))));
    private string Safe(string value)
    {
        foreach (var secret in new[] { options.Value.ApiKey, options.Value.WebhookSecret }.Where(x => !string.IsNullOrEmpty(x)).OrderByDescending(x => x.Length))
        {
            var jsonSecret = JsonSerializer.Serialize(secret);
            foreach (var representation in new[] { secret, jsonSecret[1..^1], Uri.EscapeDataString(secret) }.Distinct().OrderByDescending(x => x.Length))
                value = value.Replace(representation, "[redacted]", StringComparison.Ordinal);
        }
        return value.Length <= 6000 ? value : value[..6000] + "…";
    }
    private string ValidationSummary(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
                return Safe(errors.GetRawText()) is { Length: > 1000 } longText ? longText[..1000] : Safe(errors.GetRawText());
        }
        catch (JsonException) { }
        return "Review the property's details, room inventory and rate-plan settings, then try again. Previously created IDs have been saved.";
    }
}
