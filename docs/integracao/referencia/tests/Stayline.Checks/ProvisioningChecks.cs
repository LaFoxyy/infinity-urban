using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stayline;

namespace Stayline.Checks;

internal static class ProvisioningChecks
{
    public static async Task LocalCalendarsWorkBeforeMappingAndKeepAllRestrictions()
    {
        await using var app = new Fixture();
        var brl = await app.CreateLocalAsync("Teste", "BRL", "America/Sao_Paulo");
        var gbp = await app.CreateLocalAsync("Imported GBP draft", "GBP", "Europe/London");
        var date = brl.Start.AddDays(8);
        await Success(app.Client.PostAsJsonAsync($"/api/properties/{brl.Property.Id}/calendar", new CalendarInput([
            new(brl.Rooms[0].Id, null, date, date, Capacity: 2),
            new(null, brl.Rates[0].Id, date, date, Rate: 267.45m, MinStayArrival: 3, MinStayThrough: 2,
                MaxStay: 9, StopSell: true, ClosedToArrival: true, ClosedToDeparture: true)
        ])));
        var localCalendar = await Calendar(app, brl, date);
        var otherCalendar = await Calendar(app, gbp, date);
        Check.Equal(2, localCalendar.GetProperty("rooms").GetArrayLength(), "An unmapped local property must have a usable calendar");
        Check.Equal(2, localCalendar.GetProperty("rooms")[0].GetProperty("ratePlans").GetArrayLength(),
            "A local room's multiple rate plans must be visible without a Channex mapping");
        var roomDay = localCalendar.GetProperty("rooms")[0].GetProperty("days")[0];
        Check.Equal(2, roomDay.GetProperty("available").GetInt32(), "Local inventory must read its own saved capacity");
        var rateDay = localCalendar.GetProperty("rooms")[0].GetProperty("ratePlans")[0].GetProperty("days")[0];
        AssertRestrictions(rateDay, snakeCase: false);
        Check.Equal(2, otherCalendar.GetProperty("rooms").GetArrayLength(), "Another property's calendar must remain independently accessible");
        Check.Equal(500.25m, otherCalendar.GetProperty("rooms")[0].GetProperty("ratePlans")[0].GetProperty("days")[0].GetProperty("rate").GetDecimal(),
            "Editing the BRL property must not alter a GBP property's prices");
        await app.Worker().ProcessPendingAsync();
        Check.Equal(0, app.Remote.Requests.Count, "Unmapped calendars must never send foreign or guessed Channex IDs");
    }

