using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stayline;

namespace Stayline.Checks;

internal static class ProductionEnvironmentChecks
{
    public static async Task EnvironmentChangesOnlyHostAndPreservesApprovedRequests()
    {
        const string availability = "{\"values\":[{\"property_id\":\"11111111-1111-4111-8111-111111111111\",\"room_type_id\":\"22222222-2222-4222-8222-222222222222\",\"date_from\":\"2027-01-01\",\"date_to\":\"2027-01-04\",\"availability\":3}]}";
        const string restrictions = "{\"values\":[{\"property_id\":\"11111111-1111-4111-8111-111111111111\",\"rate_plan_id\":\"33333333-3333-4333-8333-333333333333\",\"date\":\"2027-01-02\",\"rate\":\"333.25\",\"min_stay_arrival\":3,\"min_stay_through\":3,\"closed_to_arrival\":false}]}";
        const string revision = "55555555-5555-4555-8555-555555555555";
        var traces = new List<TraceHandler>();
        foreach (var environment in new[] { "staging", "production" })
        {
            var handler = new TraceHandler();
            traces.Add(handler);
            using var http = new HttpClient(handler);
            var client = new ChannexClient(http, Options.Create(new ChannexOptions
                { Enabled = true, Environment = environment, ApiKey = TraceHandler.SyntheticKey }));
            await client.SendAriAsync("availability", availability);
            await client.SendAriAsync("restrictions", restrictions);
            await client.FetchBookingFeedAsync();
            await client.AckRevisionAsync(revision);
            Check.Equal(4, handler.Requests.Count, "Changing environments must not add or split requests");
            Check.That(handler.Requests.All(x => x.Host == (environment == "production" ? "app.channex.io" : "staging.channex.io")),
                "Every request must use only the selected allowlisted Channex host");
            Check.That(handler.Requests.All(x => x.ApiKeyPresent && x.Accept == "application/json"),
                "The approved API header contract must remain unchanged");
            Check.Equal(availability, handler.Requests[0].Body, "Availability JSON must be passed through byte-for-byte");
            Check.Equal(restrictions, handler.Requests[1].Body, "Rate/restriction JSON must be passed through byte-for-byte");
            Check.Equal("GET", handler.Requests[2].Method, "Booking reception must still use the feed GET");
            Check.Equal("/api/v1/booking_revisions/feed?order[inserted_at]=asc&pagination[page]=1&pagination[limit]=100",
                Uri.UnescapeDataString(handler.Requests[2].PathAndQuery), "Feed ordering and pagination must remain unchanged");
            Check.Equal("POST", handler.Requests[3].Method, "Acknowledgment must remain a POST");
            Check.Equal($"/api/v1/booking_revisions/{revision}/ack", handler.Requests[3].PathAndQuery,
                "The acknowledgment must keep its approved revision path");
        }
        for (var index = 0; index < 4; index++)
        {
            var staging = traces[0].Requests[index];
            var production = traces[1].Requests[index];
            Check.Equal(staging with { Host = production.Host }, production,
                "Only the destination host may differ between staging and production requests");
        }
    }

    public static async Task UnknownEnvironmentsAndPathEscapesCannotSendRequests()
    {
        var handler = new TraceHandler();
        using var http = new HttpClient(handler);
        var production = new ChannexClient(http, Options.Create(new ChannexOptions
            { Enabled = true, Environment = "production", ApiKey = TraceHandler.SyntheticKey }));
        foreach (var path in new[] { "../properties", "../../health", "properties/../properties", "https://example.invalid/properties",
            "//example.invalid/properties", "\\example.invalid/properties", "%2e%2e/properties", "properties#fragment",
            "bookings", "bookings/55555555-5555-4555-8555-555555555555", "booking_revisions",
            "booking_revisions/55555555-5555-4555-8555-555555555555" })
            await MustReject(() => production.ReadStagingJsonAsync(path), "Unsafe paths and forbidden booking reads must be rejected before transport");
        var unknown = new ChannexClient(http, Options.Create(new ChannexOptions
            { Enabled = true, Environment = "https://example.invalid", ApiKey = TraceHandler.SyntheticKey }));
        await MustReject(() => unknown.SendAriAsync("availability", "{\"values\":[]}"),
            "An unknown environment cannot redirect credentials to an arbitrary host");
        Check.Equal(0, handler.Requests.Count, "Rejected destinations and booking-list reads must make zero network requests");
    }

