using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stayline;

namespace Stayline.Checks;

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'");
    }

    public static async Task RejectsAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (AppError) { return; }
        throw new InvalidOperationException(message);
    }
}

internal static class PayloadDates
{
    // Assertions compare the actual inclusive date coverage, whether the wire format is a
    // single date or a merged range. Production code is not reused to expand the payload.
    public static JsonElement[] Expand(JsonElement root)
    {
        var result = new List<JsonElement>();
        foreach (var row in root.GetProperty("values").EnumerateArray())
        {
            var isSingle = row.TryGetProperty("date", out var single);
            Check.That(!isSingle || !row.TryGetProperty("date_from", out _), "A wire row cannot mix single-date and range syntax");
            var from = DateOnly.ParseExact(isSingle ? single.GetString()! : row.GetProperty("date_from").GetString()!, "yyyy-MM-dd");
            var to = isSingle ? from : DateOnly.ParseExact(row.GetProperty("date_to").GetString()!, "yyyy-MM-dd");
            Check.That(to >= from && to.DayNumber - from.DayNumber < 1000, "Wire date ranges must be valid and bounded");
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.GetRawText())!;
                fields.Remove("date_from");
                fields.Remove("date_to");
                fields["date"] = JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd"));
                result.Add(JsonSerializer.SerializeToElement(fields));
            }
        }
        return result.ToArray();
    }
}

internal sealed class TestDb : IDbContextFactory<AppDb>, IAsyncDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "stayline-checks-" + Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<AppDb> options;
    public TestDb()
    {
        Directory.CreateDirectory(folder);
        options = new DbContextOptionsBuilder<AppDb>()
            .UseSqlite($"Data Source={Path.Combine(folder, "test.db")};Pooling=False")
            .Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }
    public AppDb CreateDbContext() => new(options);
    public Task<AppDb> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    public ValueTask DisposeAsync()
    {
        Directory.Delete(folder, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed record SentRequest(string Method, string Path, string Body, DateTimeOffset SentAt);

// This fake is used only by the checks. Application code always uses the real HttpClient.
internal sealed class RecordingHandler : HttpMessageHandler
{
    public const string TaskId = "44444444-4444-4444-8444-444444444444";
    public List<SentRequest> Requests { get; } = [];
    public Func<SentRequest, Task<HttpResponseMessage>> Respond { get; set; } = _ =>
        Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"" + TaskId + "\",\"type\":\"task\"}]}"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = new SentRequest(request.Method.Method, request.RequestUri!.AbsolutePath,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), DateTimeOffset.UtcNow);
        Requests.Add(sent);
        return await Respond(sent);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal sealed record SeededProperty(Guid PropertyId, Guid RoomTypeId, Guid RatePlanId, DateOnly Start)
{
    public const string RemoteProperty = "11111111-1111-4111-8111-111111111111";
    public const string RemoteRoom = "22222222-2222-4222-8222-222222222222";
    public const string RemoteRate = "33333333-3333-4333-8333-333333333333";

    public static async Task<SeededProperty> CreateAsync(TestDb factory)
    {
        await using var db = factory.CreateDbContext();
        var property = new Property { Name = "Checks Hotel", ChannexId = RemoteProperty };
        var room = new RoomType { PropertyId = property.Id, Name = "Garden Double", Quantity = 5, ChannexId = RemoteRoom };
        var plan = new RatePlan { RoomTypeId = room.Id, Name = "Flexible", BasePrice = 119.95m, ChannexId = RemoteRate };
        db.AddRange(property, room, plan);
        await db.SaveChangesAsync();
        return new(property.Id, room.Id, plan.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30));
    }
}
