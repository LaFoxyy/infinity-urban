using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stayline;

namespace Stayline.Checks;

internal static class BookingChecks
{
    private const string BookingId = "55555555-5555-4555-8555-555555555555";
    private static readonly IOptions<ChannexOptions> Options = Microsoft.Extensions.Options.Options.Create(new ChannexOptions
    {
        Enabled = true, ApiKey = "test-only-not-a-real-key", WebhookSecret = "test-only-not-a-real-webhook-secret"
    });
    private static BookingInboxWorker Worker(TestDb db, RecordingHandler handler) => new(db, new MutationLock(),
        new ChannexClient(new HttpClient(handler), Options), Options, NullLogger<BookingInboxWorker>.Instance);

    public static async Task BookingIsCommittedBeforeAckAndAckRetriesAreIdempotent()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var revision = Guid.NewGuid().ToString("D");
        await using (var db = factory.CreateDbContext())
        {
            db.BookingReceipts.Add(new() { Id = revision });
            await db.SaveChangesAsync();
        }
        var attributes = Revision(seed, "new", DateTimeOffset.UtcNow, [(seed.Start, seed.Start.AddDays(3))]);
        var ackCalls = 0;
        var handler = new RecordingHandler
        {
            Respond = async request =>
            {
                if (request.Path.EndsWith("/ack"))
                {
                    await using var read = factory.CreateDbContext();
                    Check.Equal(1, await read.Reservations.CountAsync(), "The reservation must be committed before acknowledgment is sent");
                    Check.Equal(3, await read.Outbox.CountAsync(), "Availability effects must be committed before acknowledgment is sent");
                    Check.That((await read.BookingReceipts.SingleAsync()).ProcessedAt != null,
                        "The durable processed marker must be committed before acknowledgment is sent");
                    ackCalls++;
                    return RecordingHandler.Json(ackCalls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "{}");
                }
                Check.That(request.Method == "GET" && request.Path.EndsWith("/booking_revisions/feed"),
                    "Booking reception must read the complete feed payload, never list or by-ID endpoints");
                return Feed(revision, attributes);
            }
        };
        var worker = Worker(factory, handler);
        await worker.PollFeedAsync();
        await worker.ProcessInboxAsync();
        await using (var db = factory.CreateDbContext())
        {
            var receipt = await db.BookingReceipts.SingleAsync();
            Check.That(receipt.ProcessedAt != null && receipt.AckedAt == null, "Ack failure must retain a processed, unacknowledged receipt");
            Check.That(receipt.NextAttemptAt > DateTimeOffset.UtcNow, "Failed acknowledgment must receive retry backoff");
            Check.Equal(BookingId, receipt.ExternalBookingId, "The booking ID must be persisted alongside its revision receipt");
            Check.Equal("new", receipt.RevisionStatus, "The receipt must identify its actual lifecycle status");
            Check.Equal("feed", receipt.Source, "The receipt must record receipt through the real feed");
            receipt.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        // Recreating the worker models a process restart between persistence and acknowledgment.
        worker = Worker(factory, handler);
        await worker.ProcessInboxAsync();
        await worker.ProcessInboxAsync();
        Check.Equal(1, handler.Requests.Count(x => x.Method == "GET"), "Ack retry must not reimport the same booking revision");
        Check.Equal(2, ackCalls, "The failed ack must retry once; an acknowledged receipt must then be skipped");
        await using var final = factory.CreateDbContext();
        Check.Equal(1, await final.Reservations.CountAsync(), "Ack retry must not duplicate the reservation");
        Check.Equal(3, await final.Outbox.CountAsync(), "Ack retry must not duplicate inventory effects");
        Check.That((await final.BookingReceipts.SingleAsync()).AckedAt != null, "A successful retry must persist acknowledgment");
        Check.That((await final.BookingReceipts.SingleAsync()).PayloadJson == null,
            "Operational replay payload should be removed after successful acknowledgment");
    }

