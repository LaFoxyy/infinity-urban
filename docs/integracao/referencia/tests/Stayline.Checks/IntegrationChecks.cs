using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stayline;

namespace Stayline.Checks;

internal static class IntegrationChecks
{
    private static readonly IOptions<ChannexOptions> Options = Microsoft.Extensions.Options.Options.Create(new ChannexOptions
    {
        Enabled = true,
        ApiKey = "test-only-not-a-real-key",
        WebhookSecret = "test-only-not-a-real-webhook-secret",
        BatchSeconds = 1
    });

    private static ChannexSyncWorker Worker(TestDb db, RecordingHandler handler) => new(db, new MutationLock(),
        new ChannexClient(new HttpClient(handler), Options), Options, NullLogger<ChannexSyncWorker>.Instance);

    public static async Task FullHorizonBatchesCurrentSavedState()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            for (var i = 0; i < 500; i++)
            {
                var date = seed.Start.AddDays(i);
                db.DailyInventories.Add(new() { RoomTypeId = seed.RoomTypeId, Date = date, Capacity = 3 + i % 3 });
                db.DailyRates.Add(new() { RatePlanId = seed.RatePlanId, Date = date, Rate = 119.95m + i % 7,
                    MinStayArrival = i % 4 + 1, MinStayThrough = 1, MaxStay = 14 });
                db.Outbox.Add(Change(seed.PropertyId, "availability", seed.RoomTypeId, date));
                db.Outbox.Add(Change(seed.PropertyId, "restrictions", seed.RatePlanId, date));
            }
            // Multiple saves before a worker pass must coalesce into the final saved value.
            db.Outbox.Add(Change(seed.PropertyId, "restrictions", seed.RatePlanId, seed.Start));
            await db.SaveChangesAsync();
            var first = await db.DailyRates.SingleAsync(x => x.RatePlanId == seed.RatePlanId && x.Date == seed.Start);
            first.Rate = 207.35m;
            await db.SaveChangesAsync();
        }
        var handler = new RecordingHandler();
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            await Worker(factory, handler).ProcessPendingAsync(CancellationToken.None);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        Check.Equal(2, handler.Requests.Count, "A 500-day full sync must produce two requests per property");
        var availability = handler.Requests.Single(x => x.Path.EndsWith("/availability"));
        var rates = handler.Requests.Single(x => x.Path.EndsWith("/restrictions"));
        using var inventoryJson = JsonDocument.Parse(availability.Body);
        using var rateJson = JsonDocument.Parse(rates.Body);
        var inventoryValues = PayloadDates.Expand(inventoryJson.RootElement);
        var rateValues = PayloadDates.Expand(rateJson.RootElement);
        Check.Equal(500, inventoryValues.Length, "The availability request must include every date");
        Check.Equal(500, rateValues.Length, "Duplicate changes must not duplicate rate payload rows");
        var current = rateValues.Single(x => x.GetProperty("date").GetString() == seed.Start.ToString("yyyy-MM-dd"));
        Check.Equal(JsonValueKind.String, current.GetProperty("rate").ValueKind, "Rates must be encoded as decimal strings");
        Check.Equal("207.35", current.GetProperty("rate").GetString(), "Payload must use the latest DB price with invariant decimals");
        Check.That(rateValues.Select(x => x.GetProperty("rate").GetString()).Distinct().Count() > 1,
            "Saved varied prices must remain varied in the payload");
        await using var final = factory.CreateDbContext();
        Check.Equal(0, await final.Outbox.CountAsync(x => x.ProcessedAt == null), "Accepted work should be marked processed");
        Check.Equal(2, await final.SyncAttempts.CountAsync(), "Each request must have a persisted attempt record");
        Check.That((await final.SyncAttempts.ToListAsync()).All(x => x.TaskIds.Contains(RecordingHandler.TaskId)),
            "Returned task IDs must be saved for operator review");
    }

    public static async Task WarningResponseDoesNotLosePendingChanges()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await SeedRateChange(factory, seed);
        var handler = new RecordingHandler
        {
            Respond = _ => Task.FromResult(RecordingHandler.Json(HttpStatusCode.OK,
                "{\"data\":[{\"id\":\"" + RecordingHandler.TaskId + "\",\"type\":\"task\"}],\"meta\":{\"warnings\":[{\"message\":\"Partial validation failure\"}]}}"))
        };
        await Worker(factory, handler).ProcessPendingAsync(CancellationToken.None);
        await using var db = factory.CreateDbContext();
        Check.Equal(1, await db.Outbox.CountAsync(x => x.ProcessedAt == null),
            "HTTP 200 with warnings must remain pending for operator attention/retry");
        Check.That((await db.SyncAttempts.SingleAsync()).Status != "success", "Warnings must not be displayed as success");
    }

    public static async Task EchoedCredentialsNeverReachSyncHistory()
    {
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.OK })
        {
            await using var factory = new TestDb();
            var seed = await SeededProperty.CreateAsync(factory);
            await SeedRateChange(factory, seed);
            var body = JsonSerializer.Serialize(new
            {
                errors = new[] { new { title = "Invalid staging request", api_key = Options.Value.ApiKey,
                    webhook_secret = Options.Value.WebhookSecret } }
            });
            var handler = new RecordingHandler { Respond = _ => Task.FromResult(RecordingHandler.Json(status, body)) };
            await Worker(factory, handler).ProcessPendingAsync();
            await using var db = factory.CreateDbContext();
            var detail = (await db.SyncAttempts.SingleAsync()).Detail;
            Check.That(!detail.Contains(Options.Value.ApiKey, StringComparison.Ordinal), "An echoed API key must not be persisted");
            Check.That(!detail.Contains(Options.Value.WebhookSecret, StringComparison.Ordinal), "An echoed webhook secret must not be persisted");
            Check.That(detail.Contains("[redacted]", StringComparison.Ordinal), "History must show that sensitive response values were removed");
            Check.That(detail.Contains("Invalid staging request", StringComparison.Ordinal), "Useful error information should remain available");
        }
    }

    public static async Task HttpClientLoggingRedactsApiHeader()
    {
        var logs = new CapturedLogs();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddChannex(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Channex:Enabled"] = "true", ["Channex:ApiKey"] = Options.Value.ApiKey
        }).Build());
        services.AddHttpClient<ChannexClient>().ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ChannexClient>().SendAriAsync("restrictions", "{\"values\":[]}");
        var text = string.Join("\n", logs.Messages);
        Check.That(text.Contains("user-api-key", StringComparison.OrdinalIgnoreCase), "Trace logging must exercise the credential header");
        Check.That(!text.Contains(Options.Value.ApiKey, StringComparison.Ordinal), "HTTP trace logs must redact the API header value");
    }

    public static async Task RetryAfterBlocksImmediateResend()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await SeedRateChange(factory, seed);
        var handler = new RecordingHandler
        {
            Respond = _ =>
            {
                var response = RecordingHandler.Json(HttpStatusCode.TooManyRequests, "{\"errors\":[{\"title\":\"Rate limited\"}]}");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return Task.FromResult(response);
            }
        };
        var worker = Worker(factory, handler);
        await worker.ProcessPendingAsync(CancellationToken.None);
        await worker.ProcessPendingAsync(CancellationToken.None);
        Check.Equal(1, handler.Requests.Count, "Retry-After must prevent an immediate second request");
        await using var db = factory.CreateDbContext();
        Check.Equal(1, await db.Outbox.CountAsync(x => x.ProcessedAt == null), "429 must preserve pending work");
        var gate = await db.SyncGates.SingleAsync(x => x.Kind == "restrictions");
        Check.That(gate.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(90), "Retry gate must honor Retry-After");
        Check.That(gate.Failures > 0, "Transient failure count must be persisted");
        var sibling = await db.SyncGates.SingleAsync(x => x.Kind == "availability");
        Check.That(sibling.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(45),
            "A property-wide failure must pause the sibling ARI endpoint too");
    }

    public static async Task UnmappedRecordsStayPending()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await SeedRateChange(factory, seed);
        await using (var db = factory.CreateDbContext())
        {
            (await db.RatePlans.SingleAsync()).ChannexId = null;
            await db.SaveChangesAsync();
        }
        var handler = new RecordingHandler();
        await Worker(factory, handler).ProcessPendingAsync(CancellationToken.None);
        Check.Equal(0, handler.Requests.Count, "An incomplete mapping must not send an invalid request");
        await using var final = factory.CreateDbContext();
        Check.Equal(1, await final.Outbox.CountAsync(x => x.ProcessedAt == null), "Missing mappings must preserve pending work");
    }

    public static async Task DurableRateLimitSurvivesWorkerRecreation()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await SeedRateChange(factory, seed);
        var handler = new RecordingHandler();
        await Worker(factory, handler).ProcessPendingAsync();
        Check.Equal(1, handler.Requests.Count, "Initial change should be sent");
        await using (var db = factory.CreateDbContext())
        {
            db.Outbox.Add(Change(seed.PropertyId, "restrictions", seed.RatePlanId, seed.Start));
            // Remove only the next-attempt gate to check the separate persisted attempt rate limiter.
            db.SyncGates.RemoveRange(db.SyncGates);
            await db.SaveChangesAsync();
        }
        await Worker(factory, handler).ProcessPendingAsync();
        Check.Equal(1, handler.Requests.Count, "A new worker must honor the persisted recent-request rate limit");
        await using var final = factory.CreateDbContext();
        Check.Equal(1, await final.Outbox.CountAsync(x => x.ProcessedAt == null), "Rate-limited new work must remain pending");
    }

    public static async Task EditsDuringDeliveryRemainQueued()
    {
        await using var factory = new TestDb();
        var seed = await SeededProperty.CreateAsync(factory);
        await SeedRateChange(factory, seed);
        var handler = new RecordingHandler
        {
            Respond = async _ =>
            {
                // Model an operator saving another price while the earlier request is in flight.
                await using var db = factory.CreateDbContext();
                (await db.DailyRates.SingleAsync()).Rate = 211.65m;
                db.Outbox.Add(Change(seed.PropertyId, "restrictions", seed.RatePlanId, seed.Start));
                await db.SaveChangesAsync();
                return RecordingHandler.Json(HttpStatusCode.OK,
                    "{\"data\":[{\"id\":\"" + RecordingHandler.TaskId + "\",\"type\":\"task\"}]}");
            }
        };
        await Worker(factory, handler).ProcessPendingAsync();
        await using (var db = factory.CreateDbContext())
        {
            Check.Equal(1, await db.Outbox.CountAsync(x => x.ProcessedAt == null),
                "Finishing a request must not mark changes created while it was in flight as processed");
            Check.Equal(1, await db.Outbox.CountAsync(x => x.ProcessedAt != null), "The original accepted change must complete");
            foreach (var gate in await db.SyncGates.ToListAsync()) gate.NextAttemptAt = DateTimeOffset.MinValue;
            foreach (var attempt in await db.SyncAttempts.ToListAsync()) attempt.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
        }
        handler.Respond = _ => Task.FromResult(RecordingHandler.Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"" + RecordingHandler.TaskId + "\",\"type\":\"task\"}]}"));
        await Worker(factory, handler).ProcessPendingAsync();
        Check.Equal(2, handler.Requests.Count, "The edit saved during delivery must receive its own later request");
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Check.Equal("211.65", payload.RootElement.GetProperty("values")[0].GetProperty("rate").GetString(),
            "The later delivery must rebuild the payload from the final saved price");
        await using var final = factory.CreateDbContext();
        Check.Equal(0, await final.Outbox.CountAsync(x => x.ProcessedAt == null), "Both accepted changes should eventually complete");
    }

    private static async Task SeedRateChange(TestDb factory, SeededProperty seed)
    {
        await using var db = factory.CreateDbContext();
        db.DailyRates.Add(new() { RatePlanId = seed.RatePlanId, Date = seed.Start, Rate = 153.45m });
        db.Outbox.Add(Change(seed.PropertyId, "restrictions", seed.RatePlanId, seed.Start));
        await db.SaveChangesAsync();
    }

    internal static OutboxChange Change(Guid propertyId, string kind, Guid entityId, DateOnly date) => new()
    {
        PropertyId = propertyId, Kind = kind, EntityId = entityId, Date = date,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private sealed class CapturedLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }

        private sealed class CapturedLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
        }
    }
}
