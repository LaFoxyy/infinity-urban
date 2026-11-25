using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stayline;

namespace Stayline.Checks;

internal static class IncomingHttpChecks
{
    private const string BookingId = "55555555-5555-4555-8555-555555555555";
    private const string RevisionId = "66666666-6666-4666-8666-666666666666";

    public static async Task RecoveryEndpointImportsFeedAndExposesSafeLabeledReceipts()
    {
        await using var app = new Fixture();
        var property = await app.SeedAsync();
        var ackCalls = 0;
        app.Remote.Respond = async request =>
        {
            if (request.Method == "GET")
            {
                Check.Equal("/api/v1/booking_revisions/feed", request.Path, "Recovery UI must use the production feed consumer");
                return Feed(property);
            }
            Check.Equal($"/api/v1/booking_revisions/{RevisionId}/ack", request.Path, "Recovery may acknowledge only its received revision");
            await using var db = await app.DbAsync();
            Check.Equal(1, await db.Reservations.CountAsync(), "Recovery must commit the actual booking before acknowledgment");
            ackCalls++;
            return RecordingHandler.Json(ackCalls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "{}");
        };
        var first = await app.Client.PostAsJsonAsync("/api/channex/bookings/check", new { });
        Check.Equal(HttpStatusCode.OK, first.StatusCode, "The normal recovery endpoint must run without a special certification route");
        var outcome = await first.Content.ReadFromJsonAsync<JsonElement>();
        Check.That(outcome.GetProperty("checkedFeed").GetBoolean() && outcome.GetProperty("pending").GetInt32() == 1,
            "The recovery result must truthfully report an acknowledgment still pending");
        await using (var db = await app.DbAsync())
        {
            var receipt = await db.BookingReceipts.SingleAsync();
            Check.That(receipt.PayloadJson != null && receipt.ProcessedAt != null && receipt.AckedAt == null,
                "The unacknowledged replay payload must exist only on the server");
            receipt.NextAttemptAt = DateTimeOffset.MinValue;
            await db.SaveChangesAsync();
        }
        var responseText = await app.Client.GetStringAsync($"/api/properties/{property.Id}/sync");
        var receiptJson = JsonDocument.Parse(responseText).RootElement.GetProperty("receipts")[0];
        Check.Equal(RevisionId, receiptJson.GetProperty("id").GetString(), "The browser must receive the actual revision ID");
        Check.Equal(BookingId, receiptJson.GetProperty("externalBookingId").GetString(), "The browser must receive the booking ID separately");
        Check.Equal("new", receiptJson.GetProperty("revisionStatus").GetString(), "The browser must distinguish the lifecycle stage");
        Check.Equal("feed", receiptJson.GetProperty("source").GetString(), "The browser must show actual receipt provenance");
        Check.That(!receiptJson.TryGetProperty("payloadJson", out _), "The sync API must omit private replay payloads even before acknowledgment");
        Check.That(!responseText.Contains("Trusted operational", StringComparison.Ordinal) && !responseText.Contains(Fixture.FakeKey, StringComparison.Ordinal),
            "Receipt evidence must not expose the guest snapshot or API key");
        var second = await app.Client.PostAsJsonAsync("/api/channex/bookings/check", new { });
        var retried = await second.Content.ReadFromJsonAsync<JsonElement>();
        Check.Equal(0, retried.GetProperty("pending").GetInt32(), "The normal recovery endpoint must retry a committed acknowledgment");
        Check.Equal(2, ackCalls, "A failed acknowledgment must retry without reimporting the booking");
        await using var final = await app.DbAsync();
        Check.Equal(1, await final.Reservations.CountAsync(), "Recovery retries must remain idempotent");
    }