    public static async Task ProvisionUsesSavedEntitiesAndPublishesSavedCalendar()
    {
        await using var app = new Fixture();
        var local = await app.CreateLocalAsync("Saved BRL hotel", "BRL", "America/Sao_Paulo");
        var date = local.Start.AddDays(8);
        await Success(app.Client.PostAsJsonAsync($"/api/properties/{local.Property.Id}/calendar", new CalendarInput([
            new(local.Rooms[0].Id, null, date, date, Capacity: 2),
            new(null, local.Rates[0].Id, date, date, Rate: 267.45m, MinStayArrival: 3, MinStayThrough: 2,
                MaxStay: 9, StopSell: true, ClosedToArrival: true, ClosedToDeparture: true)
        ])));
        var result = await Provision(app, local);
        Check.Equal(1, result.GetProperty("createdProperties").GetInt32(), "Provision must create the real local property once");
        Check.Equal(2, result.GetProperty("createdRoomTypes").GetInt32(), "Provision must create both actual room types");
        Check.Equal(3, result.GetProperty("createdRatePlans").GetInt32(), "Provision must create every actual rate plan");
        Check.That(result.GetProperty("queuedChanges").GetInt32() > 0, "Provision must queue initial ARI only after mappings persist");
        var writes = app.Remote.Requests.Where(x => x.Method == "POST").ToArray();
        var propertyRequest = Json(writes.Single(x => x.Path == "/api/v1/properties").Body).GetProperty("property");
        Check.Equal(local.Property.Name, propertyRequest.GetProperty("title").GetString(), "Remote property name must come from the PMS DB");
        Check.Equal("BRL", propertyRequest.GetProperty("currency").GetString(), "Remote property currency must come from the PMS DB");
        Check.Equal("America/Sao_Paulo", propertyRequest.GetProperty("timezone").GetString(), "Remote timezone must come from the PMS DB");
        await using (var db = await app.DbAsync())
        {
            var saved = await db.Properties.SingleAsync(x => x.Id == local.Property.Id);
            Check.Equal(result.GetProperty("externalPropertyId").GetString(), saved.ChannexId, "Returned property UUID must be committed as the mapping");
            foreach (var room in await db.RoomTypes.Where(x => x.PropertyId == saved.Id).ToListAsync())
            {
                var body = writes.Where(x => x.Path == "/api/v1/room_types").Select(x => Json(x.Body).GetProperty("room_type"))
                    .Single(x => x.GetProperty("title").GetString() == room.Name);
                Check.Equal(saved.ChannexId, body.GetProperty("property_id").GetString(), "Room creation must reference the created property");
                Check.Equal(room.Quantity, body.GetProperty("count_of_rooms").GetInt32(), "Room creation must use the saved capacity");
                Check.Equal(room.Occupancy, body.GetProperty("occ_adults").GetInt32(), "Room creation must use the saved occupancy");
                Check.That(Guid.TryParse(room.ChannexId, out _), "Returned room UUIDs must be persisted");
                foreach (var rate in await db.RatePlans.Where(x => x.RoomTypeId == room.Id).ToListAsync())
                {
                    var rateBody = writes.Where(x => x.Path == "/api/v1/rate_plans").Select(x => Json(x.Body).GetProperty("rate_plan"))
                        .Single(x => x.GetProperty("title").GetString() == rate.Name);
                    Check.Equal(saved.ChannexId, rateBody.GetProperty("property_id").GetString(), "Rate creation must reference the created property");
                    Check.Equal(room.ChannexId, rateBody.GetProperty("room_type_id").GetString(), "Rate creation must use its own saved room mapping");
                    Check.Equal("BRL", rateBody.GetProperty("currency").GetString(), "Rate currency must match the local property");
                    Check.Equal("per_room", rateBody.GetProperty("sell_mode").GetString(), "Current pricing model must be reported accurately");
                    Check.That(Guid.TryParse(rate.ChannexId, out _), "Returned rate UUIDs must be persisted");
                }
            }
        }
        await app.Worker().ProcessPendingAsync();
        var availability = app.Remote.Requests.Single(x => x.Path == "/api/v1/availability");
        var restrictions = app.Remote.Requests.Single(x => x.Path == "/api/v1/restrictions");
        var inventoryValues = PayloadDates.Expand(Json(availability.Body));
        var rateValues = PayloadDates.Expand(Json(restrictions.Body));
        Check.Equal(1000, inventoryValues.Length, "Both room types must share one complete 500-day availability request");
        Check.Equal(1500, rateValues.Length, "All three rate plans must share one complete 500-day restrictions request");
        await using (var db = await app.DbAsync())
        {
            var rateId = (await db.RatePlans.SingleAsync(x => x.Id == local.Rates[0].Id)).ChannexId;
            AssertRestrictions(rateValues.Single(x => x.GetProperty("rate_plan_id").GetString() == rateId &&
                x.GetProperty("date").GetString() == date.ToString("yyyy-MM-dd")), snakeCase: true);
            Check.Equal(0, await db.Outbox.CountAsync(x => x.PropertyId == local.Property.Id && x.ProcessedAt == null),
                "Accepted initial ARI must complete the queued work");
        }
        foreach (var path in new[] { "/api/status", "/api/properties", $"/api/properties/{local.Property.Id}/sync" })
            Check.That(!(await app.Client.GetStringAsync(path)).Contains(Fixture.FakeKey, StringComparison.Ordinal),
                "Browser status, property lists and request history must not expose server credentials");
    }

