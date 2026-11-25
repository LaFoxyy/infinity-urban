using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Stayline;

/// <summary>
/// Persist complete authoritative feed revisions, commit their effects, then acknowledge them.
/// A failed acknowledgment is retried without applying the reservation twice.
/// </summary>
public sealed class BookingInboxWorker(IDbContextFactory<AppDb> factory, MutationLock mutations,
    ChannexClient client, IOptions<ChannexOptions> options, ILogger<BookingInboxWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim runGate = new(1, 1);
    private DateTimeOffset nextFeedPoll = DateTimeOffset.MinValue;
    private int feedRefreshRequested;

    /// <summary>Authenticated webhooks wake the regular feed consumer; webhook bodies are not booking data.</summary>
    public void RequestFeedRefresh() => Interlocked.Exchange(ref feedRefreshRequested, 1);

    /// <summary>A normal recovery action uses the same durable receive/apply/ack path as the background worker.</summary>
    public async Task CheckFeedAsync(CancellationToken ct = default)
    {
        await PollFeedAsync(ct);
        await ProcessInboxAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled && client.IsEnabled)
            {
                var requested = Interlocked.Exchange(ref feedRefreshRequested, 0) == 1;
                if (requested || DateTimeOffset.UtcNow >= nextFeedPoll)
                {
                    try { await PollFeedCoreAsync(requested ? "webhook/feed" : "feed", stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        nextFeedPoll = DateTimeOffset.UtcNow.AddMinutes(1);
                        logger.LogError("Channex booking feed polling failed ({Type}). Check the staging key and connection. Retrying in one minute.", ex.GetType().Name);
                    }
                }
                try { await ProcessInboxAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError("Channex booking inbox pass failed ({Type}); receipts remain durable.", ex.GetType().Name); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public Task PollFeedAsync(CancellationToken ct = default) => PollFeedCoreAsync("feed", ct);

    private async Task PollFeedCoreAsync(string source, CancellationToken ct)
    {
        if (!client.IsEnabled) return;
        // The same gate also protects inbox acknowledgments. The unacknowledged feed therefore
        // cannot shrink under our own pagination, even when a recovery request overlaps a worker pass.
        await runGate.WaitAsync(ct);
        try
        {
            var items = await client.FetchBookingFeedAsync(ct);
            var snapshots = items.Select(item => BookingFeedSnapshot.FromFeedItem(item)).ToArray();
            var ids = snapshots.Select(x => x.RevisionId).Distinct().ToArray();
            await mutations.Gate.WaitAsync(ct);
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var existing = await db.BookingReceipts.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
                foreach (var snapshot in snapshots)
                {
                    if (!existing.TryGetValue(snapshot.RevisionId, out var receipt))
                    {
                        receipt = new BookingReceipt { Id = snapshot.RevisionId };
                        db.BookingReceipts.Add(receipt);
                        existing.Add(receipt.Id, receipt);
                    }
                    // Do not retroactively relabel an old, already processed import as feed evidence.
                    if (receipt.ProcessedAt is not null) continue;
                    var newlyHydrated = string.IsNullOrWhiteSpace(receipt.PayloadJson);
                    receipt.PayloadJson = snapshot.PayloadJson;
                    receipt.ExternalBookingId = snapshot.BookingId;
                    receipt.RevisionStatus = snapshot.Status;
                    receipt.Source = source;
                    if (newlyHydrated)
                    {
                        receipt.NextAttemptAt = DateTimeOffset.UtcNow;
                        receipt.Error = null;
                    }
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            finally { mutations.Gate.Release(); }
            nextFeedPoll = DateTimeOffset.UtcNow.AddMinutes(15);
        }
        catch
        {
            nextFeedPoll = DateTimeOffset.UtcNow.AddMinutes(1);
            throw;
        }
        finally { runGate.Release(); }
    }

    public async Task ProcessInboxAsync(CancellationToken ct = default)
    {
        if (!client.IsEnabled) return;
        await runGate.WaitAsync(ct);
        try
        {
            await using var readDb = await factory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var pending = await readDb.BookingReceipts.AsNoTracking().Where(x => x.AckedAt == null && x.NextAttemptAt <= now)
                .OrderBy(x => x.ReceivedAt).Take(100).ToListAsync(ct);
            foreach (var receipt in pending)
            {
                try
                {
                    if (receipt.ProcessedAt is null)
                    {
                        if (string.IsNullOrWhiteSpace(receipt.PayloadJson))
                            throw new InvalidOperationException("Waiting for the complete revision from the Channex booking feed. Check for incoming bookings; no acknowledgment has been sent.");
                        using var revision = JsonDocument.Parse(receipt.PayloadJson);
                        await ApplyRevisionAsync(receipt.Id, revision.RootElement, ct);
                    }
                    // ApplyRevision commits ProcessedAt atomically with reservation/inventory changes.
                    await client.AckRevisionAsync(receipt.Id, ct);
                    await UpdateReceiptAsync(receipt.Id, true, null, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var message = ex switch
                    {
                        HttpRequestException request => $"Channex staging request failed (HTTP {(int?)request.StatusCode}). Reservation acknowledgment will retry after persistence succeeds.",
                        InvalidOperationException invalid => invalid.Message,
                        JsonException => "Channex returned an invalid booking response; this revision was not acknowledged.",
                        _ => "Booking processing failed. The revision remains unacknowledged; check the server connection and database."
                    };
                    await UpdateReceiptAsync(receipt.Id, false, message, ct);
                    logger.LogWarning("Booking revision {RevisionId} remains unacknowledged: {Reason}", receipt.Id, message);
                }
            }
        }
        finally { runGate.Release(); }
    }

    private async Task UpdateReceiptAsync(string id, bool acked, string? error, CancellationToken ct)
    {
        await mutations.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var receipt = await db.BookingReceipts.SingleAsync(x => x.Id == id, ct);
            receipt.Attempts++;
            receipt.Error = error;
            if (acked)
            {
                receipt.AckedAt = DateTimeOffset.UtcNow;
                // Operational data has its own persisted reservation. Retain receipt IDs/status,
                // not a second guest snapshot after successful processing and acknowledgment.
                receipt.PayloadJson = null;
            }
            else receipt.NextAttemptAt = DateTimeOffset.UtcNow + ChannexSyncWorker.RetryDelay(receipt.Attempts);
            await db.SaveChangesAsync(ct);
        }
        finally { mutations.Gate.Release(); }
    }

    public async Task ApplyRevisionAsync(string revisionId, JsonElement attributes, CancellationToken ct = default)
    {
        if (!Guid.TryParse(revisionId, out var revisionGuid)) throw new InvalidOperationException("Booking revision ID is invalid.");
        revisionId = revisionGuid.ToString("D");
        var payloadId = JsonFields.Optional(attributes, "id");
        if (payloadId is not null && (!Guid.TryParse(payloadId, out var payloadGuid) || payloadGuid != revisionGuid))
            throw new InvalidOperationException("Booking revision attributes have a mismatched ID.");
        var externalProperty = CanonicalUuid(JsonFields.Required(attributes, "property_id"), "property");
        var externalBooking = CanonicalUuid(JsonFields.Required(attributes, "booking_id"), "booking");
        var status = JsonFields.Required(attributes, "status");
        if (status is not ("new" or "modified" or "cancelled"))
            throw new InvalidOperationException("This booking revision has an unsupported status; it remains unacknowledged.");
        if (!DateTimeOffset.TryParse(JsonFields.Required(attributes, "inserted_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var externalUpdatedAt))
            throw new InvalidOperationException("This revision has no usable inserted_at timestamp; it cannot be ordered safely.");

        await mutations.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var receipt = await db.BookingReceipts.SingleOrDefaultAsync(x => x.Id == revisionId, ct);
            if (receipt?.ProcessedAt is not null) return;
            receipt ??= new BookingReceipt { Id = revisionId };
            if (db.Entry(receipt).State == EntityState.Detached) db.BookingReceipts.Add(receipt);
            receipt.ExternalBookingId = externalBooking;
            receipt.RevisionStatus = status;
            var properties = await db.Properties.Where(x => x.ChannexId != null).ToListAsync(ct);
            var propertyMatches = properties.Where(x => SameUuid(x.ChannexId, externalProperty)).ToList();
            if (propertyMatches.Count != 1) throw new InvalidOperationException("The booking property mapping is missing or ambiguous. Correct mapping, then retry the inbox.");
            var property = propertyMatches[0];
            receipt.PropertyId = property.Id;
            var reservation = await db.Reservations.Include(x => x.Stays).SingleOrDefaultAsync(x => x.ExternalBookingId == externalBooking, ct);
            if (reservation is not null && reservation.PropertyId != property.Id)
                throw new InvalidOperationException("This external booking ID belongs to another local property. Resolve its mapping before acknowledgment.");
            if (reservation?.ExternalUpdatedAt is { } savedTimestamp && savedTimestamp == externalUpdatedAt)
                throw new InvalidOperationException("A different booking revision has the same timestamp as the saved revision. Its order is ambiguous; review it before acknowledgment.");
            if (reservation?.ExternalUpdatedAt is { } latest && latest > externalUpdatedAt)
            {
                // Duplicate or older revision: its newer saved state already determines availability.
                receipt.ProcessedAt = DateTimeOffset.UtcNow;
                receipt.Error = null;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return;
            }

            var arrival = ParseDate(attributes, "arrival_date");
            var departure = ParseDate(attributes, "departure_date");
            ValidateStay(arrival, departure);
            var mappedStays = new List<ReservationStay>();
            if (status != "cancelled")
            {
                if (!attributes.TryGetProperty("rooms", out var roomList) || roomList.ValueKind != JsonValueKind.Array || roomList.GetArrayLength() == 0)
                    throw new InvalidOperationException("This active booking has no rooms. It remains unacknowledged.");
                var rooms = await db.RoomTypes.Where(x => x.PropertyId == property.Id).ToListAsync(ct);
                var roomIds = rooms.Select(x => x.Id).ToArray();
                var plans = await db.RatePlans.Where(x => roomIds.Contains(x.RoomTypeId)).ToListAsync(ct);
                foreach (var item in roomList.EnumerateArray())
                {
                    var roomId = CanonicalUuid(JsonFields.Required(item, "room_type_id"), "room type");
                    var planId = CanonicalUuid(JsonFields.Required(item, "rate_plan_id"), "rate plan");
                    var roomMatches = rooms.Where(x => SameUuid(x.ChannexId, roomId)).ToList();
                    var planMatches = plans.Where(x => SameUuid(x.ChannexId, planId)).ToList();
                    if (roomMatches.Count != 1 || planMatches.Count != 1 || planMatches[0].RoomTypeId != roomMatches[0].Id)
                        throw new InvalidOperationException("A booking room or rate mapping is missing, ambiguous, or belongs to a different room. Correct mapping before retrying.");
                    var checkin = ParseDate(item, "checkin_date");
                    var checkout = ParseDate(item, "checkout_date");
                    ValidateStay(checkin, checkout);
                    if (checkin < arrival || checkout > departure)
                        throw new InvalidOperationException("A room stay falls outside the booking dates. Review the revision before acknowledgment.");
                    // Channex Booking Room is one booked unit. Multiple units are separate room entries.
                    mappedStays.Add(new ReservationStay { RoomTypeId = roomMatches[0].Id, RatePlanId = planMatches[0].Id,
                        Arrival = checkin, Departure = checkout, Quantity = 1 });
                }
            }

            if (reservation is null)
            {
                reservation = new Reservation { PropertyId = property.Id, ExternalBookingId = externalBooking, Source = "channex" };
                db.Reservations.Add(reservation);
            }
            else
            {
                EnqueueOccupiedNights(db, property, reservation);
                if (status != "cancelled")
                {
                    db.ReservationStays.RemoveRange(reservation.Stays);
                    reservation.Stays.Clear();
                }
            }
            reservation.Arrival = arrival;
            reservation.Departure = departure;
            reservation.Status = status == "cancelled" ? "cancelled" : "confirmed";
            reservation.ExternalUpdatedAt = externalUpdatedAt;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;
            if (attributes.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object)
                reservation.GuestName = string.Join(" ", new[] { JsonFields.Optional(customer, "name"), JsonFields.Optional(customer, "surname") }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(reservation.GuestName)) reservation.GuestName = "Channel guest";
            // Only operational fields are copied. Card guarantees, raw JSON and payment data are never persisted.
            foreach (var stay in mappedStays)
            {
                stay.ReservationId = reservation.Id;
                reservation.Stays.Add(stay);
                // A GUID is assigned before EF sees the entity. Explicit Add keeps a new stay
                // Added when replacing the collection on an already tracked reservation.
                db.ReservationStays.Add(stay);
            }
            EnqueueOccupiedNights(db, property, reservation);
            receipt.ProcessedAt = DateTimeOffset.UtcNow;
            receipt.Error = null;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally { mutations.Gate.Release(); }
    }

    private static void EnqueueOccupiedNights(AppDb db, Property property, Reservation reservation)
    {
        var today = PmsService.Today(property);
        foreach (var stay in reservation.Stays)
        {
            var from = stay.Arrival ?? reservation.Arrival;
            var to = stay.Departure ?? reservation.Departure;
            // Historical booking revisions still import, but past dates cannot be sold again.
            PmsService.Enqueue(db, property.Id, "availability", stay.RoomTypeId, from < today ? today : from, to);
        }
    }
    private static bool SameUuid(string? left, string right) => Guid.TryParse(left, out var a) && Guid.TryParse(right, out var b) && a == b;
    private static string CanonicalUuid(string text, string label) => Guid.TryParse(text, out var id) ? id.ToString("D") : throw new InvalidOperationException($"The Channex {label} UUID is invalid or unmapped.");
    private static DateOnly ParseDate(JsonElement element, string field) => DateOnly.TryParseExact(JsonFields.Required(element, field), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new InvalidOperationException($"The booking {field} is invalid.");
    private static void ValidateStay(DateOnly arrival, DateOnly departure)
    {
        if (departure <= arrival || departure.DayNumber - arrival.DayNumber > 1000)
            throw new InvalidOperationException("Booking dates are invalid or exceed the supported 1,000-night import range. Review before acknowledgment.");
    }
}