    public static async Task AuthenticatedWebhookStoresOnlyNotificationAndThenUsesFeed()
    {
        await using var app = new Fixture();
        var property = await app.SeedAsync();
        var body = new { @event = "booking", payload = new { revision_id = RevisionId,
            customer = new { name = "Untrusted spoofed guest" }, guarantee = new { card_number = "UNTRUSTED-CARD" } } };
        var unauthorized = await app.Client.PostAsJsonAsync("/api/channex/webhook", body);
        Check.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode, "Webhook must reject an absent shared secret");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/channex/webhook") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Stayline-Webhook-Secret", Fixture.WebhookSecret);
        Check.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(request)).StatusCode, "Authenticated notification must be stored durably");
        await using (var db = await app.DbAsync())
        {
            var receipt = await db.BookingReceipts.SingleAsync();
            Check.That(receipt.Source == "webhook" && receipt.PayloadJson == null && receipt.ProcessedAt == null,
                "Webhook body must supply only a notification ID, never an authoritative booking snapshot");
            Check.Equal(0, await db.Reservations.CountAsync(), "Untrusted posted customer/card fields must not create a reservation");
        }
        app.Remote.Respond = remote => Task.FromResult(remote.Method == "GET" ? Feed(property) : RecordingHandler.Json(HttpStatusCode.OK, "{}"));
        var response = await app.Client.PostAsJsonAsync("/api/channex/bookings/check", new { });
        Check.Equal(HttpStatusCode.OK, response.StatusCode, "A notification must be recoverable through the same feed path");
        await using var final = await app.DbAsync();
        var saved = await final.Reservations.SingleAsync();
        Check.Equal("Trusted operational Guest", saved.GuestName, "Only trusted feed operational data may populate the booking");
        Check.That((await final.BookingReceipts.SingleAsync()).AckedAt != null, "The trusted feed revision must be committed and acknowledged");
        Check.That(app.Remote.Requests.Where(x => x.Method == "GET").All(x => x.Path == "/api/v1/booking_revisions/feed"),
            "Webhook handling must not fall back to list or by-ID fetching");
    }

    private static HttpResponseMessage Feed(Property property)
    {
        var start = PmsService.Today(property).AddDays(30);
        return RecordingHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[] { new { id = RevisionId, type = "booking_revision", attributes = new
            {
                property_id = SeededProperty.RemoteProperty, booking_id = BookingId, status = "new",
                inserted_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                arrival_date = start.ToString("yyyy-MM-dd"), departure_date = start.AddDays(2).ToString("yyyy-MM-dd"),
                customer = new { name = "Trusted operational", surname = "Guest" },
                rooms = new[] { new { room_type_id = SeededProperty.RemoteRoom, rate_plan_id = SeededProperty.RemoteRate,
                    checkin_date = start.ToString("yyyy-MM-dd"), checkout_date = start.AddDays(2).ToString("yyyy-MM-dd") } }
            } } }, meta = new { total = 1 }
        }));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string FakeKey = "incoming-http-test-not-a-real-key";
        public const string WebhookSecret = "incoming-http-test-webhook-secret";
        private readonly string folder = Path.Combine(Path.GetTempPath(), "stayline-incoming-http-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> original = new();
        private readonly WebApplicationFactory<PmsService> factory;
        public RecordingHandler Remote { get; } = new();
        public HttpClient Client { get; }

        public Fixture()
        {
            foreach (var pair in new Dictionary<string, string?>
            {
                ["DataDirectory"] = folder, ["Channex__Enabled"] = "true", ["Channex__ApiKey"] = FakeKey,
                ["Channex__WebhookSecret"] = WebhookSecret, ["Logging__LogLevel__Default"] = "None"
            })
            {
                original[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            factory = new WebApplicationFactory<PmsService>().WithWebHostBuilder(builder =>
                builder.UseEnvironment("Testing").UseContentRoot(Path.GetFullPath("src/Stayline.Api")).ConfigureServices(services =>
                {
                    foreach (var service in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                        (x.ImplementationType == typeof(ChannexSyncWorker) || x.ImplementationType == typeof(BookingInboxWorker) || x.ImplementationFactory != null)).ToArray())
                        services.Remove(service);
                    services.AddHttpClient<ChannexClient>().ConfigurePrimaryHttpMessageHandler(() => Remote);
                }));
            Client = factory.CreateClient();
        }

        public Task<AppDb> DbAsync() => factory.Services.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();
        public async Task<Property> SeedAsync()
        {
            await using var db = await DbAsync();
            var property = new Property { Name = "Incoming HTTP hotel", TimeZoneId = "UTC", ChannexId = SeededProperty.RemoteProperty };
            var room = new RoomType { PropertyId = property.Id, Name = "Double", Quantity = 5, ChannexId = SeededProperty.RemoteRoom };
            var rate = new RatePlan { RoomTypeId = room.Id, Name = "Flexible", BasePrice = 200m, ChannexId = SeededProperty.RemoteRate };
            db.AddRange(property, room, rate);
            await db.SaveChangesAsync();
            return property;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
