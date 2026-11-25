using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Stayline;

namespace Stayline.Checks;

internal static class HttpChecks
{
    public static async Task RealEndpointsPersistCalendarAndReservationLifecycle()
    {
        await using var app = new ApiFixture();
        var client = app.Client;
        var property = await CreateProperty(client);
        var room = await ReadSuccess<RoomType>(await client.PostAsJsonAsync($"/api/properties/{property.Id}/rooms",
            new CreateRoomInput("Garden Double", 5, 2)));
        var plan = await ReadSuccess<RatePlan>(await client.PostAsJsonAsync($"/api/rooms/{room.Id}/rates",
            new CreateRateInput("Flexible", 189.95m)));
        var date = PmsService.Today(property).AddDays(14);
        await ReadSuccess<JsonElement>(await client.PostAsJsonAsync($"/api/properties/{property.Id}/calendar", new CalendarInput([
            new(null, plan.Id, date, date.AddDays(1), Rate: 244.75m, MinStayArrival: 3),
            new(room.Id, null, date, date.AddDays(1), Capacity: 3)
        ])));
        var calendarPath = $"/api/properties/{property.Id}/calendar?from={date:yyyy-MM-dd}&days=3";
        var calendar = await client.GetFromJsonAsync<JsonElement>(calendarPath);
        var rateDay = calendar.GetProperty("rooms")[0].GetProperty("ratePlans")[0].GetProperty("days")[0];
        Check.Equal(244.75m, rateDay.GetProperty("rate").GetDecimal(), "The HTTP calendar must read back the saved price");
        Check.Equal(3, rateDay.GetProperty("minStayArrival").GetInt32(), "The HTTP calendar must read back the saved restriction");
        Check.Equal(3, Available(calendar, 0), "The HTTP calendar must read back the saved capacity");

        var reservation = await ReadSuccess<Reservation>(await client.PostAsJsonAsync($"/api/properties/{property.Id}/reservations",
            new ReservationInput("Taylor Morgan", room.Id, plan.Id, 2, date, date.AddDays(2))));
        calendar = await client.GetFromJsonAsync<JsonElement>(calendarPath);
        Check.Equal(1, Available(calendar, 0), "Creating a reservation through HTTP must reduce available inventory");
        Check.Equal(5, Available(calendar, 2), "The checkout date must remain available through the HTTP calendar");

        await ReadSuccess<Reservation>(await client.PutAsJsonAsync($"/api/reservations/{reservation.Id}",
            new ReservationInput("Taylor Morgan", room.Id, plan.Id, 1, date.AddDays(1), date.AddDays(3))));
        calendar = await client.GetFromJsonAsync<JsonElement>(calendarPath);
        Check.Equal(3, Available(calendar, 0), "Moving a reservation must release its previous arrival date");
        Check.Equal(2, Available(calendar, 1), "A modified reservation must consume its saved quantity");
        Check.Equal(4, Available(calendar, 2), "Extending a reservation must consume its new occupied date");

        await ReadSuccess<JsonElement>(await client.DeleteAsync($"/api/reservations/{reservation.Id}"));
        calendar = await client.GetFromJsonAsync<JsonElement>(calendarPath);
        Check.Equal(3, Available(calendar, 1), "Cancellation through HTTP must release the saved reservation");
        Check.Equal(5, Available(calendar, 2), "Cancellation must restore all affected capacity");
        var sync = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/sync");
        Check.That(sync.GetProperty("pending").GetInt32() > 1000, "Real HTTP edits must append to the durable outbox");
        Check.Equal(0, sync.GetProperty("attempts").GetArrayLength(), "Disabled configuration must not make Channex requests");
    }

