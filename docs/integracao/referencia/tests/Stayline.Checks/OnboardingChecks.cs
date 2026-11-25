using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stayline;

namespace Stayline.Checks;

internal static class OnboardingChecks
{
    public static async Task DiscoveryAndPreviewNeverCreateOrPublishLocalData()
    {
        await using var app = new OnboardingFixture();
        var existing = await app.SeedLocalAsync("BRL", withRoom: false);
        var discovery = await Success(await app.Client.GetAsync("/api/channex/remote"));
        Check.Equal("GBP", discovery.GetProperty("properties")[0].GetProperty("currency").GetString(),
            "Discovery must show the staging property's real currency");
        var preview = await Success(await app.Client.GetAsync($"/api/channex/import-preview?propertyId={SnapshotHandler.PropertyId}"));
        Check.Equal("Demo Property", preview.GetProperty("name").GetString(), "Preview must use authoritative staging metadata");
        Check.Equal(2, preview.GetProperty("rooms").GetArrayLength(), "Preview must include both room types");
        Check.Equal(500, preview.GetProperty("days").GetInt32(), "Preview must validate the whole publication horizon");
        await using var db = await app.DbAsync();
        Check.Equal(1, await db.Properties.CountAsync(), "Reading remote properties must not create a local property");
        Check.Equal("BRL", (await db.Properties.SingleAsync(x => x.Id == existing.PropertyId)).Currency,
            "A staging lookup must never rewrite the existing local property's currency");
        Check.Equal(0, await db.RoomTypes.CountAsync(), "Preview must not write room types");
        Check.Equal(0, await db.Outbox.CountAsync(), "Preview must not enqueue outgoing synchronization");
        Check.That(app.Remote.Requests.Count > 0 && app.Remote.Requests.All(x => x.Method == "GET"),
            "Staging discovery and preview must make GET requests only");
    }