    public static async Task ProvisionUnblocksMissingMappingsWithoutResettingTimedBackoff()
    {
        foreach (var keepTimedBackoff in new[] { false, true })
        {
            await using var app = new Fixture();
            var local = await app.CreateLocalAsync("Initially unmapped hotel", "BRL", "America/Sao_Paulo");
            await app.Worker().ProcessPendingAsync();
            Check.Equal(0, app.Remote.Requests.Count, "Unmapped queued state must not send invalid Channex requests");
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(2);
            await using (var db = await app.DbAsync())
            {
                var blocked = await db.SyncGates.Where(x => x.PropertyId == local.Property.Id).ToListAsync();
                Check.Equal(2, blocked.Count, "An unmapped worker pass must retain both kinds of blocked work");
                Check.That(blocked.All(x => x.NextAttemptAt == DateTimeOffset.MaxValue),
                    "Missing mappings must produce the permanent operator-resolution gates used in real operation");
                if (keepTimedBackoff)
                {
                    blocked.Single(x => x.Kind == "availability").NextAttemptAt = notBefore;
                    await db.SaveChangesAsync();
                }
            }
            await Provision(app, local);
            await using (var db = await app.DbAsync())
            {
                Check.Equal(0, await db.SyncGates.CountAsync(x => x.PropertyId == local.Property.Id && x.NextAttemptAt == DateTimeOffset.MaxValue),
                    "A newly complete provision must clear missing-mapping gates in its completion commit");
                if (keepTimedBackoff)
                    Check.Equal(notBefore, (await db.SyncGates.SingleAsync(x => x.PropertyId == local.Property.Id && x.Kind == "availability")).NextAttemptAt,
                        "Provisioning must preserve a timed rate-limit or network backoff");
            }
            // No manual Retry API call occurs here. The ordinary next worker pass must proceed.
            await app.Worker().ProcessPendingAsync();
            var ari = app.Remote.Requests.Where(x => x.Path is "/api/v1/availability" or "/api/v1/restrictions").ToArray();
            Check.Equal(keepTimedBackoff ? 1 : 2, ari.Length,
                "Provisioning must allow resolved work through while retaining legitimate timed backoff");
            Check.That(ari.Any(x => x.Path == "/api/v1/restrictions"), "Resolved rate mappings must send without a manual Retry action");
            await using var final = await app.DbAsync();
            Check.Equal(keepTimedBackoff ? 1000 : 0,
                await final.Outbox.Where(x => x.PropertyId == local.Property.Id && x.ProcessedAt == null).Select(x => new { x.Kind, x.EntityId, x.Date }).Distinct().CountAsync(),
                "Only deliberately deferred availability may remain queued after provision");
        }
    }

