using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Stayline;

public record StagingImportRatePreview(string ExternalId, string Name, decimal? BasePrice,
    IReadOnlyList<string> MissingPriceDates)
{
    public int MissingPriceCount => MissingPriceDates.Count;
}
public record StagingImportRoomPreview(string ExternalId, string Name, int Quantity, int Occupancy,
    IReadOnlyList<StagingImportRatePreview> RatePlans);
public record StagingImportPreview(string ExternalPropertyId, string Name, string Currency, string TimeZoneId,
    IReadOnlyList<StagingImportRoomPreview> Rooms, IReadOnlyList<string> Warnings, int Days = 500);
public record StagingImportResult(Guid PropertyId, string Name, string Currency, int RoomCount, int RateCount,
    bool AlreadyImported, string ExternalPropertyId, IReadOnlyList<string> Warnings);

/// <summary>
/// One-time onboarding from an existing staging property. The imported snapshot is deliberately
/// unmapped: neither this service nor its preview publishes ARI or acknowledges bookings.
/// </summary>
public sealed class StagingImportService(IDbContextFactory<AppDb> factory, MutationLock mutations, ChannexClient client)
{
    private const int ImportDays = 500;
    private static readonly string[] SnapshotWarnings =
    [
        "This is a local draft. Review its calendar and save its Channex mappings separately before enabling synchronization. Importing sends no prices or availability to Channex.",
        "Availability is a snapshot of Channex's remaining sellable inventory. Reconcile existing future reservations before mapping or receiving bookings, so those bookings are not deducted twice.",
        "Daily prices and restrictions are copied for 500 days. Each rate plan's local base price uses its first positive imported daily price, or the initialization price you enter; review that base price before extending the calendar."
    ];

    public async Task<StagingImportPreview> PreviewAsync(string? externalPropertyId, CancellationToken ct = default)
    {
        var id = ValidateId(externalPropertyId);
        return (await FetchSnapshotAsync(id, ct)).Preview;
    }

    public Task<StagingImportResult> ImportAsync(string? externalPropertyId, CancellationToken ct = default) =>
        ImportAsync(externalPropertyId, null, ct);

    public async Task<StagingImportResult> ImportAsync(string? externalPropertyId,
        IReadOnlyDictionary<string, decimal>? missingPriceDefaults, CancellationToken ct = default)
    {
        var id = ValidateId(externalPropertyId);
        var defaults = ValidatePriceDefaults(missingPriceDefaults);
        // Check before doing network work, then re-check while committing to make concurrent
        // imports idempotent. Provenance remains separate from the active Channex mapping.
        await mutations.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var existing = await FindExistingAsync(db, id, ct);
            if (existing != null && defaults.Count == 0) return await ExistingResultAsync(db, existing, id, ct);
        }
        finally { mutations.Gate.Release(); }