    public static async Task ImportCopiesCompleteSavedStateAsAnUnmappedDraft()
    {
        await using var app = new OnboardingFixture();
        var existing = await app.SeedLocalAsync("BRL", withRoom: false);
        var response = await Import(app.Client);
        var importedId = response.GetProperty("propertyId").GetGuid();
        Check.That(importedId != existing.PropertyId, "Import must create a separate property instead of modifying the user's BRL property");
        Check.That(!response.GetProperty("alreadyImported").GetBoolean(), "The first import must report a new draft");
        await using var db = await app.DbAsync();
        var property = await db.Properties.SingleAsync(x => x.Id == importedId);
        Check.Equal("GBP", property.Currency, "The imported currency must match staging");
        Check.Equal("UTC", property.TimeZoneId, "The imported timezone must match staging");
        Check.Equal("BRL", (await db.Properties.SingleAsync(x => x.Id == existing.PropertyId)).Currency,
            "Import must preserve the original local property");
        Check.That(property.ChannexId == null, "The imported draft must have no active property mapping");
        var rooms = await db.RoomTypes.Where(x => x.PropertyId == importedId).ToListAsync();
        var roomIds = rooms.Select(x => x.Id).ToArray();
        var plans = await db.RatePlans.Where(x => roomIds.Contains(x.RoomTypeId)).ToListAsync();
        Check.Equal(2, rooms.Count, "Import must persist every room type");
        Check.Equal(2, plans.Count, "Import must persist every supported rate plan");
        Check.That(rooms.All(x => x.ChannexId == null) && plans.All(x => x.ChannexId == null),
            "An imported draft must have no active room or rate mapping");
        foreach (var room in rooms)
        {
            var roomIndex = room.Name == "Garden Double" ? 0 : 1;
            Check.Equal(SnapshotHandler.Quantities[roomIndex], room.Quantity, "Import must preserve room quantities");
            Check.Equal(roomIndex == 0 ? 2 : 3, room.Occupancy, "Import must preserve occupancy");
            var inventory = await db.DailyInventories.Where(x => x.RoomTypeId == room.Id).OrderBy(x => x.Date).ToListAsync();
            Check.Equal(500, inventory.Count, "Draft inventory must contain all 500 dates without relying on guessed defaults");
            var plan = plans.Single(x => x.RoomTypeId == room.Id);
            var rates = await db.DailyRates.Where(x => x.RatePlanId == plan.Id).OrderBy(x => x.Date).ToListAsync();
            Check.Equal(500, rates.Count, "Draft rates and restrictions must contain all 500 dates");
            for (var day = 0; day < 500; day++)
            {
                var date = app.Remote.Start.AddDays(day);
                Check.Equal(date, inventory[day].Date, "Imported inventory dates must be consecutive");
                Check.Equal(SnapshotHandler.Available(roomIndex, day), inventory[day].Capacity,
                    "Draft open capacity must preserve the remote availability snapshot");
                var saved = rates[day];
                Check.Equal(date, saved.Date, "Imported rate dates must be consecutive");
                Check.Equal(SnapshotHandler.Price(roomIndex, day), saved.Rate, "Import must preserve each varied staging price");
                Check.Equal(1 + day % 3, saved.MinStayArrival, "Import must preserve minimum arrival stay");
                Check.Equal(1 + day % 2, saved.MinStayThrough, "Import must preserve minimum through stay");
                Check.Equal(14, saved.MaxStay, "Import must preserve maximum stay");
                Check.Equal(day % 9 == 0, saved.StopSell, "Import must preserve stop sell");
                Check.Equal(day % 11 == 0, saved.ClosedToArrival, "Import must preserve closed to arrival");
                Check.Equal(day % 13 == 0, saved.ClosedToDeparture, "Import must preserve closed to departure");
            }
        }
        Check.Equal(0, await db.Outbox.CountAsync(), "Import must never enqueue an outgoing update");
        Check.That(app.Remote.Requests.All(x => x.Method == "GET"), "Import must never send availability, restrictions or booking acknowledgments");
        var text = await app.Client.GetStringAsync("/api/properties");
        Check.That(!text.Contains(OnboardingFixture.FakeKey, StringComparison.Ordinal), "Browser-facing imported data must never include the API credential");
        using var listed = JsonDocument.Parse(text);
        var listedDraft = listed.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == importedId);
        Check.Equal(SnapshotHandler.PropertyId, listedDraft.GetProperty("suggestedChannexId").GetString(),
            "Reloading properties must preserve the draft's suggested staging property mapping");
        Check.Equal(JsonValueKind.Null, listedDraft.GetProperty("channexId").ValueKind,
            "A suggested property mapping must not activate synchronization");
        foreach (var listedRoom in listedDraft.GetProperty("roomTypes").EnumerateArray())
        {
            var roomIndex = listedRoom.GetProperty("name").GetString() == "Garden Double" ? 0 : 1;
            Check.Equal(SnapshotHandler.RoomIds[roomIndex], listedRoom.GetProperty("suggestedChannexId").GetString(),
                "Reloading properties must preserve each source room's suggested mapping");
            Check.Equal(JsonValueKind.Null, listedRoom.GetProperty("channexId").ValueKind,
                "A suggested room mapping must remain inactive");
            var listedRate = listedRoom.GetProperty("ratePlans").EnumerateArray().Single();
            Check.Equal(SnapshotHandler.RateIds[roomIndex], listedRate.GetProperty("suggestedChannexId").GetString(),
                "Reloading properties must preserve each source rate's suggested mapping");
            Check.Equal(JsonValueKind.Null, listedRate.GetProperty("channexId").ValueKind,
                "A suggested rate mapping must remain inactive");
        }
        var listedOriginal = listed.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == existing.PropertyId);
        Check.Equal(JsonValueKind.Null, listedOriginal.GetProperty("suggestedChannexId").ValueKind,
            "Import suggestions must not be attached to an unrelated pre-existing property");
    }

    public static async Task RepeatedImportAndAlreadyMappedPropertiesDoNotDuplicateRecords()
    {
        await using (var app = new OnboardingFixture())
        {
            var first = await Import(app.Client);
            var requests = app.Remote.Requests.Count;
            var again = await Import(app.Client);
            Check.Equal(first.GetProperty("propertyId").GetGuid(), again.GetProperty("propertyId").GetGuid(),
                "Repeated imports must return the same local draft");
            Check.That(again.GetProperty("alreadyImported").GetBoolean(), "Repeated imports must report that the draft already exists");
            Check.Equal(requests, app.Remote.Requests.Count, "Idempotent import must not replace saved draft state with a fresh snapshot");
            await using var db = await app.DbAsync();
            Check.Equal(1, await db.Properties.CountAsync(), "Repeated imports must not duplicate properties");
            Check.Equal(1000, await db.DailyRates.CountAsync(), "Repeated imports must not duplicate calendar state");
            Check.Equal(0, await db.Outbox.CountAsync(), "Repeated imports must not enqueue work");
        }
        await using (var app = new OnboardingFixture())
        {
            var mapped = await app.SeedLocalAsync("GBP", mapped: true);
            var result = await Import(app.Client);
            Check.Equal(mapped.PropertyId, result.GetProperty("propertyId").GetGuid(), "An existing mapping must be reused instead of duplicated");
            Check.That(result.GetProperty("alreadyImported").GetBoolean(), "An already mapped property must be reported as existing");
            await using var db = await app.DbAsync();
            Check.Equal(1, await db.Properties.CountAsync(), "Import must not duplicate an already mapped property");
            Check.Equal(0, await db.Outbox.CountAsync(), "Finding an existing mapping must not queue a full sync");
        }
    }

    public static async Task InvalidOrIncompleteSnapshotsCannotLeavePartialImportedState()
    {
        foreach (var fault in new[] { SnapshotFault.MissingDate, SnapshotFault.MissingPriceField, SnapshotFault.InvalidPrice, SnapshotFault.ForeignRoom,
                     SnapshotFault.ForeignRate, SnapshotFault.UnknownRoom, SnapshotFault.EchoedHttpFailure })
        {
            await using var app = new OnboardingFixture(fault);
            var existing = await app.SeedLocalAsync("BRL", withRoom: false);
            var response = await app.Client.PostAsJsonAsync("/api/channex/import", new { propertyId = SnapshotHandler.PropertyId });
            Check.Equal(HttpStatusCode.BadGateway, response.StatusCode, $"An invalid snapshot ({fault}) must fail with a staging data error");
            var body = await response.Content.ReadAsStringAsync();
            Check.That(!body.Contains(OnboardingFixture.FakeKey, StringComparison.Ordinal), "Import errors must not reflect a credential echoed by staging");
            await using var db = await app.DbAsync();
            Check.Equal(1, await db.Properties.CountAsync(), $"A failed {fault} import must not leave a partial property");
            Check.Equal("BRL", (await db.Properties.SingleAsync(x => x.Id == existing.PropertyId)).Currency,
                "A failed import must preserve existing property state");
            Check.Equal(0, await db.RoomTypes.CountAsync(), $"A failed {fault} import must not leave room types");
            Check.Equal(0, await db.RatePlans.CountAsync(), $"A failed {fault} import must not leave rate plans");
            Check.Equal(0, await db.DailyInventories.CountAsync(), $"A failed {fault} import must not leave inventory");
            Check.Equal(0, await db.DailyRates.CountAsync(), $"A failed {fault} import must not leave prices");
            Check.Equal(0, await db.Outbox.CountAsync(), $"A failed {fault} import must not queue work");
            Check.Equal(0, await db.SyncAttempts.CountAsync(), $"A failed {fault} import must not be recorded as imported");
            Check.That(app.Remote.Requests.All(x => x.Method == "GET"), "Even failed import must only read staging");
        }
    }

    public static async Task PreviewReportsZeroPriceDatesWithoutInventingADefault()
    {
        await using var app = new OnboardingFixture(SnapshotFault.UnpricedDates);
        var preview = await Success(await app.Client.GetAsync($"/api/channex/import-preview?propertyId={SnapshotHandler.PropertyId}"));
        foreach (var room in preview.GetProperty("rooms").EnumerateArray())
        {
            var index = room.GetProperty("externalId").GetString() == SnapshotHandler.RoomIds[0] ? 0 : 1;
            var rate = room.GetProperty("ratePlans")[0];
            Check.Equal(SnapshotHandler.Price(index, 1), rate.GetProperty("basePrice").GetDecimal(),
                "The preview candidate must come from the first positive actual price, skipping a zero first day");
            Check.Equal(2, rate.GetProperty("missingPriceCount").GetInt32(), "Preview must count each unpriced date");
            var dates = rate.GetProperty("missingPriceDates").EnumerateArray().Select(x => x.GetString()).ToArray();
            Check.That(dates.SequenceEqual(new[] { app.Remote.Start.ToString("yyyy-MM-dd"), app.Remote.Start.AddDays(499).ToString("yyyy-MM-dd") }),
                "Preview must identify the exact zero-price dates across the full 500-day horizon");
        }
        await using var db = await app.DbAsync();
        Check.Equal(0, await db.Properties.CountAsync(), "A preview with unpriced days must remain read-only");
        Check.Equal(0, await db.Outbox.CountAsync(), "An unpriced preview must never publish defaults");
        Check.That(app.Remote.Requests.All(x => x.Method == "GET"), "Preview must only read staging");
    }

    public static async Task ExplicitDefaultsFillOnlyZeroPricesAndPersistAsLocalBasePrices()
    {
        await using var app = new OnboardingFixture(SnapshotFault.UnpricedDates);
        var missing = await app.Client.PostAsJsonAsync("/api/channex/import", new { propertyId = SnapshotHandler.PropertyId });
        Check.Equal(HttpStatusCode.BadRequest, missing.StatusCode, "Unpriced dates must require an explicit local default instead of guessing");
        var defaults = new Dictionary<string, decimal>
        {
            [SnapshotHandler.RateIds[0]] = 189.95m,
            [SnapshotHandler.RateIds[1]] = 310.75m
        };
        var imported = await Success(await app.Client.PostAsJsonAsync("/api/channex/import", new
        {
            propertyId = SnapshotHandler.PropertyId, missingPriceDefaults = defaults
        }));
        await using var db = await app.DbAsync();
        var propertyId = imported.GetProperty("propertyId").GetGuid();
        var property = await db.Properties.SingleAsync(x => x.Id == propertyId);
        var rooms = await db.RoomTypes.Where(x => x.PropertyId == propertyId).ToListAsync();
        var roomIds = rooms.Select(x => x.Id).ToArray();
        var rates = await db.RatePlans.Where(x => roomIds.Contains(x.RoomTypeId)).ToListAsync();
        foreach (var room in rooms)
        {
            var index = room.Name == "Garden Double" ? 0 : 1;
            var rate = rates.Single(x => x.RoomTypeId == room.Id);
            var expectedDefault = defaults[SnapshotHandler.RateIds[index]];
            Check.Equal(expectedDefault, rate.BasePrice, "An explicitly approved default must become the local base price");
            var days = await db.DailyRates.Where(x => x.RatePlanId == rate.Id).OrderBy(x => x.Date).ToListAsync();
            Check.Equal(500, days.Count, "Defaults import must retain the full calendar");
            for (var day = 0; day < days.Count; day++)
            {
                Check.Equal(day is 0 or 499 ? expectedDefault : SnapshotHandler.Price(index, day), days[day].Rate,
                    "A default must fill only actual zero-price dates and preserve every positive source price");
                Check.Equal(1 + day % 3, days[day].MinStayArrival, "Initializing prices must preserve existing restrictions");
                Check.Equal(day % 9 == 0, days[day].StopSell, "Initializing prices must preserve stop-sell dates");
            }
        }
        Check.That(property.ChannexId == null && rooms.All(x => x.ChannexId == null) && rates.All(x => x.ChannexId == null),
            "Explicit local price defaults must not activate synchronization");
        Check.Equal(0, await db.Outbox.CountAsync(), "Initializing draft prices must not enqueue remote writes");
        Check.That(app.Remote.Requests.All(x => x.Method == "GET"), "Initializing a local draft must never write to Channex");
    }

    public static async Task EntirelyUnpricedPlansRequireUserDefaultsAndHaveNoSuggestedPrice()
    {
        await using var app = new OnboardingFixture(SnapshotFault.AllUnpriced);
        var preview = await Success(await app.Client.GetAsync($"/api/channex/import-preview?propertyId={SnapshotHandler.PropertyId}"));
        foreach (var rate in preview.GetProperty("rooms").EnumerateArray().SelectMany(x => x.GetProperty("ratePlans").EnumerateArray()))
        {
            Check.Equal(JsonValueKind.Null, rate.GetProperty("basePrice").ValueKind, "An entirely unpriced plan has no real candidate price");
            Check.Equal(500, rate.GetProperty("missingPriceCount").GetInt32(), "Every zero date must be reported to the user");
            Check.Equal(500, rate.GetProperty("missingPriceDates").GetArrayLength(), "No unpriced dates may be hidden from preview");
        }
        var missing = await app.Client.PostAsJsonAsync("/api/channex/import", new { propertyId = SnapshotHandler.PropertyId });
        Check.Equal(HttpStatusCode.BadRequest, missing.StatusCode, "Entirely unpriced plans cannot import without user input");
        await using (var unchanged = await app.DbAsync())
            Check.Equal(0, await unchanged.Properties.CountAsync(), "Missing defaults must not leave a partial property");
        await Success(await app.Client.PostAsJsonAsync("/api/channex/import", new
        {
            propertyId = SnapshotHandler.PropertyId,
            missingPriceDefaults = SnapshotHandler.RateIds.ToDictionary(x => x, _ => 205.25m)
        }));
        await using var db = await app.DbAsync();
        Check.Equal(1000, await db.DailyRates.CountAsync(x => x.Rate == 205.25m), "An explicit default must initialize all zero dates");
        Check.Equal(2, await db.RatePlans.CountAsync(x => x.BasePrice == 205.25m), "Both local bases must use the approved default");
        Check.Equal(0, await db.Outbox.CountAsync(), "Entirely unpriced draft import must remain local");
        Check.That(app.Remote.Requests.All(x => x.Method == "GET"), "Entirely unpriced import must only read Channex");
    }

    public static async Task InvalidOrForeignPriceDefaultsCannotCreatePartialDrafts()
    {
        var valid = SnapshotHandler.RateIds.ToDictionary(x => x, _ => 200m);
        var cases = new List<Dictionary<string, decimal>>
        {
            new() { [SnapshotHandler.RateIds[0]] = 200m },
            new(valid) { [SnapshotHandler.RateIds[0]] = 0m },
            new(valid) { [SnapshotHandler.RateIds[0]] = -1m },
            new(valid) { [SnapshotHandler.RateIds[0]] = 1_000_000.01m },
            new(valid) { [SnapshotHandler.RateIds[0]] = 200.001m },
            new(valid) { ["not-a-rate-uuid"] = 200m },
            new(valid) { ["99999999-9999-4999-8999-999999999999"] = 200m }
        };
        foreach (var defaults in cases)
        {
            await using var app = new OnboardingFixture(SnapshotFault.UnpricedDates);
            var original = await app.SeedLocalAsync("BRL", withRoom: false);
            var response = await app.Client.PostAsJsonAsync("/api/channex/import", new
            {
                propertyId = SnapshotHandler.PropertyId, missingPriceDefaults = defaults
            });
            Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Invalid, missing or foreign price defaults must return a client error");
            await using var db = await app.DbAsync();
            Check.Equal(1, await db.Properties.CountAsync(), "Invalid defaults must preserve only the existing local property");
            Check.Equal("BRL", (await db.Properties.SingleAsync(x => x.Id == original.PropertyId)).Currency,
                "Invalid defaults must not change the existing property's currency");
            Check.Equal(0, await db.RoomTypes.CountAsync(), "Invalid defaults must not leave rooms");
            Check.Equal(0, await db.RatePlans.CountAsync(), "Invalid defaults must not leave rates");
            Check.Equal(0, await db.DailyInventories.CountAsync(), "Invalid defaults must not leave inventory");
            Check.Equal(0, await db.DailyRates.CountAsync(), "Invalid defaults must not leave calendar prices");
            Check.Equal(0, await db.SyncAttempts.CountAsync(), "Invalid defaults must not create an import audit");
            Check.Equal(0, await db.Outbox.CountAsync(), "Invalid defaults must never enqueue synchronization");
            Check.That(app.Remote.Requests.All(x => x.Method == "GET"), "Rejected defaults must never write remotely");
        }
    }

    public static async Task FullSyncAndMappingRejectIncompleteSetupWithoutQueuingWork()
    {
        await using var app = new OnboardingFixture();
        var empty = await app.SeedLocalAsync("GBP", withRoom: false);
        var response = await app.Client.PostAsJsonAsync($"/api/properties/{empty.PropertyId}/full-sync", new { });
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Full sync of an empty property must explain that room setup is missing");
        var unmapped = await app.SeedLocalAsync("GBP");
        response = await app.Client.PostAsJsonAsync($"/api/properties/{unmapped.PropertyId}/full-sync", new { });
        Check.Equal(HttpStatusCode.Conflict, response.StatusCode, "Full sync must reject a property with incomplete mappings");
        var mismatched = await app.SeedLocalAsync("BRL");
        response = await app.Client.PutAsJsonAsync($"/api/properties/{mismatched.PropertyId}/mapping", Mapping(mismatched));
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Mapping must reject a currency mismatch");
        var mismatchError = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";
        Check.That(mismatchError.Contains("BRL", StringComparison.Ordinal) && mismatchError.Contains("GBP", StringComparison.Ordinal),
            "Currency mismatch errors must identify the local and staging currencies");
        response = await app.Client.PutAsJsonAsync($"/api/properties/{unmapped.PropertyId}/mapping",
            new MappingInput(SnapshotHandler.PropertyId, [new(unmapped.RoomId, null)], [new(unmapped.RateId, SnapshotHandler.RateIds[0])]));
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "A property mapping must not be saved with an unmapped room");
        response = await app.Client.PutAsJsonAsync($"/api/properties/{unmapped.PropertyId}/mapping",
            new MappingInput(SnapshotHandler.PropertyId, [new(unmapped.RoomId, SnapshotHandler.RoomIds[0])], [new(unmapped.RateId, null)]));
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "A property mapping must not be saved with an unmapped rate");
        response = await app.Client.PutAsJsonAsync($"/api/properties/{unmapped.PropertyId}/mapping",
            new MappingInput(SnapshotHandler.PropertyId, [new(unmapped.RoomId, SnapshotHandler.RoomIds[0])], [new(unmapped.RateId, SnapshotHandler.RateIds[1])]));
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "A mapped rate must belong to its mapped room");
        response = await app.Client.PutAsJsonAsync($"/api/properties/{unmapped.PropertyId}/mapping",
            new MappingInput(SnapshotHandler.PropertyId, [new(unmapped.RoomId, Guid.NewGuid().ToString())], [new(unmapped.RateId, SnapshotHandler.RateIds[0])]));
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Unknown remote room IDs must not be accepted");
        await using var db = await app.DbAsync();
        Check.That((await db.Properties.ToListAsync()).All(x => x.ChannexId == null), "Rejected setup must leave all property mappings unchanged");
        Check.That((await db.RoomTypes.ToListAsync()).All(x => x.ChannexId == null), "Rejected setup must leave all room mappings unchanged");
        Check.That((await db.RatePlans.ToListAsync()).All(x => x.ChannexId == null), "Rejected setup must leave all rate mappings unchanged");
        Check.Equal(0, await db.Outbox.CountAsync(), "Rejected setup must never queue synchronization");
    }

    public static async Task CompleteMappingQueuesSavedStateWithoutAWebhookSecret()
    {
        await using var app = new OnboardingFixture();
        var seed = await app.SeedLocalAsync("GBP");
        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/status");
        Check.That(!status.GetProperty("webhookConfigured").GetBoolean(), "This regression must exercise outgoing ARI without a webhook secret");
        var mapping = await Success(await app.Client.PutAsJsonAsync($"/api/properties/{seed.PropertyId}/mapping", Mapping(seed)));
        Check.That(mapping.GetProperty("fullSyncQueued").GetBoolean(), "A valid complete mapping must queue the initial saved state");
        await using var db = await app.DbAsync();
        Check.Equal(SnapshotHandler.PropertyId, (await db.Properties.SingleAsync()).ChannexId,
            "Successful mapping must save the selected property identifier");
        Check.Equal(1000, await db.Outbox.CountAsync(), "One mapped room and rate must queue 500 availability and 500 rate dates");
        Check.That(app.Remote.Requests.All(x => x.Method == "GET"), "Saving mapping must queue durable work before background delivery");
    }

    private static MappingInput Mapping(LocalSetup local) => new(SnapshotHandler.PropertyId,
        [new(local.RoomId, SnapshotHandler.RoomIds[0])], [new(local.RateId, SnapshotHandler.RateIds[0])]);
    private static Task<JsonElement> Import(HttpClient client) => Success(client.PostAsJsonAsync("/api/channex/import", new { propertyId = SnapshotHandler.PropertyId }));
    private static async Task<JsonElement> Success(Task<HttpResponseMessage> response) => await Success(await response);
    private static async Task<JsonElement> Success(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Check.That(response.IsSuccessStatusCode, $"Expected success; got {(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private sealed record LocalSetup(Guid PropertyId, Guid RoomId, Guid RateId);

    // All application HTTP routes run through the real entry point, using an isolated SQLite DB.
    // Only the remote HTTP transport and scheduled background execution are replaced for tests.
    private sealed class OnboardingFixture : IAsyncDisposable
    {
        public const string FakeKey = "onboarding-test-only-not-a-real-key";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "stayline-onboarding-checks-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> original = new();
        private readonly WebApplicationFactory<PmsService> factory;
        public SnapshotHandler Remote { get; }
        public HttpClient Client { get; }

        public OnboardingFixture(SnapshotFault fault = SnapshotFault.None)
        {
            Remote = new SnapshotHandler(fault);
            foreach (var pair in new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory,
                ["Channex__Enabled"] = "true",
                ["Channex__ApiKey"] = FakeKey,
                ["Channex__WebhookSecret"] = "",
                ["Logging__LogLevel__Default"] = "None"
            })
            {
                original[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            factory = new WebApplicationFactory<PmsService>().WithWebHostBuilder(builder =>
                builder.UseEnvironment("Testing").UseContentRoot(FindApiRoot()).ConfigureServices(services =>
                {
                    foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                                 (x.ImplementationType == typeof(ChannexSyncWorker) || x.ImplementationType == typeof(BookingInboxWorker)
                                  || x.ImplementationFactory != null)).ToArray())
                        services.Remove(service);
                    services.AddHttpClient<ChannexClient>().ConfigurePrimaryHttpMessageHandler(() => Remote);
                }));
            Client = factory.CreateClient();
        }

        public Task<AppDb> DbAsync() => factory.Services.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();
        public async Task<LocalSetup> SeedLocalAsync(string currency, bool withRoom = true, bool mapped = false)
        {
            await using var db = await DbAsync();
            var property = new Property { Name = "Local " + currency, Currency = currency, TimeZoneId = "UTC", ChannexId = mapped ? SnapshotHandler.PropertyId : null };
            var room = new RoomType { PropertyId = property.Id, Name = "Garden Double", Quantity = 5, Occupancy = 2,
                ChannexId = mapped ? SnapshotHandler.RoomIds[0] : null };
            var rate = new RatePlan { RoomTypeId = room.Id, Name = "Flexible", BasePrice = 190.50m,
                ChannexId = mapped ? SnapshotHandler.RateIds[0] : null };
            db.Properties.Add(property);
            if (withRoom) db.AddRange(room, rate);
            await db.SaveChangesAsync();
            return new(property.Id, room.Id, rate.Id);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        private static string FindApiRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var path = new DirectoryInfo(start); path != null; path = path.Parent)
            {
                var candidate = Path.Combine(path.FullName, "src", "Stayline.Api");
                if (File.Exists(Path.Combine(candidate, "Stayline.Api.csproj"))) return candidate;
            }
            throw new InvalidOperationException("Run the checks from the Stayline solution directory.");
        }
    }

    private enum SnapshotFault { None, MissingDate, MissingPriceField, InvalidPrice, ForeignRoom, ForeignRate, UnknownRoom, EchoedHttpFailure, UnpricedDates, AllUnpriced }
    private sealed record RecordedRequest(string Method, string Path);

    private sealed class SnapshotHandler(SnapshotFault fault) : HttpMessageHandler
    {
        public const string PropertyId = "11111111-1111-4111-8111-111111111111";
        public static readonly string[] RoomIds = ["22222222-2222-4222-8222-222222222222", "22222222-2222-4222-8222-222222222223"];
        public static readonly string[] RateIds = ["33333333-3333-4333-8333-333333333333", "33333333-3333-4333-8333-333333333334"];
        public static readonly int[] Quantities = [5, 1];
        public DateOnly Start { get; } = DateOnly.FromDateTime(DateTime.UtcNow);
        public List<RecordedRequest> Requests { get; } = [];
        public static int Available(int room, int day) => room == 0 ? 2 + day % 4 : day % 2;
        public static decimal Price(int room, int day) => (room == 0 ? 175.25m : 260.50m) + day % (room == 0 ? 7 : 11);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(new(request.Method.Method, uri.PathAndQuery));
            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException("Onboarding checks prohibit outbound staging writes.");
            var path = uri.AbsolutePath.Split('/').Last();
            if (fault == SnapshotFault.EchoedHttpFailure)
                return Task.FromResult(RecordingHandler.Json(HttpStatusCode.BadGateway, JsonSerializer.Serialize(new { error = OnboardingFixture.FakeKey })));
            object payload;
            if (path is "properties" || path == PropertyId)
            {
                var property = new { id = PropertyId, type = "property", attributes = new { title = "Demo Property", currency = "GBP", timezone = "UTC", property_type = "hotel" } };
                payload = path == PropertyId ? new { data = (object)property } : new { data = (object)new[] { property }, meta = new { total = 1 } };
            }
            else if (path == "room_types")
            {
                payload = new { data = RoomIds.Select((id, i) => new { id, type = "room_type", attributes = new
                {
                    title = i == 0 ? "Garden Double" : "City Apartment", count_of_rooms = Quantities[i], default_occupancy = 1,
                    occ_adults = i == 0 ? 2 : 3, occ_children = 0, occ_infants = 0, room_kind = "room"
                }, relationships = new { property = new { data = new { id = fault == SnapshotFault.ForeignRoom ? "99999999-9999-4999-8999-999999999999" : PropertyId } } } }).ToArray(), meta = new { total = 2 } };
            }
            else if (path == "rate_plans")
            {
                payload = new { data = RateIds.Select((id, i) => new { id, type = "rate_plan", attributes = new
                {
                    title = i == 0 ? "Flexible" : "Apartment Flexible", currency = "GBP", sell_mode = "per_room", rate_mode = "manual"
                }, relationships = new
                {
                    property = new { data = new { id = fault == SnapshotFault.ForeignRate ? "99999999-9999-4999-8999-999999999999" : PropertyId } },
                    room_type = new { data = new { id = fault == SnapshotFault.UnknownRoom ? "99999999-9999-4999-8999-999999999999" : RoomIds[i] } }
                } }).ToArray(), meta = new { total = 2 } };
            }
            else if (path == "availability")
            {
                payload = new { data = RoomIds.Select((id, i) => (id, i)).ToDictionary(x => x.id,
                    x => Enumerable.Range(0, 500).Where(day => fault != SnapshotFault.MissingDate || day != 173)
                        .ToDictionary(day => Start.AddDays(day).ToString("yyyy-MM-dd"), day => Available(x.i, day))) };
            }
            else if (path == "restrictions")
            {
                payload = new { data = RateIds.Select((id, i) => (id, i)).ToDictionary(x => x.id,
                    x => Enumerable.Range(0, 500).ToDictionary(day => Start.AddDays(day).ToString("yyyy-MM-dd"), day => new
                    {
                        rate = fault == SnapshotFault.AllUnpriced && day % 2 == 0 || fault == SnapshotFault.UnpricedDates && day == 499 ? (object)0 :
                            fault == SnapshotFault.InvalidPrice && day == 499 ? "-0.01" :
                            fault == SnapshotFault.AllUnpriced || fault == SnapshotFault.UnpricedDates && day is 0 or 499
                                ? "0.00" : Price(x.i, day).ToString("0.00", CultureInfo.InvariantCulture),
                        min_stay_arrival = 1 + day % 3, min_stay_through = 1 + day % 2, max_stay = 14,
                        stop_sell = day % 9 == 0, closed_to_arrival = day % 11 == 0, closed_to_departure = day % 13 == 0
                    })) };
            }
            else throw new InvalidOperationException("Unexpected staging route: " + uri.PathAndQuery);
            var json = JsonSerializer.Serialize(payload);
            if (fault == SnapshotFault.MissingPriceField && path == "restrictions")
            {
                var incomplete = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                incomplete["data"]![RateIds[0]]![Start.AddDays(499).ToString("yyyy-MM-dd")]!.AsObject().Remove("rate");
                json = incomplete.ToJsonString();
            }
            return Task.FromResult(RecordingHandler.Json(HttpStatusCode.OK, json));
        }
    }
}