    public static async Task MiddlewareRejectsCrossOriginAndMalformedRequests()
    {
        await using var app = new ApiFixture();
        var client = app.Client;
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Check.That(!status.GetProperty("configured").GetBoolean() && !status.GetProperty("enabled").GetBoolean(),
            "Default configuration must keep Channex disabled");
        var unknown = await client.GetAsync("/api/does-not-exist");
        Check.Equal(HttpStatusCode.NotFound, unknown.StatusCode, "Unknown API routes must return 404");
        Check.That((await unknown.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out _),
            "Unknown API routes must return JSON rather than the frontend HTML");

        var body = new CreatePropertyInput("Should never be created", "hotel", "BRL", "UTC");
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/properties") { Content = JsonContent.Create(body) };
        crossOrigin.Headers.TryAddWithoutValidation("Origin", "https://untrusted.example");
        Check.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossOrigin)).StatusCode,
            "The real middleware must reject cross-origin writes");
        var wrongType = await client.PostAsync("/api/properties", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain"));
        Check.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode, "Operator writes must require application/json");
        var properties = await client.GetFromJsonAsync<JsonElement>("/api/properties");
        Check.Equal(0, properties.GetArrayLength(), "Rejected HTTP writes must not mutate application state");
        Check.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/channex/remote")).StatusCode,
            "Disabled remote lookup must explain that server configuration is required");
        var webhook = await client.PostAsJsonAsync("/api/channex/webhook",
            new { @event = "booking", payload = new { revision_id = Guid.NewGuid() } });
        Check.Equal(HttpStatusCode.ServiceUnavailable, webhook.StatusCode, "An unconfigured webhook must not accept notifications");
        var page = await client.GetAsync("/");
        Check.Equal(HttpStatusCode.OK, page.StatusCode, "The frontend must be served by the real app");
        Check.That(page.Headers.GetValues("Content-Security-Policy").Any(x => x.Contains("default-src 'self'")),
            "The real app must emit its content security policy");
    }

    public static async Task MalformedMappingCannotAlterSavedMappings()
    {
        await using var app = new ApiFixture();
        var client = app.Client;
        var property = await CreateProperty(client);
        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}/mapping",
            new { channexId = "not-a-uuid", rooms = Array.Empty<object>(), rates = Array.Empty<object>() });
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Malformed mapping identifiers must return a validation error");
        response = await client.PutAsJsonAsync($"/api/properties/{property.Id}/mapping",
            new { channexId = (string?)null, rooms = (object?)null, rates = Array.Empty<object>() });
        Check.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Missing mapping collections must return a validation error");
        var properties = await client.GetFromJsonAsync<JsonElement>("/api/properties");
        Check.Equal(JsonValueKind.Null, properties[0].GetProperty("channexId").ValueKind,
            "Rejected mapping requests must leave the saved property mapping unchanged");
    }

    private static int Available(JsonElement calendar, int index) => calendar.GetProperty("rooms")[0].GetProperty("days")[index].GetProperty("available").GetInt32();
    private static Task<Property> CreateProperty(HttpClient client) => ReadSuccess<Property>(client.PostAsJsonAsync("/api/properties",
        new CreatePropertyInput("HTTP Checks Hotel", "hotel", "BRL", "America/Sao_Paulo")));
    private static async Task<T> ReadSuccess<T>(Task<HttpResponseMessage> response) => await ReadSuccess<T>(await response);
    private static async Task<T> ReadSuccess<T>(HttpResponseMessage response)
    {
        Check.That(response.IsSuccessStatusCode, $"Expected HTTP success; got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // TestServer exercises the actual entry point, routes and middleware entirely in process.
    // It does not open a socket and has no permission to contact staging.
    private sealed class ApiFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "stayline-http-checks-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> original = new();
        private readonly WebApplicationFactory<PmsService> factory;
        public HttpClient Client { get; }

        public ApiFixture()
        {
            foreach (var pair in new Dictionary<string, string?>
            {
                ["DataDirectory"] = directory,
                ["Channex__Enabled"] = "false",
                ["Channex__ApiKey"] = "",
                ["Channex__WebhookSecret"] = "",
                ["Logging__LogLevel__Default"] = "Warning"
            })
            {
                original[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            factory = new WebApplicationFactory<PmsService>().WithWebHostBuilder(builder =>
                builder.UseEnvironment("Testing").UseContentRoot(FindApiRoot()));
            Client = factory.CreateClient();
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
            {
                for (var path = new DirectoryInfo(start); path != null; path = path.Parent)
                {
                    var candidate = Path.Combine(path.FullName, "src", "Stayline.Api");
                    if (File.Exists(Path.Combine(candidate, "Stayline.Api.csproj"))) return candidate;
                }
            }
            throw new InvalidOperationException("Run the checks from the Stayline solution directory.");
        }
    }
}
