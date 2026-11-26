using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Stayline;

namespace InfinityUrban.Web;

public sealed record RevenueRoom(Guid RoomId, DateOnly Arrival, DateOnly Departure, long? AmountCents, Dictionary<string,long>? Days);
public sealed record RevenueSnapshot(Guid BookingId, Guid PropertyId, string Currency, DateTimeOffset RevisionAt, RevenueRoom[] Rooms);

// Separate, durable financial projection. No guest, contact, card or raw booking payload is retained.
public sealed class AnalyticsLedger(CatalogFiles files)
{
    private readonly SemaphoreSlim gate = new(1,1);
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        Directory.CreateDirectory(files.DirectoryPath);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(files.DirectoryPath,"analytics.db"), DefaultTimeout=30 }.ToString());
        await db.OpenAsync(ct);
        using var command=db.CreateCommand();
        command.CommandText="CREATE TABLE IF NOT EXISTS RevenueSnapshots (BookingId TEXT PRIMARY KEY, RevisionAt TEXT NOT NULL, Snapshot TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(ct);
        return db;
    }
    public async Task Save(RevenueSnapshot snapshot,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);using var command=db.CreateCommand();
            command.CommandText="INSERT INTO RevenueSnapshots VALUES ($id,$at,$json) ON CONFLICT(BookingId) DO UPDATE SET RevisionAt=excluded.RevisionAt,Snapshot=excluded.Snapshot WHERE excluded.RevisionAt >= RevenueSnapshots.RevisionAt;";
            command.Parameters.AddWithValue("$id",snapshot.BookingId.ToString("D"));command.Parameters.AddWithValue("$at",snapshot.RevisionAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(snapshot,JsonSerializerOptions.Web));await command.ExecuteNonQueryAsync(ct);
        }
        finally { gate.Release(); }
    }
    public async Task<RevenueSnapshot?> Read(Guid booking,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);using var command=db.CreateCommand();command.CommandText="SELECT Snapshot FROM RevenueSnapshots WHERE BookingId=$id";command.Parameters.AddWithValue("$id",booking.ToString("D"));
            return await command.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<RevenueSnapshot>(text,JsonSerializerOptions.Web):null;
        }
        finally { gate.Release(); }
    }
    public async Task<Dictionary<Guid,RevenueSnapshot>> ReadMany(IEnumerable<Guid> bookings,CancellationToken ct)
    {
        var result=new Dictionary<Guid,RevenueSnapshot>();var ids=bookings.Distinct().ToArray();if(ids.Length==0)return result;
        await gate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);
            foreach(var batch in ids.Chunk(400))
            {
                using var command=db.CreateCommand();
                var parameters=batch.Select((id,index)=>{var name="$b"+index;command.Parameters.AddWithValue(name,id.ToString("D"));return name;});
                command.CommandText="SELECT Snapshot FROM RevenueSnapshots WHERE BookingId IN ("+string.Join(',',parameters)+")";
                await using var reader=await command.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))if(JsonSerializer.Deserialize<RevenueSnapshot>(reader.GetString(0),JsonSerializerOptions.Web) is {} snapshot)result[snapshot.BookingId]=snapshot;
            }
            return result;
        }
        finally { gate.Release(); }
    }
    public static long? Cents(JsonElement value)
    {
        var text=value.ValueKind==JsonValueKind.String?value.GetString():value.ValueKind==JsonValueKind.Number?value.GetRawText():null;
        if(!decimal.TryParse(text,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var amount)||amount<0||amount>999999999.99m)return null;
        return (long)decimal.Round(amount*100,0,MidpointRounding.AwayFromZero);
    }
    public static RevenueSnapshot? Project(JsonElement item)
    {
        if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("attributes",out var a))return null;
        string? Text(string key)=>RemoteAccommodationService.Text(a,key);
        if(!Guid.TryParse(Text("booking_id"),out var booking)||!Guid.TryParse(Text("property_id"),out var property)||!DateTimeOffset.TryParse(Text("inserted_at"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var at)||Text("currency") is not {Length:3} currency||!PmsService.Currencies.Contains(currency)||!a.TryGetProperty("rooms",out var list)||list.ValueKind!=JsonValueKind.Array)return null;
        var rooms=new List<RevenueRoom>();
        foreach(var room in list.EnumerateArray())
        {
            if(!Guid.TryParse(RemoteAccommodationService.Text(room,"room_type_id"),out var id)||!DateOnly.TryParseExact(RemoteAccommodationService.Text(room,"checkin_date")??Text("arrival_date"),"yyyy-MM-dd",out var from)||!DateOnly.TryParseExact(RemoteAccommodationService.Text(room,"checkout_date")??Text("departure_date"),"yyyy-MM-dd",out var to)||to<=from||to.DayNumber-from.DayNumber>730)continue;
            var amount=room.TryGetProperty("amount",out var money)?Cents(money):null;
            // Only a single-room booking can use the booking-level amount without leaking siblings' revenue.
            if(amount is null&&list.GetArrayLength()==1&&a.TryGetProperty("amount",out var total))amount=Cents(total);
            var daily=new Dictionary<string,long>();
            if(room.TryGetProperty("days",out var days)&&days.ValueKind==JsonValueKind.Object)
                foreach(var day in days.EnumerateObject())if(DateOnly.TryParseExact(day.Name,"yyyy-MM-dd",out var date)&&date>=from&&date<to&&Cents(day.Value) is {} cents)daily[day.Name]=cents;
            rooms.Add(new(id,from,to,amount,daily));
        }
        return new(booking,property,currency,at,rooms.ToArray());
    }
}

// Observe only the already-requested approved feed, before its durable receive/ack path.
// This adds no requests and never changes the response, the feed snapshot or its acknowledgment.
public sealed class AnalyticsFeedObserver(AnalyticsLedger ledger):DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var response=await base.SendAsync(request,ct);
        if(request.Method!=HttpMethod.Get||request.RequestUri?.AbsolutePath!="/api/v1/booking_revisions/feed"||!response.IsSuccessStatusCode)return response;
        try
        {
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if(json.RootElement.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Array)
                foreach(var item in data.EnumerateArray())if(AnalyticsLedger.Project(item) is {} snapshot)await ledger.Save(snapshot,ct);
            return response;
        }
        catch { response.Dispose();throw; }
    }
}