    public static async Task FreshProductionDatabaseBindsAndCannotSwitchEnvironment()
    {
        await using var factory = new TestDb();
        await using var db = factory.CreateDbContext();
        var production = new ChannexOptions { Environment = "production" };
        await ChannexEnvironmentGuard.EnsureAsync(db, production);
        await ChannexEnvironmentGuard.EnsureAsync(db, production);
        db.Properties.Add(new Property { Name = "Production property" });
        await db.SaveChangesAsync();
        await MustReject(() => ChannexEnvironmentGuard.EnsureAsync(db, new ChannexOptions { Environment = "staging" }),
            "A production database cannot later be opened as staging");
        Check.Equal(1, await db.Properties.CountAsync(), "A rejected environment switch must preserve actual properties");
        await ChannexEnvironmentGuard.EnsureAsync(db, production);
    }

    public static async Task LegacyMappingsAndQueuesCannotBeReusedInProduction()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await using var db = factory.CreateDbContext();
        db.Outbox.Add(new OutboxChange { PropertyId = seed.PropertyId, EntityId = seed.RatePlanId, Kind = "restrictions", Date = seed.Start });
        db.BookingReceipts.Add(new BookingReceipt { Id = "55555555-5555-4555-8555-555555555555", PropertyId = seed.PropertyId });
        await db.SaveChangesAsync();
        await MustReject(() => ChannexEnvironmentGuard.EnsureAsync(db, new ChannexOptions { Environment = "production" }),
            "Legacy staging mappings and pending work must block accidental production startup");
        Check.Equal(SeededProperty.RemoteProperty, (await db.Properties.SingleAsync()).ChannexId,
            "The production guard must never rewrite a staging mapping");
        Check.Equal(1, await db.Outbox.CountAsync(x => x.ProcessedAt == null), "The guard must never consume or erase queued work");
        Check.Equal(1, await db.BookingReceipts.CountAsync(x => x.AckedAt == null), "The guard must never erase or acknowledge receipt evidence");
        await ChannexEnvironmentGuard.EnsureAsync(db, new ChannexOptions { Environment = "staging" });
        await ChannexEnvironmentGuard.EnsureAsync(db, new ChannexOptions { Environment = "staging" });
        await MustReject(() => ChannexEnvironmentGuard.EnsureAsync(db, new ChannexOptions { Environment = "production" }),
            "An explicitly bound staging database must also reject a later production switch");
        Check.Equal(1, await db.Outbox.CountAsync(), "Repeated environment checks must preserve every queued row");
    }

    public static async Task ProductionHttpTraceLogsStillRedactCredentialHeaders()
    {
        var logs = new TraceLogs();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddChannex(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Channex:Enabled"] = "true", ["Channex:Environment"] = "production", ["Channex:ApiKey"] = TraceHandler.SyntheticKey
        }).Build());
        services.AddHttpClient<ChannexClient>().ConfigurePrimaryHttpMessageHandler(() => new TraceHandler());
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ChannexClient>().SendAriAsync("availability", "{\"values\":[]}");
        var messages = string.Join("\n", logs.Messages);
        Check.That(messages.Contains("user-api-key", StringComparison.OrdinalIgnoreCase), "The production test must exercise verbose header logging");
        Check.That(!messages.Contains(TraceHandler.SyntheticKey, StringComparison.Ordinal), "Production trace logs must redact the API key");
    }

    private static async Task MustReject(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed record RequestTrace(string Host, string Method, string PathAndQuery, string Body, string? ContentType, string Accept, bool ApiKeyPresent);

    private sealed class TraceHandler : HttpMessageHandler
    {
        public const string SyntheticKey = "synthetic-production-environment-check-key";
        public List<RequestTrace> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.RequestUri!.Host, request.Method.Method, request.RequestUri.PathAndQuery,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Content?.Headers.ContentType?.ToString(),
                string.Join(",", request.Headers.Accept), request.Headers.TryGetValues("user-api-key", out var values) && values.Single() == SyntheticKey));
            return RecordingHandler.Json(HttpStatusCode.OK, request.Method == HttpMethod.Get ? "{\"data\":[],\"meta\":{\"total\":0}}" : "{}");
        }
    }

    private sealed class TraceLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new TraceLogger(Messages);
        public void Dispose() { }
        private sealed class TraceLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
