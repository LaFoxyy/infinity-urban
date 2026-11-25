using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Stayline;

public static class PmsEndpoints
{
    public static void MapPmsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/status", async (IDbContextFactory<AppDb> factory, IOptions<ChannexOptions> options, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var configured = options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ApiKey);
            return new { name = "Stayline", integrationVersion = "2026.10.29", environment = options.Value.EnvironmentName, connected = configured, configured, enabled = options.Value.Enabled,
                webhookConfigured = !string.IsNullOrWhiteSpace(options.Value.WebhookSecret) && options.Value.WebhookSecret.Length >= 16,
                pendingChanges = await db.Outbox.CountAsync(x => x.ProcessedAt == null, ct),
                pendingBookings = await db.BookingReceipts.CountAsync(x => x.AckedAt == null, ct),
                today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "America/Sao_Paulo").DateTime).ToString("yyyy-MM-dd") };
        });
        app.MapGet("/api/properties", async (IDbContextFactory<AppDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var properties = await db.Properties.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
            var rooms = await db.RoomTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
            var rates = await db.RatePlans.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
            var imports = await db.SyncAttempts.AsNoTracking().Where(x => x.Endpoint == "import" && x.Status == "imported").OrderByDescending(x => x.Id).ToListAsync(ct);
            var suggestions = new Dictionary<Guid, string>();
            var roomSuggestions = new Dictionary<Guid, string>();
            var rateSuggestions = new Dictionary<Guid, string>();
            foreach (var imported in imports)
            {
                if (suggestions.ContainsKey(imported.PropertyId)) continue;
                try
                {
                    using var json = JsonDocument.Parse(imported.RequestJson);
                    if (json.RootElement.TryGetProperty("externalPropertyId", out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var remoteId))
                        suggestions.Add(imported.PropertyId, remoteId.ToString());
                    ReadSuggestions(json.RootElement, "roomMappings", roomSuggestions);
                    ReadSuggestions(json.RootElement, "rateMappings", rateSuggestions);
                }
                catch (JsonException) { /* An older audit entry must not prevent loading properties. */ }
            }
            return properties.Select(p => new { p.Id, p.Name, p.Kind, p.Currency, p.TimeZoneId, p.ChannexId, suggestedChannexId = suggestions.GetValueOrDefault(p.Id),
                roomTypes = rooms.Where(r => r.PropertyId == p.Id).Select(r => new { r.Id, r.PropertyId, r.Name, r.Quantity, r.Occupancy, r.ChannexId, suggestedChannexId = roomSuggestions.GetValueOrDefault(r.Id),
                    ratePlans = rates.Where(x => x.RoomTypeId == r.Id).Select(x => new { x.Id, x.RoomTypeId, x.Name, x.BasePrice, x.ChannexId, suggestedChannexId = rateSuggestions.GetValueOrDefault(x.Id) }) }) });
        });
        app.MapPost("/api/properties", async (CreatePropertyInput input, PmsService service, CancellationToken ct) => Results.Ok(await service.CreatePropertyAsync(input, ct)));
        app.MapPost("/api/properties/{id:guid}/rooms", async (Guid id, CreateRoomInput input, PmsService service, CancellationToken ct) => Results.Ok(await service.CreateRoomAsync(id, input, ct)));
        app.MapPost("/api/rooms/{id:guid}/rates", async (Guid id, CreateRateInput input, PmsService service, CancellationToken ct) => Results.Ok(await service.CreateRateAsync(id, input, ct)));
        app.MapGet("/api/properties/{id:guid}/calendar", CalendarAsync);
        app.MapPost("/api/properties/{id:guid}/calendar", async (Guid id, CalendarInput input, PmsService service, CancellationToken ct) => Results.Ok(new { savedChanges = await service.SaveCalendarAsync(id, input, ct) }));
        app.MapGet("/api/properties/{id:guid}/reservations", async (Guid id, IDbContextFactory<AppDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await PmsService.PropertyAsync(db, id, ct);
            return await db.Reservations.Include(x => x.Stays).Where(x => x.PropertyId == id).OrderByDescending(x => x.UpdatedAt).Take(500).ToListAsync(ct);
        });
        app.MapPost("/api/properties/{id:guid}/reservations", async (Guid id, ReservationInput input, PmsService service, CancellationToken ct) => Results.Ok(await service.SaveReservationAsync(id, null, input, ct)));
        app.MapPut("/api/reservations/{id:guid}", async (Guid id, ReservationInput input, IDbContextFactory<AppDb> factory, PmsService service, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var propertyId = await db.Reservations.Where(x => x.Id == id).Select(x => (Guid?)x.PropertyId).SingleOrDefaultAsync(ct) ?? throw new AppError("Reservation not found.", 404);
            return Results.Ok(await service.SaveReservationAsync(propertyId, id, input, ct));
        });
        app.MapDelete("/api/reservations/{id:guid}", async (Guid id, PmsService service, CancellationToken ct) => Results.Ok(new { cancelled = await service.CancelReservationAsync(id, ct) }));
        app.MapPut("/api/properties/{id:guid}/mapping", MappingAsync);
        app.MapPost("/api/properties/{id:guid}/full-sync", async (Guid id, PmsService service, IOptions<ChannexOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.ApiKey))
                throw new AppError("Configure and enable Channex staging before sending a full sync.", 409);
            return Results.Ok(new { queued = await service.QueueFullSyncAsync(id, ct), days = 500 });
        });
        app.MapPost("/api/properties/{id:guid}/retry", RetryAsync);
        app.MapGet("/api/properties/{id:guid}/sync", SyncAsync);
        app.MapPost("/api/channex/bookings/check", async (BookingInboxWorker worker, ChannexClient client,
            IDbContextFactory<AppDb> factory, CancellationToken ct) =>
        {
            if (!client.IsEnabled) throw new AppError("Configure and enable Channex staging before checking incoming bookings.", 409);
            await worker.CheckFeedAsync(ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(new { checkedFeed = true, received = await db.BookingReceipts.CountAsync(ct),
                acknowledged = await db.BookingReceipts.CountAsync(x => x.AckedAt != null, ct),
                pending = await db.BookingReceipts.CountAsync(x => x.AckedAt == null, ct) });
        });
    }

    private static async Task<object> CalendarAsync(Guid id, DateOnly? from, int? days, IDbContextFactory<AppDb> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var property = await PmsService.PropertyAsync(db, id, ct);
        var start = from ?? PmsService.Today(property);
        var length = days ?? 14;
        PmsService.Require(length is >= 1 and <= 60, "Display between 1 and 60 days.");
        PmsService.Require(start.Year >= 2000 && start.Year <= 2100, "Choose a date from 2000 to 2100.");
        var end = start.AddDays(length);
        var rooms = await db.RoomTypes.Where(x => x.PropertyId == id).OrderBy(x => x.Name).ToListAsync(ct);
        var roomIds = rooms.Select(x => x.Id).ToList();
        var rates = await db.RatePlans.Where(x => roomIds.Contains(x.RoomTypeId)).OrderBy(x => x.Name).ToListAsync(ct);
        var rateIds = rates.Select(x => x.Id).ToList();
        var dailyRates = await db.DailyRates.Where(x => rateIds.Contains(x.RatePlanId) && x.Date >= start && x.Date < end).ToListAsync(ct);
        var inventory = await db.DailyInventories.Where(x => roomIds.Contains(x.RoomTypeId) && x.Date >= start && x.Date < end).ToListAsync(ct);
        var reservations = await PmsService.ActiveReservationsAsync(db, id, ct);
        var dates = Enumerable.Range(0, length).Select(start.AddDays).ToArray();
        return new { from = start, days = length, dates, rooms = rooms.Select(room => new
        {
            room.Id, room.Name, room.Quantity, room.Occupancy,
            days = dates.Select(date =>
            {
                var capacity = inventory.SingleOrDefault(x => x.RoomTypeId == room.Id && x.Date == date)?.Capacity ?? room.Quantity;
                var booked = PmsService.Booked(reservations, room.Id, date);
                return new { date, capacity, booked, available = Math.Max(0, capacity - booked) };
            }),
            ratePlans = rates.Where(rate => rate.RoomTypeId == room.Id).Select(rate => new { rate.Id, rate.Name, days = dates.Select(date =>
            {
                var row = dailyRates.SingleOrDefault(x => x.RatePlanId == rate.Id && x.Date == date) ?? new DailyRate { Rate = rate.BasePrice };
                return new { date, rate = row.Rate, row.MinStayArrival, row.MinStayThrough, row.MaxStay, row.StopSell, row.ClosedToArrival, row.ClosedToDeparture };
            }) })
        }) };
    }

    private static async Task<IResult> MappingAsync(Guid id, MappingInput input, ChannexClient client, IDbContextFactory<AppDb> factory, MutationLock mutation, CancellationToken ct)
    {
        PmsService.Require(input.Rooms != null && input.Rates != null, "Include room and rate mappings.");
        var externalId = NormalizeUuid(input.ChannexId);
        if (externalId != null && !client.IsEnabled) throw new AppError("Configure the staging API key and enable Channex on the server before mapping.", 409);
        var remote = externalId == null ? null : await client.FetchInventoryAsync(externalId, ct);
        await mutation.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var property = await PmsService.PropertyAsync(db, id, ct);
            var rooms = await db.RoomTypes.Where(x => x.PropertyId == id).ToListAsync(ct);
            var roomIds = rooms.Select(x => x.Id).ToList();
            var rates = await db.RatePlans.Where(x => roomIds.Contains(x.RoomTypeId)).ToListAsync(ct);
            PmsService.Require(input.Rooms!.Select(x => x.Id).Distinct().Count() == rooms.Count && input.Rooms.Count == rooms.Count && input.Rooms.All(x => roomIds.Contains(x.Id)), "Include each of this property's room types once.");
            PmsService.Require(input.Rates!.Select(x => x.Id).Distinct().Count() == rates.Count && input.Rates.Count == rates.Count && input.Rates.All(x => rates.Any(r => r.Id == x.Id)), "Include each of this property's rate plans once.");
            if (remote != null)
            {
                var remoteProperty = remote.Properties.SingleOrDefault(x => x.Id == externalId) ?? throw new AppError("The staging key cannot access that property.");
                PmsService.Require(remoteProperty.Currency == property.Currency, $"Currency mismatch: this PMS property uses {property.Currency}, while the Channex property uses {remoteProperty.Currency}. Import the staging property as a separate matching-currency property, or choose a Channex property using {property.Currency}.");
                PmsService.Require(rooms.Count > 0 && rates.Count > 0, "Add room types and rate plans, or import this staging property, before saving a mapping.");
                PmsService.Require(rooms.All(room => rates.Any(rate => rate.RoomTypeId == room.Id)), "Every room type must have at least one rate plan before mapping.");
                PmsService.Require(input.Rooms.All(x => !string.IsNullOrWhiteSpace(x.ChannexId)) && input.Rates.All(x => !string.IsNullOrWhiteSpace(x.ChannexId)), "Choose a Channex mapping for every room type and rate plan before connecting.");
            }
            property.ChannexId = externalId;
            foreach (var room in rooms)
            {
                var mappedId = externalId == null ? null : NormalizeUuid(input.Rooms.Single(x => x.Id == room.Id).ChannexId);
                PmsService.Require(mappedId == null || remote!.RoomTypes.Any(x => x.Id == mappedId && x.PropertyId == externalId), "A room mapping belongs to another Channex property.");
                room.ChannexId = mappedId;
            }
            foreach (var rate in rates)
            {
                var mappedId = externalId == null ? null : NormalizeUuid(input.Rates.Single(x => x.Id == rate.Id).ChannexId);
                var room = rooms.Single(x => x.Id == rate.RoomTypeId);
                PmsService.Require(mappedId == null || remote!.RatePlans.Any(x => x.Id == mappedId && x.RoomTypeId == room.ChannexId), "A rate mapping must belong to its mapped Channex room.");
                rate.ChannexId = mappedId;
            }
            PmsService.Require(rooms.Where(x => x.ChannexId != null).Select(x => x.ChannexId).Distinct().Count() == rooms.Count(x => x.ChannexId != null), "Map each Channex room only once.");
            PmsService.Require(rates.Where(x => x.ChannexId != null).Select(x => x.ChannexId).Distinct().Count() == rates.Count(x => x.ChannexId != null), "Map each Channex rate plan only once.");
            db.SyncGates.RemoveRange(await db.SyncGates.Where(x => x.PropertyId == id && x.NextAttemptAt == DateTimeOffset.MaxValue).ToListAsync(ct));
            var queued = externalId != null && rooms.Count > 0 && rates.Count > 0;
            if (queued) await PmsService.FullSyncAsync(db, property, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { saved = true, fullSyncQueued = queued });
        }
        finally { mutation.Gate.Release(); }
    }

    private static async Task<IResult> RetryAsync(Guid id, IDbContextFactory<AppDb> factory, MutationLock mutation, CancellationToken ct)
    {
        await mutation.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await PmsService.PropertyAsync(db, id, ct);
            db.SyncGates.RemoveRange(await db.SyncGates.Where(x => x.PropertyId == id && x.NextAttemptAt == DateTimeOffset.MaxValue).ToListAsync(ct));
            var receipts = await db.BookingReceipts.Where(x => x.AckedAt == null && (x.PropertyId == id || x.PropertyId == null)).ToListAsync(ct);
            foreach (var receipt in receipts) receipt.NextAttemptAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { retrying = true });
        }
        finally { mutation.Gate.Release(); }
    }

    private static async Task<object> SyncAsync(Guid id, IDbContextFactory<AppDb> factory, IOptions<ChannexOptions> options, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var property = await PmsService.PropertyAsync(db, id, ct);
        var attempts = await db.SyncAttempts.Where(x => x.PropertyId == id).OrderByDescending(x => x.Id).Take(50).ToListAsync(ct);
        return new { readiness = await SyncReadiness.CheckAsync(db, property, options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ApiKey), ct), pending = await db.Outbox.CountAsync(x => x.PropertyId == id && x.ProcessedAt == null, ct),
            attempts = attempts.Select(x => new { x.Id, x.Endpoint, x.CreatedAt, x.Status, x.HttpStatus, taskIds = JsonSerializer.Deserialize<string[]>(x.TaskIds), x.ChangeCount, x.Detail,
                requestJson = x.RequestJson.Length > 24000 ? x.RequestJson[..24000] + "\n… (full request retained in SQLite)" : x.RequestJson }),
            receipts = await db.BookingReceipts.Where(x => x.PropertyId == id || x.PropertyId == null).OrderByDescending(x => x.ReceivedAt)
                .Take(50).Select(x => new { x.Id, x.PropertyId, x.ExternalBookingId, x.RevisionStatus, x.Source, x.ReceivedAt,
                    x.ProcessedAt, x.AckedAt, x.Error, x.Attempts, x.NextAttemptAt }).ToListAsync(ct) };
    }
    private static string? NormalizeUuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Guid.TryParse(value, out var uuid) || uuid == Guid.Empty) throw new AppError("Channex mappings must use valid UUIDs loaded from staging.");
        return uuid.ToString();
    }
    private static void ReadSuggestions(JsonElement source, string name, Dictionary<Guid, string> target)
    {
        if (!source.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("localId", out var local) || !item.TryGetProperty("externalId", out var remote)) continue;
            if (local.ValueKind == JsonValueKind.String && remote.ValueKind == JsonValueKind.String && Guid.TryParse(local.GetString(), out var localId) && Guid.TryParse(remote.GetString(), out var remoteId))
                target.TryAdd(localId, remoteId.ToString());
        }
    }
}
