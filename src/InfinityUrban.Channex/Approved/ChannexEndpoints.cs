using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Stayline;

public static class ChannexEndpoints
{
    public static IServiceCollection AddChannex(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChannexOptions>(configuration.GetSection("Channex"));
        services.AddHttpClient<ChannexClient>(http => http.Timeout = TimeSpan.FromSeconds(45))
            .RedactLoggedHeaders(["user-api-key"])
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHostedService<ChannexSyncWorker>();
        services.AddSingleton<BookingInboxWorker>();
        services.AddHostedService<BookingInboxWorker>(provider => provider.GetRequiredService<BookingInboxWorker>());
        return services;
    }

    public static IEndpointRouteBuilder MapChannexEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/channex/remote", async (string? propertyId, ChannexClient client, CancellationToken ct) =>
        {
            if (!client.IsEnabled) return Results.Json(new { error = "Channex staging is disabled. Configure the server API key and Enabled setting first." }, statusCode: 409);
            try { return Results.Ok(await client.FetchInventoryAsync(propertyId, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
            catch (HttpRequestException ex) { return Results.Json(new { error = $"Channex staging could not load inventory (HTTP {(int?)ex.StatusCode}). Check the server API key and account access." }, statusCode: 502); }
        });
        app.MapPost("/api/channex/webhook", ReceiveWebhookAsync);
        return app;
    }

    public static async Task<IResult> ReceiveWebhookAsync(HttpContext context, IOptions<ChannexOptions> options,
        IDbContextFactory<AppDb> factory, MutationLock mutations, ILoggerFactory loggers, BookingInboxWorker worker, CancellationToken ct)
    {
        var config = options.Value;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.WebhookSecret) || config.WebhookSecret.Length < 16)
            return Results.Json(new { error = "The staging webhook is not configured." }, statusCode: 503);
        var supplied = context.Request.Headers["X-Stayline-Webhook-Secret"];
        if (supplied.Count != 1 || supplied[0] is not { Length: > 0 and <= 512 } secret ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(config.WebhookSecret))))
            return Results.Unauthorized();
        string revisionId;
        try
        {
            if (context.Request.ContentLength > 65_536) return Results.StatusCode(413);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, ct)) != 0)
            {
                if (body.Length + count > 65_536) return Results.StatusCode(413);
                await body.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            body.Position = 0;
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var root = json.RootElement;
            var eventName = JsonFields.Optional(root, "event");
            if (eventName is not ("booking" or "booking_new" or "booking_modification" or "booking_cancellation" or "booking_unmapped_room" or "booking_unmapped_rate" or "non_acked_booking"))
                return Results.BadRequest(new { error = "A booking webhook event is required." });
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Configure the Channex webhook with send_data=true." });
            // The ordinary booking event documents revision_id; unmapped/non-acked events use booking_revision_id.
            var ordinary = JsonFields.Optional(payload, "revision_id");
            var alternate = JsonFields.Optional(payload, "booking_revision_id");
            if (!Guid.TryParse(ordinary ?? alternate, out var id) ||
                ordinary is not null && alternate is not null && (!Guid.TryParse(alternate, out var second) || second != id))
                return Results.BadRequest(new { error = "A valid, unambiguous booking revision UUID is required." });
            revisionId = id.ToString("D");
        }
        catch (JsonException) { return Results.BadRequest(new { error = "Invalid webhook JSON." }); }

        await mutations.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.BookingReceipts.AnyAsync(x => x.Id == revisionId, ct))
            {
                db.BookingReceipts.Add(new BookingReceipt { Id = revisionId, Source = "webhook" });
                await db.SaveChangesAsync(ct);
            }
            // Return success only after the notification ID is durable. It wakes the normal feed
            // consumer; neither an identifier-only fetch nor the posted guest/card data is used.
            worker.RequestFeedRefresh();
            return Results.Ok(new { received = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("ChannexWebhook").LogError("Could not persist booking webhook ({Type}); returning 503 so Channex retries.", ex.GetType().Name);
            return Results.Json(new { error = "The booking notification could not be stored. Retry delivery." }, statusCode: 503);
        }
        finally { mutations.Gate.Release(); }
    }
}
