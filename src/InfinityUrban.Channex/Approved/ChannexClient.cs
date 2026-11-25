using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Stayline;

public record RemoteProperty(string Id, string Name, string Currency);
public record RemoteRoomType(string Id, string Name, string PropertyId);
public record RemoteRatePlan(string Id, string Name, string RoomTypeId);
public record RemoteInventory(IReadOnlyList<RemoteProperty> Properties,
    IReadOnlyList<RemoteRoomType> RoomTypes, IReadOnlyList<RemoteRatePlan> RatePlans);
public record ChannexResponse(int StatusCode, string Body, TimeSpan? RetryAfter)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>Server-side API client with allowlisted deployment environments. Never log headers or booking response bodies.</summary>
public sealed class ChannexClient(HttpClient http, IOptions<ChannexOptions> options)
{
    public bool IsEnabled => options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!IsEnabled) throw new InvalidOperationException("Channex is disabled. Set the API key and Enabled in server configuration.");
        // Only the environment selects the host. Paths cannot select hosts or escape the API root.
        var baseUri = options.Value.ApiBaseUri;
        var pathPart = path.Split('?', 2)[0];
        if (string.IsNullOrWhiteSpace(pathPart) || path.StartsWith('/') || pathPart.Contains('\\') ||
            pathPart.Split('/').Any(segment => segment is "." or "..") ||
            pathPart.Contains('%') || path.Contains('#') || Uri.TryCreate(path, UriKind.Absolute, out _))
            throw new ArgumentException("A relative Channex API path is required.", nameof(path));
        var requestUri = new Uri(baseUri, path);
        if (requestUri.Scheme != baseUri.Scheme || requestUri.Authority != baseUri.Authority ||
            !requestUri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new ArgumentException("The path must stay within the selected Channex API.", nameof(path));
        var resource = requestUri.AbsolutePath.TrimEnd('/');
        if (method == HttpMethod.Get &&
            (resource == "/api/v1/bookings" || resource.StartsWith("/api/v1/bookings/", StringComparison.Ordinal) ||
             resource == "/api/v1/booking_revisions" || resource.StartsWith("/api/v1/booking_revisions/", StringComparison.Ordinal)) &&
            resource != "/api/v1/booking_revisions/feed")
            throw new InvalidOperationException("Receive bookings through the booking revision feed. Booking list and by-ID reads are disabled in the integration.");
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("user-api-key", options.Value.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    public async Task<ChannexResponse> SendAriAsync(string endpoint, string requestJson, CancellationToken ct = default)
    {
        if (endpoint is not ("availability" or "restrictions")) throw new ArgumentException("Unknown ARI endpoint.");
        if (Encoding.UTF8.GetByteCount(requestJson) >= 10_000_000)
            throw new InvalidOperationException("The batch exceeds Channex's 10 MB limit. Reduce the requested range before retrying; the application will not silently split this operation.");
        using var request = Request(HttpMethod.Post, endpoint);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var retryAfter = response.Headers.RetryAfter;
        var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return new ChannexResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), wait);
    }

    /// <summary>Create actual property inventory in the configured environment. Metadata endpoints return entity IDs, not ARI task IDs.</summary>
    public async Task<ChannexResponse> CreateStagingEntityAsync(string endpoint, string requestJson, CancellationToken ct = default)
    {
        if (endpoint is not ("properties" or "room_types" or "rate_plans"))
            throw new ArgumentException("Unknown Channex metadata endpoint.", nameof(endpoint));
        using var request = Request(HttpMethod.Post, endpoint);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var retryAfter = response.Headers.RetryAfter;
        var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return new ChannexResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), wait);
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Channex returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.Clone();
    }

    /// <summary>Read metadata or ARI in the configured environment; credentials stay inside this server-side client.</summary>
    public Task<JsonElement> ReadStagingJsonAsync(string path, CancellationToken ct = default) => GetAsync(path, ct);

    /// <summary>Fetch all pages. Refuse malformed/repeating pagination rather than silently losing mappings/bookings.</summary>
    public async Task<IReadOnlyList<JsonElement>> FetchCollectionAsync(string path, CancellationToken ct = default)
    {
        var result = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 10_000; page++)
        {
            var separator = path.Contains('?') ? '&' : '?';
            var root = await GetAsync($"{path}{separator}pagination[page]={page}&pagination[limit]=100", ct);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Channex returned a malformed collection.");
            foreach (var item in data.EnumerateArray())
            {
                var id = JsonFields.Required(item, "id");
                if (!seen.Add(id)) throw new InvalidOperationException("Channex pagination repeated an item. Retry the collection instead of using an incomplete result.");
                result.Add(item.Clone());
            }
            if (data.GetArrayLength() == 0) return result;
            if (!root.TryGetProperty("meta", out var meta) ||
                !meta.TryGetProperty("total", out var totalElement) || !totalElement.TryGetInt32(out var total))
            {
                // The documented /options endpoints are unpaginated; regular collections must include meta.
                if (path.Split('?')[0].EndsWith("/options", StringComparison.Ordinal)) return result;
                throw new InvalidOperationException("Channex collection has no pagination metadata.");
            }
            if (result.Count >= total) return result;
        }
        throw new InvalidOperationException("Channex pagination exceeded its safety limit.");
    }

    public async Task<RemoteInventory> FetchInventoryAsync(string? externalPropertyId, CancellationToken ct = default)
    {
        var properties = (await FetchCollectionAsync("properties", ct)).Select(x =>
        {
            var a = x.GetProperty("attributes");
            return new RemoteProperty(JsonFields.Required(x, "id"), JsonFields.Required(a, "title"), JsonFields.Required(a, "currency"));
        }).ToArray();
        if (string.IsNullOrWhiteSpace(externalPropertyId)) return new(properties, [], []);
        if (!Guid.TryParse(externalPropertyId, out var id)) throw new ArgumentException("The Channex property ID must be a UUID.");
        var filter = $"?filter[property_id]={id:D}";
        var roomItems = await FetchCollectionAsync("room_types" + filter, ct);
        var rateItems = await FetchCollectionAsync("rate_plans" + filter, ct);
        var rooms = roomItems.Select(x => new RemoteRoomType(JsonFields.Required(x, "id"),
            JsonFields.Required(x.GetProperty("attributes"), "title"), RelationshipId(x, "property", "property_id"))).ToArray();
        var rates = rateItems.Select(x => new RemoteRatePlan(JsonFields.Required(x, "id"),
            JsonFields.Required(x.GetProperty("attributes"), "title"), RelationshipId(x, "room_type", "room_type_id"))).ToArray();
        if (rooms.Any(x => !string.Equals(x.PropertyId, id.ToString("D"), StringComparison.OrdinalIgnoreCase)) ||
            rateItems.Any(x => !string.Equals(RelationshipId(x, "property", "property_id"), id.ToString("D"), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Channex returned inventory outside the selected property.");
        return new(properties, rooms, rates);
    }

    private static string RelationshipId(JsonElement item, string relation, string field)
    {
        if (item.GetProperty("attributes").TryGetProperty(field, out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString()!;
        return JsonFields.Required(item.GetProperty("relationships").GetProperty(relation).GetProperty("data"), "id");
    }

    /// <summary>Receive complete authoritative revisions from the unacknowledged feed only.</summary>
    public Task<IReadOnlyList<JsonElement>> FetchBookingFeedAsync(CancellationToken ct = default) =>
        FetchCollectionAsync("booking_revisions/feed?order[inserted_at]=asc", ct);

    public async Task AckRevisionAsync(string revisionId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(revisionId, out var id)) throw new ArgumentException("Invalid booking revision UUID.");
        using var request = Request(HttpMethod.Post, $"booking_revisions/{id:D}/ack");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Channex booking acknowledgment returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }
}

internal static class JsonFields
{
    public static string? Optional(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static string Required(JsonElement root, string name) =>
        Optional(root, name) is { Length: > 0 } text ? text : throw new InvalidOperationException($"Channex data is missing {name}.");
}