    public static async Task InvalidBookingMappingPreventsAckAndAllBookingWrites()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var revision = Guid.NewGuid().ToString("D");
        await using (var db = factory.CreateDbContext())
        {
            db.BookingReceipts.Add(new() { Id = revision });
            (await db.RatePlans.SingleAsync()).ChannexId = null;
            await db.SaveChangesAsync();
        }
        var attributes = Revision(seed, "new", DateTimeOffset.UtcNow, [(seed.Start, seed.Start.AddDays(2))]);
        var handler = new RecordingHandler
        {
            Respond = request =>
            {
                Check.That(request.Method == "GET" && request.Path.EndsWith("/booking_revisions/feed"),
                    "Invalid mappings must not cause a fallback to booking lists or by-ID fetching");
                return Task.FromResult(Feed(revision, attributes));
            }
        };
        var worker = Worker(factory, handler);
        await worker.PollFeedAsync();
        await worker.ProcessInboxAsync();
        Check.Equal(0, handler.Requests.Count(x => x.Path.EndsWith("/ack")), "An unmapped booking revision must not be acknowledged");
        await using var final = factory.CreateDbContext();
        Check.Equal(0, await final.Reservations.CountAsync(), "Invalid mapping must not leave a partial booking");
        Check.Equal(0, await final.Outbox.CountAsync(), "Invalid mapping must not leave partial inventory effects");
        var receipt = await final.BookingReceipts.SingleAsync();
        Check.That(receipt.ProcessedAt == null && receipt.AckedAt == null && !string.IsNullOrWhiteSpace(receipt.Error),
            "Mapping failure must remain visible and unacknowledged");
    }

    public static async Task PerRoomDatesAndOrderedRevisionsPreserveInventory()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var handler = new RecordingHandler();
        var worker = Worker(factory, handler);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var firstRevision = Guid.NewGuid().ToString("D");
        await worker.ApplyRevisionAsync(firstRevision, Revision(seed, "new", timestamp,
            [(seed.Start, seed.Start.AddDays(2)), (seed.Start.AddDays(1), seed.Start.AddDays(4))]));
        await worker.ApplyRevisionAsync(firstRevision, Revision(seed, "new", timestamp,
            [(seed.Start, seed.Start.AddDays(2)), (seed.Start.AddDays(1), seed.Start.AddDays(4))]));
        await using (var db = factory.CreateDbContext())
        {
            var bookings = await db.Reservations.Include(x => x.Stays).ToListAsync();
            Check.Equal(1, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start), "Per-room arrival dates must determine consumed inventory");
            Check.Equal(2, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(1)), "Overlapping booked room entries must both consume inventory");
            Check.Equal(1, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(2)), "Each room's checkout must be exclusive");
            Check.Equal(0, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(4)), "Final checkout must consume no inventory");
            Check.Equal(5, await db.Outbox.CountAsync(), "Duplicate revision import must not duplicate inventory work");
        }
        var tiedRevision = Guid.NewGuid().ToString("D");
        await using (var db = factory.CreateDbContext())
        {
            db.BookingReceipts.Add(new() { Id = tiedRevision });
            await db.SaveChangesAsync();
        }
        var rejectedTie = false;
        try
        {
            await worker.ApplyRevisionAsync(tiedRevision, Revision(seed, "modified", timestamp,
                [(seed.Start.AddDays(2), seed.Start.AddDays(5))]));
        }
        catch (InvalidOperationException) { rejectedTie = true; }
        Check.That(rejectedTie, "Distinct revisions with identical ordering timestamps must require review rather than silently overwrite state");
        await using (var db = factory.CreateDbContext())
        {
            var tiedReceipt = await db.BookingReceipts.SingleAsync(x => x.Id == tiedRevision);
            Check.That(tiedReceipt.ProcessedAt == null && tiedReceipt.AckedAt == null,
                "An ambiguously ordered revision must remain unprocessed and unacknowledged");
            Check.Equal(5, await db.Outbox.CountAsync(), "Ambiguous ordering must not change inventory work");
        }
        await worker.ApplyRevisionAsync(Guid.NewGuid().ToString("D"), Revision(seed, "modified", timestamp.AddMinutes(1),
            [(seed.Start.AddDays(2), seed.Start.AddDays(5))]));
        await using (var db = factory.CreateDbContext())
        {
            var bookings = await db.Reservations.Include(x => x.Stays).ToListAsync();
            Check.Equal(0, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start), "A booking modification must release old occupied dates");
            Check.Equal(1, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(4)), "A booking modification must consume new occupied dates");
            Check.Equal(1, await db.ReservationStays.CountAsync(), "A booking modification must replace previous room entries");
        }
        await worker.ApplyRevisionAsync(Guid.NewGuid().ToString("D"), Revision(seed, "cancelled", timestamp.AddMinutes(2), []));
        int countAfterCancel;
        await using (var db = factory.CreateDbContext())
        {
            var bookings = await db.Reservations.Include(x => x.Stays).ToListAsync();
            Check.Equal(0, PmsService.Booked(bookings, seed.RoomTypeId, seed.Start.AddDays(3)), "Channel cancellation must release inventory");
            countAfterCancel = await db.Outbox.CountAsync();
        }
        var staleRevision = Guid.NewGuid().ToString("D");
        await worker.ApplyRevisionAsync(staleRevision, Revision(seed, "new", timestamp,
            [(seed.Start, seed.Start.AddDays(2))]));
        await using var final = factory.CreateDbContext();
        Check.Equal("cancelled", (await final.Reservations.SingleAsync()).Status, "An older revision must not resurrect a cancelled booking");
        Check.Equal(countAfterCancel, await final.Outbox.CountAsync(), "An older revision must not change inventory effects");
        Check.That((await final.BookingReceipts.SingleAsync(x => x.Id == staleRevision)).ProcessedAt != null,
            "An obsolete revision must be recorded as safely processed so it can be acknowledged");
        Check.Equal(0, handler.Requests.Count, "Applying revisions directly must never acknowledge before its caller completes persistence");
    }

    public static async Task FeedRecoveryCollectsEveryPageWithoutDuplicatingReceipts()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        var page = 0;
        var handler = new RecordingHandler
        {
            Respond = request =>
            {
                Check.That(request.Method == "GET" && request.Path.EndsWith("/booking_revisions/feed"),
                    "Feed recovery must collect pages before importing or acknowledging revisions");
                var selected = page++ % 2 == 0 ? ids.Take(100) : ids.Skip(100);
                return Task.FromResult(RecordingHandler.Json(HttpStatusCode.OK,
                    JsonSerializer.Serialize(new { data = selected.Select(id => new { id, type = "booking_revision",
                        attributes = Revision(seed, "new", DateTimeOffset.UtcNow, [(seed.Start, seed.Start.AddDays(2))]) }).ToArray(),
                        meta = new { total = 101 } })));
            }
        };
        var worker = Worker(factory, handler);
        await worker.PollFeedAsync();
        await worker.PollFeedAsync();
        Check.Equal(4, handler.Requests.Count, "Each complete feed poll must read all pages");
        await using var db = factory.CreateDbContext();
        Check.Equal(101, await db.BookingReceipts.CountAsync(), "Repeated feed polling must recover every revision exactly once");
        Check.Equal(101, await db.BookingReceipts.CountAsync(x => x.ProcessedAt == null && x.AckedAt == null),
            "Recovered revisions must remain durably pending until booking persistence succeeds");
        Check.Equal(101, await db.BookingReceipts.CountAsync(x => x.PayloadJson != null && x.Source == "feed"),
            "Every page must durably retain its complete sanitized feed snapshot without a second fetch");
    }

    public static async Task CompleteLifecycleUsesOnlyFeedThenAckAndKeepsLabeledEvidence()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var revisions = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        var statuses = new[] { "new", "modified", "cancelled" };
        var stage = 0;
        var ackCalls = 0;
        var insertedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var handler = new RecordingHandler
        {
            Respond = async request =>
            {
                if (request.Method == "GET")
                {
                    Check.Equal("/api/v1/booking_revisions/feed", request.Path, "Incoming lifecycle must never call booking lists or revision by-ID endpoints");
                    var stays = stage == 0 ? new[] { (seed.Start, seed.Start.AddDays(2)) } :
                        stage == 1 ? new[] { (seed.Start.AddDays(1), seed.Start.AddDays(4)) } : [];
                    return Feed(revisions[stage], Revision(seed, statuses[stage], insertedAt.AddMinutes(stage), stays));
                }
                Check.Equal($"/api/v1/booking_revisions/{revisions[stage]}/ack", request.Path,
                    "Only the revision received through the feed may be acknowledged");
                await using var read = factory.CreateDbContext();
                var reservation = await read.Reservations.SingleAsync();
                Check.Equal(BookingId, reservation.ExternalBookingId, "All revisions must update the same external booking");
                Check.Equal(stage == 2 ? "cancelled" : "confirmed", reservation.Status, "Booking state must commit before its corresponding acknowledgment");
                Check.That((await read.BookingReceipts.SingleAsync(x => x.Id == revisions[stage])).ProcessedAt != null,
                    "Each lifecycle stage must have a committed processed marker before acknowledgment");
                ackCalls++;
                return RecordingHandler.Json(HttpStatusCode.OK, "{}");
            }
        };
        var worker = Worker(factory, handler);
        for (stage = 0; stage < 3; stage++) await worker.CheckFeedAsync();
        Check.Equal(3, ackCalls, "Each lifecycle revision must be acknowledged exactly once");
        Check.Equal(6, handler.Requests.Count, "Each stage must require only one feed GET and one ACK POST");
        await using var db = factory.CreateDbContext();
        var receipts = await db.BookingReceipts.ToListAsync();
        Check.Equal(3, receipts.Count, "All three revision IDs must remain available as evidence");
        Check.That(receipts.All(x => x.ExternalBookingId == BookingId && x.Source == "feed" && x.AckedAt != null && x.PayloadJson == null),
            "Acknowledged evidence must retain booking ID/provenance while dropping replay payloads");
        Check.That(receipts.Select(x => x.RevisionStatus).Order().SequenceEqual(statuses.Order()),
            "New, modified and cancelled revision IDs must remain distinguishable");
        var saved = await db.Reservations.Include(x => x.Stays).SingleAsync();
        Check.Equal(0, PmsService.Booked([saved], seed.RoomTypeId, seed.Start.AddDays(2)), "Cancellation must release imported inventory");
    }

    public static async Task FeedSnapshotsStripCardsAndContactDataBeforePersistence()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var revision = Guid.NewGuid().ToString("D");
        var attributes = JsonSerializer.Deserialize<Dictionary<string, object?>>(Revision(seed, "new", DateTimeOffset.UtcNow,
            [(seed.Start, seed.Start.AddDays(2))]).GetRawText())!;
        attributes["customer"] = new { name = "Operational", surname = "Guest", email = "do-not-retain@example.invalid",
            phone = "CONTACT-MUST-NOT-PERSIST", address = "PRIVATE-ADDRESS-MUST-NOT-PERSIST" };
        attributes["guarantee"] = new { card_number = "CARD-MUST-NOT-PERSIST", cvv = "CVV-MUST-NOT-PERSIST" };
        attributes["notes"] = "PRIVATE-NOTES-MUST-NOT-PERSIST";
        attributes["payment_type"] = "PAYMENT-MUST-NOT-PERSIST";
        var handler = new RecordingHandler
        {
            Respond = request =>
            {
                Check.Equal("/api/v1/booking_revisions/feed", request.Path, "Durable discovery must use only the feed");
                return Task.FromResult(Feed(revision, JsonSerializer.SerializeToElement(attributes)));
            }
        };
        await Worker(factory, handler).PollFeedAsync();
        await using var db = factory.CreateDbContext();
        var receipt = await db.BookingReceipts.SingleAsync();
        Check.That(!string.IsNullOrWhiteSpace(receipt.PayloadJson), "The feed must remain replayable before processing/acknowledgment");
        foreach (var forbidden in new[] { "do-not-retain", "MUST-NOT-PERSIST", "guarantee", "card_number", "cvv", "payment_type", "notes", "email", "phone", "address" })
            Check.That(!receipt.PayloadJson!.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "Private field must not be stored in the replay payload: " + forbidden);
        Check.That(receipt.PayloadJson!.Contains("Operational", StringComparison.Ordinal), "The operational guest name needed to display the booking must survive sanitization");
        Check.That(!JsonSerializer.Serialize(receipt).Contains("Operational", StringComparison.Ordinal),
            "The replay payload must never be serialized in browser-facing receipt objects");
    }

    public static async Task BookingListAndByIdReadsAreBlockedBeforeTransport()
    {
        var handler = new RecordingHandler();
        var client = new ChannexClient(new HttpClient(handler), Options);
        foreach (var path in new[] { "bookings", "bookings/" + BookingId, "booking_revisions", "booking_revisions/" + Guid.NewGuid().ToString("D") })
        {
            var rejected = false;
            try { await client.FetchCollectionAsync(path); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException) { rejected = true; }
            Check.That(rejected, "Accidental booking list/by-ID reads must be rejected: " + path);
        }
        Check.Equal(0, handler.Requests.Count, "Forbidden receive methods must be rejected before any remote request");
    }

    private static HttpResponseMessage Feed(string id, JsonElement attributes) => RecordingHandler.Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new { data = new[] { new { id, type = "booking_revision", attributes } }, meta = new { total = 1 } }));

    private static JsonElement Revision(SeededProperty seed, string status, DateTimeOffset insertedAt,
        (DateOnly Arrival, DateOnly Departure)[] stays)
    {
        var arrival = stays.Length == 0 ? seed.Start : stays.Min(x => x.Arrival);
        var departure = stays.Length == 0 ? seed.Start.AddDays(5) : stays.Max(x => x.Departure);
        return JsonSerializer.SerializeToElement(new
        {
            property_id = SeededProperty.RemoteProperty,
            booking_id = BookingId,
            status,
            inserted_at = insertedAt.ToString("O"),
            arrival_date = arrival.ToString("yyyy-MM-dd"),
            departure_date = departure.ToString("yyyy-MM-dd"),
            customer = new { name = "Jordan", surname = "Lee" },
            rooms = stays.Select(x => new
            {
                room_type_id = SeededProperty.RemoteRoom,
                rate_plan_id = SeededProperty.RemoteRate,
                checkin_date = x.Arrival.ToString("yyyy-MM-dd"),
                checkout_date = x.Departure.ToString("yyyy-MM-dd")
            }).ToArray()
        });
    }
}
