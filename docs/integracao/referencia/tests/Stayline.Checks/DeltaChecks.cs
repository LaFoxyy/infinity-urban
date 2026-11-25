using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stayline;

namespace Stayline.Checks;

internal static class DeltaChecks
{
    private static readonly IOptions<ChannexOptions> Options = Microsoft.Extensions.Options.Options.Create(new ChannexOptions
    { Enabled = true, ApiKey = "delta-checks-only", BatchSeconds = 0 });

    private static ChannexSyncWorker Worker(TestDb db, RecordingHandler handler, MutationLock? gate = null) => new(db,
        gate ?? new MutationLock(), new ChannexClient(new HttpClient(handler), Options), Options, NullLogger<ChannexSyncWorker>.Instance);

    public static async Task PriceOnlySaveTargetsExactlyTheChosenDateAndRate()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var date = seed.Start.AddDays(2);
        await new PmsService(factory, new MutationLock()).SaveCalendarAsync(seed.PropertyId,
            new CalendarInput([new(null, seed.RatePlanId, date, date, Rate: 333.25m)]));
        var handler = new RecordingHandler();
        await Worker(factory, handler).ProcessPendingAsync();
        Check.Equal(1, handler.Requests.Count, "One price save must emit one restrictions request");
        Check.Equal("/api/v1/restrictions", handler.Requests[0].Path, "A price edit must not emit availability");
        var value = Values(handler.Requests[0]).Single();
        Fields(value, "property_id", "rate_plan_id", "date", "rate");
        Check.Equal(SeededProperty.RemoteRate, value.GetProperty("rate_plan_id").GetString(), "The selected rate mapping must be retained");
        Check.Equal(date.ToString("yyyy-MM-dd"), value.GetProperty("date").GetString(), "The selected date must not become today or a neighboring date");
        Check.Equal("333.25", value.GetProperty("rate").GetString(), "The saved price must be preserved exactly");
    }

    public static async Task MultiRateSavePreservesEverySelectedMappingInOneRequest()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var setup = await AddOtherRoomAndPlans(factory, seed);
        var changes = new[]
        {
            (Local: seed.RatePlanId, Remote: SeededProperty.RemoteRate, Day: seed.Start.AddDays(1), Price: 333m),
            (Local: setup.SecondRate.Id, Remote: setup.SecondRate.ChannexId!, Day: seed.Start.AddDays(5), Price: 444m),
            (Local: setup.ThirdRate.Id, Remote: setup.ThirdRate.ChannexId!, Day: seed.Start.AddDays(9), Price: 456.23m)
        };
        await new PmsService(factory, new MutationLock()).SaveCalendarAsync(seed.PropertyId,
            new CalendarInput(changes.Select(x => new CalendarChange(null, x.Local, x.Day, x.Day, Rate: x.Price)).ToList()));
        var handler = new RecordingHandler();
        await Worker(factory, handler).ProcessPendingAsync();
        Check.Equal(1, handler.Requests.Count, "A multi-rate calendar save must be batched into one API request");
        var values = Values(handler.Requests[0]);
        Check.Equal(3, values.Length, "Each of the three selected rates/dates must appear once");
        foreach (var expected in changes)
        {
            var row = values.Single(x => x.GetProperty("rate_plan_id").GetString() == expected.Remote);
            Fields(row, "property_id", "rate_plan_id", "date", "rate");
            Check.Equal(expected.Day.ToString("yyyy-MM-dd"), row.GetProperty("date").GetString(), "Each rate must retain its own date");
            Check.Equal(expected.Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), row.GetProperty("rate").GetString(),
                "Each rate must retain its own decimal price");
        }
    }

    public static async Task MinimumStayAndBooleanEditsContainOnlyRequestedRestrictions()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var service = new PmsService(factory, new MutationLock());
        var expected = new[] { 3, 2, 5 };
        await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput(expected.Select((n, i) =>
            new CalendarChange(null, seed.RatePlanId, seed.Start.AddDays(i * 2), seed.Start.AddDays(i * 2),
                MinStayArrival: n, MinStayThrough: n)).ToList()));
        var handler = new RecordingHandler();
        var worker = Worker(factory, handler);
        await worker.ProcessPendingAsync();
        foreach (var row in Values(handler.Requests.Single()))
        {
            Fields(row, "property_id", "rate_plan_id", "date", "min_stay_arrival", "min_stay_through");
            Check.Equal(row.GetProperty("min_stay_arrival").GetInt32(), row.GetProperty("min_stay_through").GetInt32(),
                "Explicit minimum-stay pairs must both carry the requested value");
        }
        await using (var db = factory.CreateDbContext())
        {
            db.DailyRates.Add(new DailyRate { RatePlanId = seed.RatePlanId, Date = seed.Start.AddDays(10),
                Rate = 200m, StopSell = true, ClosedToArrival = true });
            await db.SaveChangesAsync();
        }
        await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput([
            new(null, seed.RatePlanId, seed.Start.AddDays(10), seed.Start.AddDays(10), StopSell: false)
        ]));
        await ExpireDeliveryWindow(factory);
        await worker.ProcessPendingAsync();
        var stop = Values(handler.Requests.Last()).Single();
        Fields(stop, "property_id", "rate_plan_id", "date", "stop_sell");
        Check.That(!stop.GetProperty("stop_sell").GetBoolean(), "An explicit false restriction must be sent, not treated as absent");
        await using var final = factory.CreateDbContext();
        var day = await final.DailyRates.SingleAsync(x => x.Date == seed.Start.AddDays(10));
        Check.That(day.ClosedToArrival && day.Rate == 200m, "Unselected saved restrictions and price must remain unchanged");
    }

    public static async Task RangesMergeOnlyContiguousMatchingEntitiesMasksAndValues()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var setup = await AddOtherRoomAndPlans(factory, seed);
        var service = new PmsService(factory, new MutationLock());
        await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput([
            new(null, seed.RatePlanId, seed.Start, seed.Start.AddDays(2), Rate: 250m),
            new(null, seed.RatePlanId, seed.Start.AddDays(4), seed.Start.AddDays(5), Rate: 250m),
            new(null, seed.RatePlanId, seed.Start.AddDays(6), seed.Start.AddDays(6), Rate: 260m),
            new(null, seed.RatePlanId, seed.Start.AddDays(7), seed.Start.AddDays(7), Rate: 260m, MinStayArrival: 2, MinStayThrough: 2),
            new(null, setup.SecondRate.Id, seed.Start, seed.Start.AddDays(2), Rate: 250m)
        ]));
        var handler = new RecordingHandler();
        await Worker(factory, handler).ProcessPendingAsync();
        var rows = Values(handler.Requests.Single());
        Check.Equal(5, rows.Length, "Only exact contiguous sequences may be merged");
        var primary = rows.Where(x => x.GetProperty("rate_plan_id").GetString() == SeededProperty.RemoteRate).ToArray();
        Check.Equal(4, primary.Length, "Different selected fields must prevent merging even when the price matches");
        var first = primary.Single(x => x.TryGetProperty("date_from", out var from) && from.GetString() == seed.Start.ToString("yyyy-MM-dd"));
        Check.Equal(seed.Start.AddDays(2).ToString("yyyy-MM-dd"), first.GetProperty("date_to").GetString(), "Ranges are inclusive");
        Fields(first, "property_id", "rate_plan_id", "date_from", "date_to", "rate");
        var combined = primary.Single(x => x.TryGetProperty("date", out var date) && date.GetString() == seed.Start.AddDays(7).ToString("yyyy-MM-dd"));
        Fields(combined, "property_id", "rate_plan_id", "date", "rate", "min_stay_arrival", "min_stay_through");
        using var document = JsonDocument.Parse(handler.Requests.Single().Body);
        var expanded = PayloadDates.Expand(document.RootElement);
        Check.Equal(10, expanded.Length, "Merging must preserve every intended date and entity exactly once");
        Check.That(!expanded.Any(x => x.GetProperty("date").GetString() == seed.Start.AddDays(3).ToString("yyyy-MM-dd")),
            "Merging must not fill a gap with unrequested updates");
    }

    public static async Task DirtyMasksUnionLatestStateAndLegacyRowsRemainSafe()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var service = new PmsService(factory, new MutationLock());
        await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput([new(null, seed.RatePlanId, seed.Start, seed.Start, Rate: 200m)]));
        await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput([new(null, seed.RatePlanId, seed.Start, seed.Start,
            MinStayArrival: 4, MinStayThrough: 4)]));
        await using var db = factory.CreateDbContext();
        (await db.DailyRates.SingleAsync()).Rate = 444.45m;
        await db.SaveChangesAsync();
        var pending = await db.Outbox.AsNoTracking().ToListAsync();
        var union = Json(await ChannexSyncWorker.BuildPayloadAsync(db, seed.PropertyId, "restrictions", pending));
        var row = union.GetProperty("values")[0];
        Fields(row, "property_id", "rate_plan_id", "date", "rate", "min_stay_arrival", "min_stay_through");
        Check.Equal("444.45", row.GetProperty("rate").GetString(), "A merged dirty key must read the latest committed price");
        pending.Add(new OutboxChange { PropertyId = seed.PropertyId, Kind = "restrictions", EntityId = seed.RatePlanId, Date = seed.Start });
        var legacy = Json(await ChannexSyncWorker.BuildPayloadAsync(db, seed.PropertyId, "restrictions", pending));
        Fields(legacy.GetProperty("values")[0], "property_id", "rate_plan_id", "date", "rate", "min_stay_arrival",
            "min_stay_through", "max_stay", "stop_sell", "closed_to_arrival", "closed_to_departure");
    }

    public static async Task FullSyncCoversAllRoomsAndPlansUsingTwoMergedRequests()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await AddOtherRoomAndPlans(factory, seed);
        await new PmsService(factory, new MutationLock()).QueueFullSyncAsync(seed.PropertyId);
        var handler = new RecordingHandler();
        await Worker(factory, handler).ProcessPendingAsync();
        Check.Equal(2, handler.Requests.Count, "A full sync must remain one availability and one restrictions request");
        var availability = handler.Requests.Single(x => x.Path.EndsWith("/availability"));
        var restrictions = handler.Requests.Single(x => x.Path.EndsWith("/restrictions"));
        var inventoryRows = Values(availability);
        var restrictionRows = Values(restrictions);
        Check.Equal(2, inventoryRows.Length, "Each uniform room should use a single merged 500-day range");
        Check.Equal(4, restrictionRows.Length, "Each uniform rate should use a single merged 500-day range");
        await using var db = factory.CreateDbContext();
        var start = PmsService.Today(await db.Properties.SingleAsync());
        foreach (var row in inventoryRows.Concat(restrictionRows))
        {
            Check.Equal(start.ToString("yyyy-MM-dd"), row.GetProperty("date_from").GetString(), "Full sync must start today in the property's timezone");
            Check.Equal(start.AddDays(499).ToString("yyyy-MM-dd"), row.GetProperty("date_to").GetString(), "Full sync must cover exactly500 dates");
        }
        foreach (var row in restrictionRows)
            Fields(row, "property_id", "rate_plan_id", "date_from", "date_to", "rate", "min_stay_arrival",
                "min_stay_through", "max_stay", "stop_sell", "closed_to_arrival", "closed_to_departure");
        Check.Equal(1000, PayloadDates.Expand(Json(availability.Body)).Length, "All room dates must remain covered after merging");
        Check.Equal(2000, PayloadDates.Expand(Json(restrictions.Body)).Length, "All rate dates must remain covered after merging");
    }

    public static async Task ReapplyRepairsOnlyExplicitFieldsWithoutInventingStateChanges()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            db.DailyRates.Add(new DailyRate { RatePlanId = seed.RatePlanId, Date = seed.Start, Rate = 200m,
                MinStayArrival = 1, MinStayThrough = 3 });
            await db.SaveChangesAsync();
        }
        var service = new PmsService(factory, new MutationLock());
        var selected = new List<CalendarChange> { new(null, seed.RatePlanId, seed.Start, seed.Start, MinStayArrival: 3, MinStayThrough: 3) };
        Check.Equal(1, await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput(selected)), "Changing one field of an explicit pair must queue the pair");
        var handler = new RecordingHandler();
        var worker = Worker(factory, handler);
        await worker.ProcessPendingAsync();
        Fields(Values(handler.Requests.Single()).Single(), "property_id", "rate_plan_id", "date", "min_stay_arrival", "min_stay_through");
        Check.Equal(0, await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput(selected)), "An ordinary unchanged save must not manufacture dirty work");
        Check.Equal(1, await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput(selected, Reapply: true)), "Explicit remote repair may requeue saved selected fields");
        await ExpireDeliveryWindow(factory);
        await worker.ProcessPendingAsync();
        var repaired = Values(handler.Requests.Last()).Single();
        Fields(repaired, "property_id", "rate_plan_id", "date", "min_stay_arrival", "min_stay_through");
        Check.Equal(3, repaired.GetProperty("min_stay_arrival").GetInt32(), "Reapply must preserve requested arrival minimum");
        Check.Equal(3, repaired.GetProperty("min_stay_through").GetInt32(), "Reapply must preserve requested through minimum");
    }

    public static async Task NewFieldSavedDuringDeliveryKeepsItsOwnLaterDelta()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        var gate = new MutationLock();
        var service = new PmsService(factory, gate);
        await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput([new(null, seed.RatePlanId, seed.Start, seed.Start, Rate: 200m)]));
        var handler = new RecordingHandler();
        handler.Respond = async _ =>
        {
            await service.SaveCalendarAsync(seed.PropertyId, new CalendarInput([new(null, seed.RatePlanId, seed.Start, seed.Start, StopSell: true)]));
            return RecordingHandler.Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"" + RecordingHandler.TaskId + "\",\"type\":\"task\"}]}");
        };
        var worker = Worker(factory, handler, gate);
        await worker.ProcessPendingAsync();
        Fields(Values(handler.Requests.Single()).Single(), "property_id", "rate_plan_id", "date", "rate");
        await using (var db = factory.CreateDbContext())
            Check.Equal(1, await db.Outbox.CountAsync(x => x.ProcessedAt == null), "Accepting the first snapshot must leave the newer field pending");
        handler.Respond = _ => Task.FromResult(RecordingHandler.Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"" + RecordingHandler.TaskId + "\",\"type\":\"task\"}]}"));
        await ExpireDeliveryWindow(factory);
        await worker.ProcessPendingAsync();
        var final = Values(handler.Requests.Last()).Single();
        Fields(final, "property_id", "rate_plan_id", "date", "stop_sell");
        Check.That(final.GetProperty("stop_sell").GetBoolean(), "The later delta must send the newer saved field");
    }

    private static async Task<(RoomType Room, RatePlan SecondRate, RatePlan ThirdRate)> AddOtherRoomAndPlans(TestDb factory, SeededProperty seed)
    {
        await using var db = factory.CreateDbContext();
        var room = new RoomType { PropertyId = seed.PropertyId, Name = "Other room", Quantity = 4, ChannexId = Guid.NewGuid().ToString("D") };
        var second = new RatePlan { RoomTypeId = room.Id, Name = "Flexible", BasePrice = 201.25m, ChannexId = Guid.NewGuid().ToString("D") };
        var third = new RatePlan { RoomTypeId = room.Id, Name = "Breakfast", BasePrice = 225.25m, ChannexId = Guid.NewGuid().ToString("D") };
        var fourth = new RatePlan { RoomTypeId = seed.RoomTypeId, Name = "Breakfast", BasePrice = 149.95m, ChannexId = Guid.NewGuid().ToString("D") };
        db.AddRange(room, second, third, fourth);
        await db.SaveChangesAsync();
        return (room, second, third);
    }

    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static JsonElement[] Values(SentRequest request) => Json(request.Body).GetProperty("values").EnumerateArray().ToArray();
    private static void Fields(JsonElement value, params string[] expected)
    {
        var actual = value.EnumerateObject().Select(x => x.Name).Order().ToArray();
        Check.That(actual.SequenceEqual(expected.Order()), "Payload fields must be exactly: " + string.Join(", ", expected) + "; got " + string.Join(", ", actual));
    }
    private static async Task ExpireDeliveryWindow(TestDb factory)
    {
        await using var db = factory.CreateDbContext();
        foreach (var gate in await db.SyncGates.ToListAsync()) gate.NextAttemptAt = DateTimeOffset.MinValue;
        foreach (var attempt in await db.SyncAttempts.ToListAsync()) attempt.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();
    }
}