        var snapshot = await FetchSnapshotAsync(id, ct);
        ValidateSnapshotDefaults(snapshot, defaults);
        await mutations.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var existing = await FindExistingAsync(db, id, ct);
            if (existing != null) return await ExistingResultAsync(db, existing, id, ct);
            var preview = snapshot.Preview;
            var property = new Property
            {
                Name = preview.Name, Kind = snapshot.Kind, Currency = preview.Currency,
                TimeZoneId = preview.TimeZoneId, ChannexId = null
            };
            db.Properties.Add(property);
            var roomMappings = new List<object>();
            var rateMappings = new List<object>();
            foreach (var importedRoom in snapshot.Rooms)
            {
                var room = new RoomType
                {
                    PropertyId = property.Id, Name = importedRoom.Preview.Name,
                    Quantity = importedRoom.Preview.Quantity, Occupancy = importedRoom.Preview.Occupancy,
                    ChannexId = null
                };
                db.RoomTypes.Add(room);
                roomMappings.Add(new { localId = room.Id, externalId = importedRoom.Preview.ExternalId });
                foreach (var day in importedRoom.Inventory)
                    db.DailyInventories.Add(new DailyInventory { RoomTypeId = room.Id, Date = day.Date, Capacity = day.Capacity });
                foreach (var importedRate in importedRoom.Rates)
                {
                    var rate = new RatePlan
                    {
                        RoomTypeId = room.Id, Name = importedRate.Preview.Name,
                        BasePrice = defaults.TryGetValue(importedRate.Preview.ExternalId, out var initializationPrice)
                            ? initializationPrice : importedRate.Preview.BasePrice!.Value,
                        ChannexId = null
                    };
                    db.RatePlans.Add(rate);
                    rateMappings.Add(new { localId = rate.Id, externalId = importedRate.Preview.ExternalId });
                    foreach (var day in importedRate.Days)
                        db.DailyRates.Add(new DailyRate
                        {
                            RatePlanId = rate.Id, Date = day.Date,
                            Rate = day.Rate == 0 ? defaults[importedRate.Preview.ExternalId] : day.Rate,
                            MinStayArrival = day.MinStayArrival, MinStayThrough = day.MinStayThrough,
                            MaxStay = day.MaxStay, StopSell = day.StopSell,
                            ClosedToArrival = day.ClosedToArrival, ClosedToDeparture = day.ClosedToDeparture
                        });
                }
            }
            db.SyncAttempts.Add(new SyncAttempt
            {
                PropertyId = property.Id, Endpoint = "import", Status = "imported", TaskIds = "[]",
                ChangeCount = 0, Detail = defaults.Count == 0
                    ? "Imported a read-only Channex staging snapshot as an unmapped local draft. No ARI was sent."
                    : "Imported a read-only Channex staging snapshot as an unmapped local draft. Zero-price dates were initialized locally with the supplied prices. No ARI was sent.",
                RequestJson = JsonSerializer.Serialize(new
                {
                    externalPropertyId = id, source = "channex-staging", snapshot.From, days = ImportDays,
                    roomMappings, rateMappings, missingPriceDefaults = defaults
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            // Do not use the normal create/edit methods here: those deliberately enqueue ARI.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(property.Id, property.Name, property.Currency, snapshot.Rooms.Count,
                snapshot.Rooms.Sum(x => x.Rates.Count), false, id, preview.Warnings);
        }
        finally { mutations.Gate.Release(); }
    }

    private string ValidateId(string? value)
    {
        if (!client.IsEnabled) throw new AppError("Channex staging is disabled. Configure the server API key and Enabled setting first.", 409);
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new AppError("Choose a valid Channex staging property UUID.");
        return id.ToString("D");
    }

    private static IReadOnlyDictionary<string, decimal> ValidatePriceDefaults(IReadOnlyDictionary<string, decimal>? values)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (values == null) return result;
        foreach (var (externalId, price) in values)
        {
            if (!Guid.TryParse(externalId, out var rateId) || rateId == Guid.Empty)
                throw new AppError("Each initialization price must reference a valid Channex rate plan UUID.");
            if (price <= 0 || price > 1_000_000 || decimal.Round(price, 2) != price)
                throw new AppError("Enter a positive initialization price of at most 1,000,000 with no more than two decimal places for each unpriced rate plan.");
            if (!result.TryAdd(rateId.ToString("D"), price))
                throw new AppError("An initialization price was supplied more than once for the same rate plan.");
        }
        return result;
    }