    public static async Task RepeatedAndConcurrentProvisionDoesNotDuplicateRemoteEntities()
    {
        await using var app = new Fixture();
        var local = await app.CreateLocalAsync("Concurrent hotel", "BRL", "America/Sao_Paulo");
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Provision(app, local)));
        var firstCount = app.Remote.Requests.Count(x => x.Method == "POST");
        Check.Equal(6, firstCount, "Concurrent provision requests must create each property, room and rate only once");
        Check.That(responses.Select(x => x.GetProperty("externalPropertyId").GetString()).Distinct().Count() == 1,
            "Concurrent calls must return the same persisted property mapping");
        await using var before = await app.DbAsync();
        var pending = await before.Outbox.CountAsync();
        var again = await Provision(app, local);
        Check.That(again.GetProperty("alreadyProvisioned").GetBoolean(), "A repeated completion must report an existing provision");
        Check.Equal(0, again.GetProperty("queuedChanges").GetInt32(), "A repeated provision must not enqueue another initial sync");
        Check.Equal(firstCount, app.Remote.Requests.Count(x => x.Method == "POST"), "A repeated provision must not recreate remote records");
        await using var after = await app.DbAsync();
        Check.Equal(pending, await after.Outbox.CountAsync(), "Repeated and concurrent completion must not duplicate queued ARI");

        // A real later addition changes the graph and must be published without recreating its parents.
        var room = await Success(app.Client.PostAsJsonAsync($"/api/properties/{local.Property.Id}/rooms", new CreateRoomInput("New suite", 1, 3)));
        await Success(app.Client.PostAsJsonAsync($"/api/rooms/{room.GetProperty("id").GetGuid()}/rates", new CreateRateInput("Suite flexible", 940.25m)));
        var addition = await Provision(app, local);
        Check.Equal(0, addition.GetProperty("createdProperties").GetInt32(), "Adding rooms later must reuse the existing remote property");
        Check.Equal(1, addition.GetProperty("createdRoomTypes").GetInt32(), "Adding rooms later must create exactly the new room");
        Check.Equal(1, addition.GetProperty("createdRatePlans").GetInt32(), "Adding rooms later must create exactly the new rate");
        Check.That(!addition.GetProperty("alreadyProvisioned").GetBoolean() && addition.GetProperty("queuedChanges").GetInt32() > 0,
            "The enlarged saved graph needs an initial horizon for the newly mapped entities");
        Check.Equal(8, app.Remote.Requests.Count(x => x.Method == "POST"), "A later addition must preserve all previous remote entities");
    }

    public static async Task KnownCreateFailureResumesFromPersistedMappings()
    {
        await using var app = new Fixture();
        var local = await app.CreateLocalAsync("Resumable hotel", "BRL", "America/Sao_Paulo");
        app.Remote.FailNextRate = true;
        var failed = await app.Client.PostAsJsonAsync($"/api/properties/{local.Property.Id}/channex/provision", new { });
        Check.That(!failed.IsSuccessStatusCode, "Rejected rate creation must be reported as failure");
        Check.That(!(await failed.Content.ReadAsStringAsync()).Contains(Fixture.FakeKey, StringComparison.Ordinal),
            "A remote validation error echoing the key must be redacted");
        var propertyWrites = app.Remote.Requests.Count(x => x.Method == "POST" && x.Path == "/api/v1/properties");
        var roomWrites = app.Remote.Requests.Count(x => x.Method == "POST" && x.Path == "/api/v1/room_types");
        await using (var db = await app.DbAsync())
        {
            Check.That((await db.Properties.SingleAsync()).ChannexId != null, "Successful property creation must survive a later rate failure");
            Check.That(!(string.Join("\n", (await db.SyncAttempts.ToListAsync()).Select(x => x.Detail + x.RequestJson))).Contains(Fixture.FakeKey, StringComparison.Ordinal),
                "Provision audit history must redact the rejected response credential");
        }
        await Provision(app, local);
        Check.Equal(propertyWrites, app.Remote.Requests.Count(x => x.Method == "POST" && x.Path == "/api/v1/properties"),
            "Retry after a definite validation error must reuse the persisted property UUID");
        Check.Equal(2, app.Remote.Requests.Count(x => x.Method == "POST" && x.Path == "/api/v1/room_types"),
            "Retry must reuse earlier rooms and create only missing rooms");
        Check.That(roomWrites is >= 1 and <= 2, "The failure must happen after actual room creation");
        await using var final = await app.DbAsync();
        Check.Equal(3, await final.RatePlans.CountAsync(x => x.ChannexId != null), "Resume must finish all missing rates");
    }

    public static async Task UnknownCreateOutcomeBlocksBlindRetry()
    {
        foreach (var malformedBody in new string?[] { null, "{\"data\":{\"id\":\"not-a-valid-uuid\"}}", "[]", "{\"data\":[]}" })
        {
        await using var app = new Fixture();
        var local = await app.CreateLocalAsync("Uncertain hotel", "BRL", "America/Sao_Paulo");
        app.Remote.LoseNextRoomResponse = malformedBody == null;
        app.Remote.MalformedNextRoomBody = malformedBody;
        var failed = await app.Client.PostAsJsonAsync($"/api/properties/{local.Property.Id}/channex/provision", new { });
        Check.That(!failed.IsSuccessStatusCode, "A lost creation response must be surfaced to the operator");
        var writes = app.Remote.Requests.Count(x => x.Method == "POST");
        await using (var beforeRetry = await app.DbAsync())
        {
            // Give a newly-added unrelated room an earlier sort key. Pending uncertainty must be
            // checked for the entire graph before the service creates any otherwise-safe sibling.
            var newRoom = new RoomType { Id = Guid.Parse("00000000-0000-4000-8000-000000000001"), PropertyId = local.Property.Id,
                Name = "Added while reconciling", Quantity = 1, Occupancy = 2 };
            beforeRetry.RoomTypes.Add(newRoom);
            beforeRetry.RatePlans.Add(new RatePlan { RoomTypeId = newRoom.Id, Name = "Sibling plan", BasePrice = 575.25m });
            await beforeRetry.SaveChangesAsync();
        }
        var retry = await app.Client.PostAsJsonAsync($"/api/properties/{local.Property.Id}/channex/provision", new { });
        Check.Equal(HttpStatusCode.Conflict, retry.StatusCode, "An uncertain non-idempotent create must require reconciliation");
        Check.Equal(writes, app.Remote.Requests.Count(x => x.Method == "POST"), "Blind retry must not duplicate a possibly-created remote room");
        await using var db = await app.DbAsync();
        Check.That(await db.SyncAttempts.AnyAsync(x => x.Status == "provision-uncertain"), "The uncertain outcome must survive as durable audit state");
        Check.Equal(0, await db.RatePlans.CountAsync(x => x.ChannexId != null), "No rates may be provisioned past an uncertain room mapping");
        }
    }

    public static async Task ForeignMappingsCannotBeUsedForProvision()
    {
        await using var app = new Fixture();
        var local = await app.CreateLocalAsync("Foreign mapping hotel", "BRL", "America/Sao_Paulo");
        await Provision(app, local);
        var writes = app.Remote.Requests.Count(x => x.Method == "POST");
        app.Remote.ForeignRoom = true;
        var response = await app.Client.PostAsJsonAsync($"/api/properties/{local.Property.Id}/channex/provision", new { });
        Check.That(!response.IsSuccessStatusCode, "A room whose remote property changed must not be trusted");
        Check.Equal(writes, app.Remote.Requests.Count(x => x.Method == "POST"), "Invalid ancestry must stop creation before further writes");
    }

    private static void AssertRestrictions(JsonElement day, bool snakeCase)
    {
        Check.Equal(267.45m, day.GetProperty("rate").ValueKind == JsonValueKind.String ? decimal.Parse(day.GetProperty("rate").GetString()!, System.Globalization.CultureInfo.InvariantCulture) : day.GetProperty("rate").GetDecimal(),
            "Prices must retain the saved decimal amount");
        Check.Equal(3, day.GetProperty(snakeCase ? "min_stay_arrival" : "minStayArrival").GetInt32(), "Minimum stay on arrival must persist and propagate");
        Check.Equal(2, day.GetProperty(snakeCase ? "min_stay_through" : "minStayThrough").GetInt32(), "Minimum stay through must persist and propagate");
        Check.Equal(9, day.GetProperty(snakeCase ? "max_stay" : "maxStay").GetInt32(), "Maximum stay must persist and propagate");
        Check.That(day.GetProperty(snakeCase ? "stop_sell" : "stopSell").GetBoolean(), "Stop sell must persist and propagate");
        Check.That(day.GetProperty(snakeCase ? "closed_to_arrival" : "closedToArrival").GetBoolean(), "CTA must persist and propagate");
        Check.That(day.GetProperty(snakeCase ? "closed_to_departure" : "closedToDeparture").GetBoolean(), "CTD must persist and propagate");
    }

    private static Task<JsonElement> Calendar(Fixture app, Local local, DateOnly date) => Success(app.Client.GetAsync($"/api/properties/{local.Property.Id}/calendar?from={date:yyyy-MM-dd}&days=1"));
    private static Task<JsonElement> Provision(Fixture app, Local local) => Success(app.Client.PostAsJsonAsync($"/api/properties/{local.Property.Id}/channex/provision", new { }));
    private static async Task<JsonElement> Success(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Check.That(response.IsSuccessStatusCode, $"Expected HTTP success; got {(int)response.StatusCode}: {text}");
        return Json(text);
    }
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private sealed record Local(Property Property, RoomType[] Rooms, RatePlan[] Rates, DateOnly Start);

    private sealed class Fixture : IAsyncDisposable
    {
        public const string FakeKey = "provision-test-only-never-a-real-key";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "stayline-provision-checks-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> original = new();
        private readonly WebApplicationFactory<PmsService> factory;
        public RemoteHandler Remote { get; } = new();
        public HttpClient Client { get; }
        public Fixture()
        {
            foreach (var pair in new Dictionary<string, string?> { ["DataDirectory"] = directory, ["Channex__Environment"] = "staging", ["Channex__Enabled"] = "true", ["Channex__ApiKey"] = FakeKey,
                ["Channex__WebhookSecret"] = "", ["Channex__BatchSeconds"] = "0", ["Logging__LogLevel__Default"] = "None" })
            { original[pair.Key] = Environment.GetEnvironmentVariable(pair.Key); Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
            factory = new WebApplicationFactory<PmsService>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing").UseContentRoot(FindApiRoot()).ConfigureServices(services =>
            {
                foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                    (x.ImplementationType == typeof(ChannexSyncWorker) || x.ImplementationType == typeof(BookingInboxWorker)
                     || x.ImplementationFactory != null)).ToArray()) services.Remove(service);
                services.AddHttpClient<ChannexClient>().ConfigurePrimaryHttpMessageHandler(() => Remote);
            }));
            Client = factory.CreateClient();
        }
        public Task<AppDb> DbAsync() => factory.Services.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();
        public ChannexSyncWorker Worker() => new(factory.Services.GetRequiredService<IDbContextFactory<AppDb>>(), factory.Services.GetRequiredService<MutationLock>(),
            factory.Services.GetRequiredService<ChannexClient>(), factory.Services.GetRequiredService<IOptions<ChannexOptions>>(), NullLogger<ChannexSyncWorker>.Instance);
        public async Task<Local> CreateLocalAsync(string name, string currency, string timezone)
        {
            var property = JsonSerializer.Deserialize<Property>(await Success(Client.PostAsJsonAsync("/api/properties", new CreatePropertyInput(name, "hotel", currency, timezone))), JsonSerializerOptions.Web)!;
            var first = JsonSerializer.Deserialize<RoomType>(await Success(Client.PostAsJsonAsync($"/api/properties/{property.Id}/rooms", new CreateRoomInput("Garden Double", 5, 2))), JsonSerializerOptions.Web)!;
            var second = JsonSerializer.Deserialize<RoomType>(await Success(Client.PostAsJsonAsync($"/api/properties/{property.Id}/rooms", new CreateRoomInput("Terrace Family", 2, 4))), JsonSerializerOptions.Web)!;
            var rates = new List<RatePlan>();
            foreach (var item in new[] { (first.Id, "Flexible", 500.25m), (first.Id, "Non refundable", 450.50m), (second.Id, "Family standard", 800.75m) })
                rates.Add(JsonSerializer.Deserialize<RatePlan>(await Success(Client.PostAsJsonAsync($"/api/rooms/{item.Id}/rates", new CreateRateInput(item.Item2, item.Item3))), JsonSerializerOptions.Web)!);
            return new(property, [first, second], rates.ToArray(), PmsService.Today(property));
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await factory.DisposeAsync();
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        private static string FindApiRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var path = new DirectoryInfo(start); path != null; path = path.Parent)
            { var candidate = Path.Combine(path.FullName, "src", "Stayline.Api"); if (File.Exists(Path.Combine(candidate, "Stayline.Api.csproj"))) return candidate; }
            throw new InvalidOperationException("Run the checks from the Stayline solution directory.");
        }
    }

    // Only the outbound transport is replaced. Tests exercise the real API, DB, mutation lock and worker.
    private sealed class RemoteHandler : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];
        private readonly Dictionary<string, JsonElement> properties = new(), rooms = new(), rates = new();
        public bool FailNextRate { get; set; }
        public bool LoseNextRoomResponse { get; set; }
        public string? MalformedNextRoomBody { get; set; }
        public bool ForeignRoom { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check.Equal("staging.channex.io", request.RequestUri!.Host, "Every request must use the fixed staging host");
            Check.That(request.Headers.TryGetValues("user-api-key", out var keys) && keys.Single() == Fixture.FakeKey,
                "The staging credential must travel only in the server-side authentication header");
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Check.That(!body.Contains(Fixture.FakeKey, StringComparison.Ordinal), "API credentials must not appear in JSON payloads");
            var path = request.RequestUri.AbsolutePath;
            Requests.Add(new(request.Method.Method, path, body, DateTimeOffset.UtcNow));
            var route = path["/api/v1/".Length..].Split('/');
            var collection = route[0];
            if (request.Method == HttpMethod.Post && collection is "availability" or "restrictions")
                return RecordingHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new[] { new { id = RecordingHandler.TaskId, type = "task" } } }));
            var store = collection switch { "properties" => properties, "room_types" => rooms, "rate_plans" => rates, _ => throw new InvalidOperationException("Unexpected remote endpoint: " + path) };
            if (request.Method == HttpMethod.Post)
            {
                if (collection == "rate_plans" && FailNextRate)
                { FailNextRate = false; return RecordingHandler.Json(HttpStatusCode.UnprocessableEntity, JsonSerializer.Serialize(new { errors = new { title = "Validation failed", secret = Fixture.FakeKey } })); }
                var wrapper = collection switch { "properties" => "property", "room_types" => "room_type", _ => "rate_plan" };
                var attrs = Json(body).GetProperty(wrapper);
                var id = Guid.NewGuid().ToString();
                var relationships = new Dictionary<string, object>();
                if (attrs.TryGetProperty("property_id", out var property)) relationships["property"] = new { data = new { id = property.GetString() } };
                if (attrs.TryGetProperty("room_type_id", out var room)) relationships["room_type"] = new { data = new { id = room.GetString() } };
                var entity = Json(JsonSerializer.Serialize(new { id, type = wrapper, attributes = attrs, relationships }));
                store[id] = entity;
                if (collection == "room_types" && LoseNextRoomResponse)
                { LoseNextRoomResponse = false; throw new HttpRequestException("Simulated lost response after server committed the room."); }
                if (collection == "room_types" && MalformedNextRoomBody is { } malformedBody)
                { MalformedNextRoomBody = null; return RecordingHandler.Json(HttpStatusCode.Created, malformedBody); }
                return RecordingHandler.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { data = entity }));
            }
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Unexpected staging method");
            if (route.Length == 2)
            {
                var entity = store[route[1]];
                if (ForeignRoom && collection == "room_types")
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(entity.GetRawText())!;
                    node["relationships"]!["property"]!["data"]!["id"] = Guid.NewGuid().ToString();
                    node["attributes"]!["property_id"] = node["relationships"]!["property"]!["data"]!["id"]!.GetValue<string>();
                    entity = Json(node.ToJsonString());
                }
                return RecordingHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = entity }));
            }
            return RecordingHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = store.Values.ToArray(), meta = new { total = store.Count } }));
        }
    }
}
