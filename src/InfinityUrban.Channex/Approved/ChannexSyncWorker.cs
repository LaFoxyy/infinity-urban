using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Stayline;

/// <summary>
/// A durable change queue: keys and field masks describe dirty state, not a frozen payload.
/// Every attempt reads the latest committed PMS state. Only its captured rows are completed.
/// </summary>
public sealed class ChannexSyncWorker(IDbContextFactory<AppDb> factory, MutationLock mutations,
    ChannexClient client, IOptions<ChannexOptions> options, ILogger<ChannexSyncWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim runGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "The Channex outbound queue pass failed; durable changes remain pending."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        if (!client.IsEnabled) return;
        await runGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var groups = await db.Outbox.AsNoTracking().Where(x => x.ProcessedAt == null)
                .Select(x => new { x.PropertyId, x.Kind }).Distinct().ToListAsync(ct);
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                await DispatchAsync(group.PropertyId, group.Kind, ct);
            }
        }
        finally { runGate.Release(); }
    }

    private async Task DispatchAsync(Guid propertyId, string kind, CancellationToken ct)
    {
        long attemptId;
        long[] snapshotIds;
        string requestJson;
        await mutations.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var gate = await db.SyncGates.SingleOrDefaultAsync(x => x.PropertyId == propertyId && x.Kind == kind, ct);
            if (gate?.NextAttemptAt > now) return;
            var pending = await db.Outbox.Where(x => x.PropertyId == propertyId && x.Kind == kind && x.ProcessedAt == null).ToListAsync(ct);
            var property = await db.Properties.AsNoTracking().SingleAsync(x => x.Id == propertyId, ct);
            var today = PmsService.Today(property);
            var expired = pending.Where(x => x.Date < today).ToList();
            if (expired.Count > 0)
            {
                foreach (var row in expired) row.ProcessedAt = now;
                db.SyncAttempts.Add(new SyncAttempt { PropertyId = propertyId, Endpoint = kind, Status = "expired",
                    ChangeCount = expired.Count, Detail = "No API request: these dirty dates are now in the property's past. Channex does not accept historical rate updates. Future changes remain queued." });
                await db.SaveChangesAsync(ct);
                pending = pending.Where(x => x.Date >= today).ToList();
            }
            if (pending.Count == 0 || pending.Min(x => x.CreatedAt).AddSeconds(Math.Clamp(options.Value.BatchSeconds, 0, 60)) > now) return;
            // Both controls are persisted, so restarting or pressing Retry cannot reset the rate limit.
            var recent = await db.SyncAttempts.AsNoTracking()
                .Where(x => x.PropertyId == propertyId && x.Endpoint == kind && x.RequestJson != "" && x.CreatedAt > now.AddMinutes(-1))
                .Select(x => x.CreatedAt).ToListAsync(ct);
            if (recent.Count >= 10 || recent.Any(x => x > now.AddSeconds(-7))) return;
            gate ??= new SyncGate { PropertyId = propertyId, Kind = kind };
            if (gate.Id == 0) db.SyncGates.Add(gate);
            snapshotIds = pending.Select(x => x.Id).ToArray();
            try
            {
                requestJson = await BuildPayloadAsync(db, propertyId, kind, pending, ct);
                if (Encoding.UTF8.GetByteCount(requestJson) >= 10_000_000)
                    throw new InvalidOperationException("This batch exceeds Channex's 10 MB limit. Reduce the requested range; this operation was not split.");
            }
            catch (InvalidOperationException ex)
            {
                gate.NextAttemptAt = DateTimeOffset.MaxValue;
                gate.Failures++;
                db.SyncAttempts.Add(new SyncAttempt { PropertyId = propertyId, Endpoint = kind, Status = "blocked", ChangeCount = pending.Count, Detail = ex.Message });
                await db.SaveChangesAsync(ct);
                return;
            }
            var attempt = new SyncAttempt { PropertyId = propertyId, Endpoint = kind, CreatedAt = now,
                Status = "sending", ChangeCount = pending.Count, RequestJson = requestJson };
            db.SyncAttempts.Add(attempt);
            gate.NextAttemptAt = now.AddSeconds(7);
            await db.SaveChangesAsync(ct);
            attemptId = attempt.Id;
        }
        finally { mutations.Gate.Release(); }

        ChannexResponse? response = null;
        string? transportError = null;
        try { response = await client.SendAriAsync(kind, requestJson, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        { transportError = ex is InvalidOperationException ? ex.Message : "The staging request failed or timed out. The latest PMS state will be rebuilt for retry."; }

        await mutations.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var attempt = await db.SyncAttempts.SingleAsync(x => x.Id == attemptId, ct);
            var gate = await db.SyncGates.SingleOrDefaultAsync(x => x.PropertyId == propertyId && x.Kind == kind, ct);
            gate ??= new SyncGate { PropertyId = propertyId, Kind = kind };
            if (gate.Id == 0) db.SyncGates.Add(gate);
            var now = DateTimeOffset.UtcNow;
            attempt.HttpStatus = response?.StatusCode;
            var parsed = ParseAriResponse(response);
            attempt.TaskIds = JsonSerializer.Serialize(parsed.TaskIds);
            var success = response?.IsSuccess == true && parsed.TaskIds.Length > 0 && !parsed.HasWarnings && !parsed.HasErrors;
            if (success)
            {
                attempt.Status = "success";
                attempt.Detail = "Accepted by Channex staging. Task IDs record API acceptance; check the Channex channel logs for downstream delivery.";
                gate.Failures = 0;
                gate.NextAttemptAt = now.AddSeconds(7);
                var rows = await db.Outbox.Where(x => snapshotIds.Contains(x.Id) && x.ProcessedAt == null).ToListAsync(ct);
                foreach (var row in rows) row.ProcessedAt = now;
            }
            else
            {
                gate.Failures++;
                var permanent = response is { StatusCode: >= 400 and < 500 } && response.StatusCode != 429;
                var warning = response?.IsSuccess == true;
                attempt.Status = warning ? "warning" : "error";
                attempt.Detail = transportError ?? (warning
                    ? "Channex returned warnings, errors, or no task IDs. Changes remain pending. Inspect the response and correct the PMS data before retrying. " + SafeDetail(response!.Body)
                    : $"HTTP {response?.StatusCode}. " + SafeDetail(response?.Body ?? "No response."));
                // Validation warnings and permanent client errors need a human correction, not a retry loop.
                gate.NextAttemptAt = permanent || warning ? DateTimeOffset.MaxValue : now + RetryDelay(gate.Failures, response?.RetryAfter);
                if (!permanent && !warning)
                {
                    // Channex asks for a property-wide quiet period on network/server/rate-limit
                    // errors. Availability and restrictions retain their own normal quotas.
                    var otherKind = kind == "availability" ? "restrictions" : "availability";
                    var otherGate = await db.SyncGates.SingleOrDefaultAsync(x => x.PropertyId == propertyId && x.Kind == otherKind, ct);
                    if (otherGate is null)
                        db.SyncGates.Add(new SyncGate { PropertyId = propertyId, Kind = otherKind, NextAttemptAt = gate.NextAttemptAt });
                    else if (otherGate.NextAttemptAt < gate.NextAttemptAt)
                        otherGate.NextAttemptAt = gate.NextAttemptAt;
                }
            }
            await db.SaveChangesAsync(ct);
        }
        finally { mutations.Gate.Release(); }
    }

    public static TimeSpan RetryDelay(int failures, TimeSpan? serverDelay = null)
    {
        var seconds = Math.Min(3600, 60 * Math.Pow(2, Math.Clamp(failures - 1, 0, 6)));
        if (serverDelay is { } retry) seconds = Math.Max(seconds, retry.TotalSeconds);
        return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble() * 10);
    }

    private string SafeDetail(string value)
    {
        // Error responses can echo request data. Remove configured credentials before
        // persisting text that is later displayed in synchronization history.
        foreach (var secret in new[] { options.Value.ApiKey, options.Value.WebhookSecret }
            .Where(x => !string.IsNullOrEmpty(x)).OrderByDescending(x => x.Length))
            value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value.Length > 6000 ? value[..6000] + "…" : value;
    }

    public static (string[] TaskIds, bool HasWarnings, bool HasErrors) ParseAriResponse(ChannexResponse? response)
    {
        if (response is null) return ([], false, true);
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], false, true);
            var taskIds = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Where(x => JsonFields.Optional(x, "type") == "task")
                    .Select(x => JsonFields.Optional(x, "id")).Where(x => Guid.TryParse(x, out _)).Cast<string>().Distinct().ToArray()
                : [];
            var warnings = root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("warnings", out var w) &&
                w.ValueKind != JsonValueKind.Null && (w.ValueKind != JsonValueKind.Array || w.GetArrayLength() != 0);
            var errors = root.TryGetProperty("errors", out var e) && e.ValueKind != JsonValueKind.Null;
            return (taskIds, warnings, errors);
        }
        catch (JsonException) { return ([], false, true); }
    }

    public static async Task<string> BuildPayloadAsync(AppDb db, Guid propertyId, string kind,
        IReadOnlyCollection<OutboxChange> pending, CancellationToken ct = default)
    {
        var property = await db.Properties.AsNoTracking().SingleAsync(x => x.Id == propertyId, ct);
        if (!Guid.TryParse(property.ChannexId, out _)) throw new InvalidOperationException("Map this property's staging Channex ID before synchronization.");
        var rooms = await db.RoomTypes.AsNoTracking().Where(x => x.PropertyId == propertyId).ToDictionaryAsync(x => x.Id, ct);
        var days = new List<PayloadDay>();
        var keys = pending.GroupBy(x => (x.EntityId, x.Date))
            .Select(group => new
            {
                group.Key.EntityId,
                group.Key.Date,
                // Old rows and full-sync rows carry zero, which explicitly means every field.
                Fields = group.Any(x => x.ChangedFields == 0) ? AriFields.AllRestrictions : group.Aggregate(0, (mask, row) => mask | row.ChangedFields)
            })
            .OrderBy(x => x.EntityId).ThenBy(x => x.Date).ToArray();
        if (kind == "availability")
        {
            var inventory = await db.DailyInventories.AsNoTracking().Where(x => rooms.Keys.Contains(x.RoomTypeId)).ToListAsync(ct);
            var overrides = inventory.ToDictionary(x => (x.RoomTypeId, x.Date));
            var reservations = await db.Reservations.AsNoTracking().Include(x => x.Stays)
                .Where(x => x.PropertyId == propertyId && x.Status != "cancelled").ToListAsync(ct);
            foreach (var key in keys)
            {
                if (!rooms.TryGetValue(key.EntityId, out var room) || !Guid.TryParse(room.ChannexId, out _))
                    throw new InvalidOperationException("Every pending room type must be mapped to Channex before synchronization.");
                var capacity = overrides.GetValueOrDefault((room.Id, key.Date))?.Capacity ?? room.Quantity;
                var occupied = reservations.Sum(r => r.Stays.Where(s => s.RoomTypeId == room.Id &&
                    (s.Arrival ?? r.Arrival) <= key.Date && (s.Departure ?? r.Departure) > key.Date).Sum(s => s.Quantity));
                var value = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["property_id"] = property.ChannexId,
                    ["room_type_id"] = room.ChannexId!,
                    ["availability"] = Math.Max(0, capacity - occupied)
                };
                days.Add(new PayloadDay(key.EntityId, key.Date, 0, value));
            }
        }
        else if (kind == "restrictions")
        {
            var plans = await db.RatePlans.AsNoTracking().Where(x => rooms.Keys.Contains(x.RoomTypeId)).ToDictionaryAsync(x => x.Id, ct);
            var daily = await db.DailyRates.AsNoTracking().Where(x => plans.Keys.Contains(x.RatePlanId)).ToListAsync(ct);
            var overrides = daily.ToDictionary(x => (x.RatePlanId, x.Date));
            foreach (var key in keys)
            {
                if (!plans.TryGetValue(key.EntityId, out var rate) || !Guid.TryParse(rate.ChannexId, out _))
                    throw new InvalidOperationException("Every pending rate plan must be mapped to Channex before synchronization.");
                if (key.Fields <= 0 || (key.Fields & ~AriFields.AllRestrictions) != 0)
                    throw new InvalidOperationException("A pending restriction change has an unknown field mask. Review the queued change before retrying.");
                var day = overrides.GetValueOrDefault((rate.Id, key.Date));
                var value = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["property_id"] = property.ChannexId,
                    ["rate_plan_id"] = rate.ChannexId!
                };
                if ((key.Fields & AriFields.Rate) != 0)
                {
                    var price = day?.Rate ?? rate.BasePrice;
                    if (price <= 0) throw new InvalidOperationException("Channex requires a positive saved price for every synchronized date.");
                    value["rate"] = price.ToString("0.00", CultureInfo.InvariantCulture);
                }
                if ((key.Fields & AriFields.MinStayArrival) != 0) value["min_stay_arrival"] = day?.MinStayArrival ?? 1;
                if ((key.Fields & AriFields.MinStayThrough) != 0) value["min_stay_through"] = day?.MinStayThrough ?? 1;
                if ((key.Fields & AriFields.MaxStay) != 0) value["max_stay"] = day?.MaxStay ?? 0;
                if ((key.Fields & AriFields.StopSell) != 0) value["stop_sell"] = day?.StopSell ?? false;
                if ((key.Fields & AriFields.ClosedToArrival) != 0) value["closed_to_arrival"] = day?.ClosedToArrival ?? false;
                if ((key.Fields & AriFields.ClosedToDeparture) != 0) value["closed_to_departure"] = day?.ClosedToDeparture ?? false;
                days.Add(new PayloadDay(key.EntityId, key.Date, key.Fields, value));
            }
        }
        else throw new InvalidOperationException("Unknown pending synchronization kind.");
        return JsonSerializer.Serialize(new { values = MergeDateRanges(days) });
    }

    private sealed record PayloadDay(Guid EntityId, DateOnly Date, int Fields, Dictionary<string, object> Value)
    {
        // Value insertion order is fixed above, so equal selected fields have equal signatures.
        public string Signature { get; } = JsonSerializer.Serialize(Value);
    }

    private static List<Dictionary<string, object>> MergeDateRanges(IReadOnlyList<PayloadDay> days)
    {
        var values = new List<Dictionary<string, object>>();
        if (days.Count == 0) return values;
        var first = days[0];
        var last = first;
        foreach (var next in days.Skip(1))
        {
            if (next.EntityId == last.EntityId && next.Fields == last.Fields &&
                next.Date.DayNumber == last.Date.DayNumber + 1 && next.Signature == last.Signature)
            {
                last = next;
                continue;
            }
            AddRange(first, last);
            first = last = next;
        }
        AddRange(first, last);
        return values;

        void AddRange(PayloadDay start, PayloadDay end)
        {
            var value = new Dictionary<string, object>(start.Value, StringComparer.Ordinal);
            if (start.Date == end.Date)
                value["date"] = start.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else
            {
                value["date_from"] = start.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                value["date_to"] = end.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            values.Add(value);
        }
    }
}