    private static void ValidateSnapshotDefaults(ImportedSnapshot snapshot, IReadOnlyDictionary<string, decimal> defaults)
    {
        var rates = snapshot.Rooms.SelectMany(x => x.Rates).ToArray();
        var rateIds = rates.Select(x => x.Preview.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (defaults.Keys.Any(x => !rateIds.Contains(x)))
            throw new AppError("An initialization price refers to a rate plan outside the selected Channex property. Refresh the import preview and try again.");
        var missing = rates.Where(x => x.Preview.MissingPriceCount > 0 && !defaults.ContainsKey(x.Preview.ExternalId)).ToArray();
        if (missing.Length > 0)
            throw new AppError($"Choose a positive local initialization price for each unpriced rate plan before importing ({missing.Length} still need a price). Refresh the preview to review the affected dates; no local data was changed.");
    }

    private static async Task<Property?> FindExistingAsync(AppDb db, string externalId, CancellationToken ct)
    {
        var mapped = await db.Properties.AsNoTracking().Where(x => x.ChannexId != null).ToListAsync(ct);
        var property = mapped.FirstOrDefault(x => SameId(x.ChannexId, externalId));
        if (property != null) return property;
        var imports = await db.SyncAttempts.AsNoTracking().Where(x => x.Endpoint == "import" && x.Status == "imported").ToListAsync(ct);
        foreach (var import in imports.OrderBy(x => x.Id))
        {
            try
            {
                using var document = JsonDocument.Parse(import.RequestJson);
                if (!SameId(JsonFields.Optional(document.RootElement, "externalPropertyId"), externalId)) continue;
                property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(x => x.Id == import.PropertyId, ct);
                if (property != null) return property;
            }
            catch (JsonException) { /* Historical malformed log entries cannot establish provenance. */ }
        }
        return null;
    }

    private static async Task<StagingImportResult> ExistingResultAsync(AppDb db, Property property, string id, CancellationToken ct)
    {
        var rooms = await db.RoomTypes.AsNoTracking().Where(x => x.PropertyId == property.Id).Select(x => x.Id).ToListAsync(ct);
        var rateCount = await db.RatePlans.CountAsync(x => rooms.Contains(x.RoomTypeId), ct);
        return new(property.Id, property.Name, property.Currency, rooms.Count, rateCount, true, id,
            ["This staging property is already present locally. Its saved data and mappings were preserved; no duplicate was created and no ARI was sent."]);
    }

    private async Task<ImportedSnapshot> FetchSnapshotAsync(string id, CancellationToken ct)
    {
        try
        {
            var propertyRoot = await client.ReadStagingJsonAsync($"properties/{id}", ct);
            var propertyItem = Object(propertyRoot, "data");
            if (!SameId(RequiredId(propertyItem), id)) throw RemoteError("Channex returned a different property than requested.");
            var property = Object(propertyItem, "attributes");
            var name = RequiredName(property);
            var currency = RequiredString(property, "currency").ToUpperInvariant();
            if (!PmsService.Currencies.Contains(currency)) throw RemoteError("This property's currency is not supported by Stayline's two-decimal pricing model.");
            var timeZone = RequiredString(property, "timezone");
            try { TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            { throw RemoteError("The staging property has an unsupported timezone. Set a valid IANA timezone in Channex before importing."); }
            var from = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);
            var to = from.AddDays(ImportDays - 1);
            var filter = $"?filter[property_id]={id}";
            var roomItems = await client.FetchCollectionAsync("room_types" + filter, ct);
            var rateItems = await client.FetchCollectionAsync("rate_plans" + filter, ct);
            if (roomItems.Count == 0 || rateItems.Count == 0)
                throw RemoteError("Add room types and rate plans to this Channex staging property before importing it.");
            if (roomItems.Count > 200 || rateItems.Count > 1000)
                throw RemoteError("This staging property exceeds the current import size limit (200 room types or 1,000 rate plans).");
            var roomsById = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var room in roomItems)
            {
                if (!SameId(RelationshipId(room, "property", "property_id"), id)) throw RemoteError("Channex returned a room outside the selected property.");
                if (!roomsById.TryAdd(RequiredId(room), room)) throw RemoteError("Channex returned duplicate room types.");
            }
            var ratesByRoom = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);
            var rateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rate in rateItems)
            {
                if (!SameId(RelationshipId(rate, "property", "property_id"), id)) throw RemoteError("Channex returned a rate outside the selected property.");
                var roomId = RelationshipId(rate, "room_type", "room_type_id");
                if (!roomsById.ContainsKey(roomId)) throw RemoteError("A Channex rate plan refers to a room outside the selected property.");
                if (!rateIds.Add(RequiredId(rate))) throw RemoteError("Channex returned duplicate rate plans.");
                var attributes = Object(rate, "attributes");
                if (RequiredString(attributes, "sell_mode") != "per_room")
                    throw RemoteError("This staging property uses per-person pricing. Stayline currently imports per-room rate plans only; no data was imported.");
                if (RequiredString(attributes, "rate_mode") is not ("manual" or "auto"))
                    throw RemoteError("This staging property uses a derived rate plan. Stayline currently imports manual or automatic per-room snapshots only; no data was imported.");
                if (!string.Equals(RequiredString(attributes, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                    throw RemoteError("A rate plan has a different currency from its property. Stayline cannot import mixed-currency rate plans.");
                if (!ratesByRoom.TryGetValue(roomId, out var list)) ratesByRoom[roomId] = list = [];
                list.Add(rate);
            }
            var dateFilter = $"{filter}&filter[date][gte]={from:yyyy-MM-dd}&filter[date][lte]={to:yyyy-MM-dd}";
            var availability = Object(await client.ReadStagingJsonAsync("availability" + dateFilter, ct), "data");
            var restrictions = Object(await client.ReadStagingJsonAsync("restrictions" + dateFilter +
                "&filter[restrictions]=rate,min_stay_arrival,min_stay_through,max_stay,stop_sell,closed_to_arrival,closed_to_departure", ct), "data");
            foreach (var item in availability.EnumerateObject())
                if (!roomsById.ContainsKey(item.Name)) throw RemoteError("Channex returned availability outside the selected inventory.");
            foreach (var item in restrictions.EnumerateObject())
                if (!rateIds.Contains(item.Name)) throw RemoteError("Channex returned prices outside the selected inventory.");
            var importedRooms = new List<ImportedRoom>();
            foreach (var (roomId, item) in roomsById)
            {
                var attributes = Object(item, "attributes");
                var quantity = Integer(attributes, "count_of_rooms", 1, 1000);
                // default_occupancy is Channex's normal guest count, which can be lower
                // than the room's adult bed spaces. capacity describes a physical dorm,
                // so it must not become the guest limit of an individual sellable bed.
                if (JsonFields.Optional(attributes, "room_kind") == "dorm")
                    throw RemoteError("This staging property contains dormitory beds. Stayline currently imports room or rental-unit inventory only.");
                var occupancy = Integer(attributes, "occ_adults", 1, 30);
                var roomName = RequiredName(attributes);
                var inventoryDates = Object(availability, roomId);
                var inventory = new List<ImportedInventory>();
                for (var date = from; date <= to; date = date.AddDays(1))
                {
                    var value = Field(inventoryDates, date.ToString("yyyy-MM-dd"));
                    if (!value.TryGetInt32(out var capacity) || capacity < 0 || capacity > quantity)
                        throw RemoteError($"The staging availability on {date:yyyy-MM-dd} is missing or outside the room's physical inventory. Review it in Channex before importing.");
                    inventory.Add(new(date, capacity));
                }
                if (!ratesByRoom.TryGetValue(roomId, out var roomRates) || roomRates.Count == 0)
                    throw RemoteError("Every imported room type needs a rate plan. Add the missing plan in Channex before importing.");
                var importedRates = new List<ImportedRate>();
                foreach (var rate in roomRates)
                {
                    var rateId = RequiredId(rate);
                    var rateDates = Object(restrictions, rateId);
                    var days = new List<ImportedRateDay>();
                    for (var date = from; date <= to; date = date.AddDays(1))
                    {
                        var daily = Object(rateDates, date.ToString("yyyy-MM-dd"));
                        var price = Price(Field(daily, "rate"));
                        var minArrival = Integer(daily, "min_stay_arrival", 1, 365);
                        var minThrough = Integer(daily, "min_stay_through", 1, 365);
                        var maxStay = Integer(daily, "max_stay", 0, 365);
                        if (maxStay != 0 && maxStay < Math.Max(minArrival, minThrough)) throw RemoteError("A staging maximum stay is less than its minimum stay. Review the calendar in Channex before importing.");
                        days.Add(new(date, price, minArrival, minThrough, maxStay,
                            Boolean(daily, "stop_sell"), Boolean(daily, "closed_to_arrival"), Boolean(daily, "closed_to_departure")));
                    }
                    var missingPriceDates = days.Where(x => x.Rate == 0).Select(x => x.Date.ToString("yyyy-MM-dd")).ToArray();
                    var firstPositivePrice = days.Where(x => x.Rate > 0).Select(x => (decimal?)x.Rate).FirstOrDefault();
                    importedRates.Add(new(new(rateId, RequiredName(Object(rate, "attributes")), firstPositivePrice, missingPriceDates), days));
                }
                importedRooms.Add(new(new(roomId, roomName, quantity, occupancy, importedRates.Select(x => x.Preview).ToArray()), inventory, importedRates));
            }
            var warnings = SnapshotWarnings.ToList();
            foreach (var rate in importedRooms.SelectMany(x => x.Rates).Where(x => x.Preview.MissingPriceCount > 0))
                warnings.Add($"Rate plan '{rate.Preview.Name}' has {rate.Preview.MissingPriceCount} zero-price dates ({rate.Preview.MissingPriceDates[0]} to {rate.Preview.MissingPriceDates[^1]}). Enter a positive initialization price to fill only those dates in the local draft; Channex is unchanged until you separately save its mapping.");
            if (roomItems.Any(x => HasExtraGuestSpaces(Object(x, "attributes"))))
                warnings.Add("Guest limits use the room's adult bed spaces. Stayline does not yet model separate child-only beds or infant cots; review guest capacity before connecting this draft.");
            if (rateItems.Any(x => JsonFields.Optional(Object(x, "attributes"), "rate_mode") == "auto"))
                warnings.Add("Automatic rate plans were imported as their actual daily per-room prices. Channex's automatic pricing rules are not copied into Stayline; review pricing before connecting the draft.");
            var rentalTypes = new[] { "apartment", "boat", "chalet", "country_house", "farm_stay", "holiday_home", "homestay", "villa" };
            var isRental = rentalTypes.Contains(JsonFields.Optional(property, "property_type")) || JsonFields.Optional(property, "property_category") == "vacation_rental";
            var kind = isRental && importedRooms.All(x => x.Preview.Quantity == 1) ? "rental" : "hotel";
            if (isRental && kind == "hotel") warnings.Add("This rental property contains pooled inventory. It uses Stayline's hotel inventory model so its actual unit counts are preserved.");
            return new(new(id, name, currency, timeZone, importedRooms.Select(x => x.Preview).ToArray(), warnings, ImportDays), kind, from, importedRooms);
        }
        catch (HttpRequestException ex)
        {
            var status = ex.StatusCode.HasValue ? $" (HTTP {(int)ex.StatusCode.Value})" : "";
            throw RemoteError("Channex staging could not load the property snapshot" + status + ". Check the staging account access and try again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw RemoteError("Channex staging timed out while loading the property snapshot. Retry the import."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw RemoteError("Channex returned incomplete or unsupported property/calendar data. The import requires 500 days of availability, supported nonnegative per-room prices, and all supported restrictions; no local data was changed."); }
    }

    private static AppError RemoteError(string message) => new(message, 502);
    private static bool SameId(string? first, string second) => Guid.TryParse(first, out var left) && Guid.TryParse(second, out var right) && left == right;
    private static JsonElement Field(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        ? value : throw RemoteError("Channex snapshot is missing required metadata or calendar values. Complete the 500-day calendar in Channex before importing.");
    private static JsonElement Object(JsonElement root, string name)
    {
        var value = Field(root, name);
        return value.ValueKind == JsonValueKind.Object ? value : throw RemoteError("Channex returned an unsupported property/calendar structure. No data was imported.");
    }
    private static string RequiredString(JsonElement root, string name) =>
        JsonFields.Optional(root, name) is { Length: > 0 } text ? text : throw RemoteError("Channex property metadata is incomplete. Complete the property's settings before importing.");
    private static string RequiredId(JsonElement root)
    {
        if (!Guid.TryParse(RequiredString(root, "id"), out var id) || id == Guid.Empty) throw RemoteError("Channex returned an invalid inventory identifier.");
        return id.ToString("D");
    }
    private static string RequiredName(JsonElement attributes)
    {
        var value = RequiredString(attributes, "title").Trim();
        if (value.Length is < 1 or > 120) throw RemoteError("A staging property, room, or rate name exceeds Stayline's 120-character limit.");
        return value;
    }
    private static string RelationshipId(JsonElement item, string relation, string field)
    {
        var direct = JsonFields.Optional(Object(item, "attributes"), field);
        if (direct != null)
        {
            if (Guid.TryParse(direct, out var id) && id != Guid.Empty) return id.ToString("D");
            throw RemoteError("Channex returned an invalid inventory relationship.");
        }
        return RequiredId(Object(Object(Object(item, "relationships"), relation), "data"));
    }
    private static int Integer(JsonElement root, string name, int minimum, int maximum)
    {
        var value = Field(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < minimum || result > maximum)
            throw RemoteError("Channex returned inventory or restrictions outside Stayline's supported range. Review the property and calendar before importing.");
        return result;
    }
    private static bool Boolean(JsonElement root, string name)
    {
        var value = Field(root, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw RemoteError("Channex returned an unsupported booking restriction value.")
        };
    }
    private static bool HasExtraGuestSpaces(JsonElement attributes) =>
        attributes.TryGetProperty("occ_children", out var children) && children.ValueKind == JsonValueKind.Number && children.TryGetInt32(out var childCount) && childCount > 0 ||
        attributes.TryGetProperty("occ_infants", out var infants) && infants.ValueKind == JsonValueKind.Number && infants.TryGetInt32(out var infantCount) && infantCount > 0;
    private static decimal Price(JsonElement value)
    {
        decimal amount;
        // Channex represents money as a decimal string or integer minor units.
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var text)) amount = text;
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var minor)) amount = minor / 100m;
        else throw RemoteError("Channex returned a missing or unsupported per-room price. Complete its 500-day calendar before importing.");
        if (amount < 0 || amount > 1_000_000 || decimal.Round(amount, 2) != amount)
            throw RemoteError("Channex has a negative or unsupported daily price. Use nonnegative two-decimal prices of at most 1,000,000 for the full 500-day staging calendar before importing.");
        return amount;
    }

    private sealed record ImportedSnapshot(StagingImportPreview Preview, string Kind, DateOnly From, IReadOnlyList<ImportedRoom> Rooms);
    private sealed record ImportedRoom(StagingImportRoomPreview Preview, IReadOnlyList<ImportedInventory> Inventory, IReadOnlyList<ImportedRate> Rates);
    private sealed record ImportedInventory(DateOnly Date, int Capacity);
    private sealed record ImportedRate(StagingImportRatePreview Preview, IReadOnlyList<ImportedRateDay> Days);
    private sealed record ImportedRateDay(DateOnly Date, decimal Rate, int MinStayArrival, int MinStayThrough, int MaxStay,
        bool StopSell, bool ClosedToArrival, bool ClosedToDeparture);
}
