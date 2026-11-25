using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stayline;
namespace InfinityUrban.Web;

public sealed record OperationalLinkResult(int AddedRooms,int AddedRates,string[] Issues);
// Adapter around the certified engine. Only reads Channex; it never queues ARI or changes remote mappings.
public sealed class OperationalLinks(IDbContextFactory<AppDb> factory,MutationLock mutation,ChannexClient client,BookingInboxWorker inbox)
{
    private static string? T(JsonElement row,string key)=>RemoteAccommodationService.Text(row,key);
    private static JsonElement A(JsonElement row)=>RemoteAccommodationService.Attributes(row);
    private static string? Parent(JsonElement row,string name)=>RemoteAccommodationService.Parent(row,name);
    private static string Id(JsonElement row)=>Guid.Parse(T(row,"id")!).ToString();
    private static int Number(JsonElement row,string key)=>row.TryGetProperty(key,out var value)&&value.TryGetInt32(out var n)?n:0;
    public async Task<OperationalLinkResult> Reconcile(Guid? propertyId,CancellationToken ct)
    {
        var propertiesTask=client.FetchCollectionAsync(propertyId is {} id?$"properties?filter[id]={id:D}":"properties",ct);
        var roomsTask=client.FetchCollectionAsync(propertyId is {} rp?$"room_types?filter[property_id]={rp:D}":"room_types",ct);
        var ratesTask=client.FetchCollectionAsync(propertyId is {} tp?$"rate_plans?filter[property_id]={tp:D}":"rate_plans",ct);
        await Task.WhenAll(propertiesTask,roomsTask,ratesTask);var properties=await propertiesTask;var rooms=await roomsTask;var rates=await ratesTask;
        var addedRooms=0;var addedRates=0;var issues=new List<string>();
        foreach(var property in properties.Where(p=>propertyId is null||Id(p)==propertyId.Value.ToString()))
        {
            var pid=Id(property);var children=rooms.Where(r=>Parent(r,"property")==pid).ToArray();if(children.Length==0)continue;
            var plans=rates.Where(r=>Parent(r,"property")==pid&&children.Any(room=>Id(room)==Parent(r,"room_type"))&&T(A(r),"sell_mode")=="per_room"&&T(A(r),"rate_mode") is "manual" or "auto").ToArray();
            try{var result=await Ensure(property,children,plans,ct);addedRooms+=result.Rooms;addedRates+=result.Rates;}
            catch(Exception e)when(e is HttpRequestException or PortalError or InvalidOperationException or FormatException or JsonException or KeyNotFoundException or TimeZoneNotFoundException or InvalidTimeZoneException or DbUpdateException){issues.Add($"{T(A(property),"title")??pid}: vínculo automático pendente; revise os dados e o calendário na Channex.");}
        }
        return new(addedRooms,addedRates,issues.ToArray());
    }
    private async Task<(int Rooms,int Rates)> Ensure(JsonElement remoteProperty,JsonElement[] remoteRooms,JsonElement[] remoteRates,CancellationToken ct)
    {
        var pid=Id(remoteProperty);var attributes=A(remoteProperty);
        if(T(attributes,"timezone") is null){remoteProperty=(await client.ReadStagingJsonAsync("properties/"+pid,ct)).GetProperty("data");attributes=A(remoteProperty);}
        var currency=T(attributes,"currency")!;var timezone=T(attributes,"timezone")!;
        if(!PmsService.Currencies.Contains(currency)||string.IsNullOrWhiteSpace(timezone))throw new PortalError(409,"Moeda ou fuso indisponível.");
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById(timezone)).DateTime);
        await using var inspect=await factory.CreateDbContextAsync(ct);
        var existingProperties=await inspect.Properties.AsNoTracking().Where(p=>p.ChannexId==pid).ToListAsync(ct);
        if(existingProperties.Count>1)throw new PortalError(409,"Vínculo duplicado.");
        var allRooms=await inspect.RoomTypes.AsNoTracking().Where(r=>r.ChannexId!=null).ToListAsync(ct);var allRates=await inspect.RatePlans.AsNoTracking().Where(r=>r.ChannexId!=null).ToListAsync(ct);
        var missingRooms=remoteRooms.Where(r=>!allRooms.Any(l=>string.Equals(l.ChannexId,Id(r),StringComparison.OrdinalIgnoreCase))).ToArray();
        var missingRates=remoteRates.Where(r=>!allRates.Any(l=>string.Equals(l.ChannexId,Id(r),StringComparison.OrdinalIgnoreCase))).ToArray();
        var needsCalendar=missingRooms.Length>0||missingRates.Length>0;
        JsonElement availability=default,restrictions=default;
        if(needsCalendar)
        {
            // Persist pending revisions first, without acknowledging them, so reserved stock isn't counted twice.
            await inbox.PollFeedAsync(ct);
            var filter=$"?filter[property_id]={pid}&filter[date][gte]={today:yyyy-MM-dd}&filter[date][lte]={today.AddDays(499):yyyy-MM-dd}";
            var availabilityTask=client.ReadStagingJsonAsync("availability"+filter,ct);
            var restrictionsTask=client.ReadStagingJsonAsync("restrictions"+filter+"&filter[restrictions]=rate,min_stay_arrival,min_stay_through,max_stay,stop_sell,closed_to_arrival,closed_to_departure",ct);
            await Task.WhenAll(availabilityTask,restrictionsTask);availability=(await availabilityTask).GetProperty("data");restrictions=(await restrictionsTask).GetProperty("data");
        }
        await mutation.Gate.WaitAsync(ct);
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);await using var transaction=await db.Database.BeginTransactionAsync(ct);
            var properties=await db.Properties.Where(p=>p.ChannexId==pid).ToListAsync(ct);if(properties.Count>1)throw new PortalError(409,"Vínculo duplicado.");
            var property=properties.SingleOrDefault();
            if(property is null){property=new Property{Name=T(attributes,"title")!,Currency=currency,TimeZoneId=timezone,Kind=T(attributes,"property_type")??"hotel",ChannexId=pid};db.Properties.Add(property);}
            else if(property.Currency!=currency||property.TimeZoneId!=timezone)throw new PortalError(409,"Moeda ou fuso divergente.");
            property.Name=T(attributes,"title")??property.Name;
            var localRooms=await db.RoomTypes.ToListAsync(ct);var localRates=await db.RatePlans.ToListAsync(ct);
            var active=await db.Reservations.AsNoTracking().Include(r=>r.Stays).Where(r=>r.PropertyId==property.Id&&r.Status!="cancelled").ToListAsync(ct);
            var receipts=needsCalendar?await db.BookingReceipts.AsNoTracking().Where(r=>r.ProcessedAt==null&&r.PayloadJson!=null).ToListAsync(ct):[];
            var pending=receipts.Select(r=>JsonDocument.Parse(r.PayloadJson!)).ToArray();
            try
            {
                var latest=pending.Select(doc=>doc.RootElement).Where(p=>T(p,"property_id")==pid).GroupBy(p=>T(p,"booking_id")).Select(g=>g.OrderByDescending(p=>T(p,"inserted_at"),StringComparer.Ordinal).First()).ToArray();
                foreach(var remoteRoom in remoteRooms)
                {
                    var rid=Id(remoteRoom);var roomPlans=remoteRates.Where(r=>Parent(r,"room_type")==rid).ToArray();if(roomPlans.Length==0)throw new PortalError(409,"Apartamento sem tarifa operacional compatível.");
                    var ra=A(remoteRoom);var quantity=Number(ra,"count_of_rooms");var occupancy=Number(ra,"occ_adults");
                    if(quantity is <1 or >1000||occupancy is <1 or >30||T(ra,"room_kind")=="dorm")throw new PortalError(409,"Tipo ou capacidade incompatível.");
                    var candidates=localRooms.Where(r=>string.Equals(r.ChannexId,rid,StringComparison.OrdinalIgnoreCase)).ToArray();if(candidates.Length>1)throw new PortalError(409,"Apartamento duplicado.");
                    var room=candidates.SingleOrDefault();var isNew=room is null;
                    if(room is null){
                        // A card deletion can finish after the initial collection read. Never adopt a deleted/stale Room.
                        var freshRoom=(await client.ReadStagingJsonAsync("room_types/"+rid,ct)).GetProperty("data");
                        if(Id(freshRoom)!=rid||Parent(freshRoom,"property")!=pid||Number(A(freshRoom),"count_of_rooms")!=quantity||Number(A(freshRoom),"occ_adults")!=occupancy)throw new PortalError(409,"O apartamento mudou durante a preparação. Atualize novamente.");
                        room=new RoomType{PropertyId=property.Id,Name=T(ra,"title")!,Quantity=quantity,Occupancy=occupancy,ChannexId=rid};db.RoomTypes.Add(room);
                    }
                    else if(room.PropertyId!=property.Id||room.Quantity!=quantity)throw new PortalError(409,"Vínculo ou capacidade divergente.");
                    room.Name=T(ra,"title")??room.Name;room.Occupancy=occupancy;
                    if(isNew)
                    {
                        if(!availability.TryGetProperty(rid,out var stock))throw new PortalError(409,"Disponibilidade não confirmada.");
                        for(var date=today;date<today.AddDays(500);date=date.AddDays(1))
                        {
                            if(!stock.TryGetProperty(date.ToString("yyyy-MM-dd"),out var v)||!v.TryGetInt32(out var available)||available<0||available>quantity)throw new PortalError(409,"Disponibilidade incompleta.");
                            var pendingBooked=latest.Where(p=>T(p,"status")!="cancelled").Sum(p=>p.GetProperty("rooms").EnumerateArray().Count(s=>T(s,"room_type_id")==rid&&DateOnly.Parse(T(s,"checkin_date")??T(p,"arrival_date")!)<=date&&DateOnly.Parse(T(s,"checkout_date")??T(p,"departure_date")!)>date));
                            var pendingIds=latest.Select(p=>T(p,"booking_id")).ToHashSet();var localBooked=PmsService.Booked(active.Where(r=>!pendingIds.Contains(r.ExternalBookingId)),room.Id,date);
                            var capacity=available+pendingBooked+localBooked;if(capacity>quantity)throw new PortalError(409,"Inventário mudou durante a leitura; tentar novamente.");
                            db.DailyInventories.Add(new DailyInventory{RoomTypeId=room.Id,Date=date,Capacity=capacity});
                        }
                    }
                    foreach(var plan in roomPlans)
                    {
                        var rateId=Id(plan);var matches=localRates.Where(r=>string.Equals(r.ChannexId,rateId,StringComparison.OrdinalIgnoreCase)).ToArray();if(matches.Length>1)throw new PortalError(409,"Tarifa duplicada.");
                        var rate=matches.SingleOrDefault();if(rate is not null){if(rate.RoomTypeId!=room.Id)throw new PortalError(409,"Tarifa em outro apartamento.");rate.Name=T(A(plan),"title")??rate.Name;continue;}
                        if(!restrictions.TryGetProperty(rateId,out var daily))throw new PortalError(409,"Tarifas não confirmadas.");
                        var basePrice=RemoteAccommodationService.Price(plan);if(basePrice is null or <=0)throw new PortalError(409,"Tarifa-base não confirmada.");
                        rate=new RatePlan{RoomTypeId=room.Id,Name=T(A(plan),"title")!,BasePrice=basePrice.Value,ChannexId=rateId};db.RatePlans.Add(rate);
                        for(var date=today;date<today.AddDays(500);date=date.AddDays(1))
                        {
                            if(!daily.TryGetProperty(date.ToString("yyyy-MM-dd"),out var day)||!day.TryGetProperty("rate",out var price)||!decimal.TryParse(price.ToString(),NumberStyles.Number,CultureInfo.InvariantCulture,out var amount)||amount<=0||amount>1000000||decimal.Round(amount,2)!=amount)throw new PortalError(409,"Calendário tarifário incompleto.");
                            foreach(var field in new[]{"min_stay_arrival","min_stay_through","max_stay","stop_sell","closed_to_arrival","closed_to_departure"})if(!day.TryGetProperty(field,out _))throw new PortalError(409,"Restrições incompletas.");
                            bool Flag(string key)=>day.TryGetProperty(key,out var flag)&&flag.ValueKind==JsonValueKind.True;
                            db.DailyRates.Add(new DailyRate{RatePlanId=rate.Id,Date=date,Rate=amount,MinStayArrival=Math.Max(1,Number(day,"min_stay_arrival")),MinStayThrough=Math.Max(1,Number(day,"min_stay_through")),MaxStay=Number(day,"max_stay"),StopSell=Flag("stop_sell"),ClosedToArrival=Flag("closed_to_arrival"),ClosedToDeparture=Flag("closed_to_departure")});
                        }
                    }
                }
                var addedRooms=db.ChangeTracker.Entries<RoomType>().Count(e=>e.State==EntityState.Added);var addedRates=db.ChangeTracker.Entries<RatePlan>().Count(e=>e.State==EntityState.Added);
                await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);return(addedRooms,addedRates);
            }
            finally{foreach(var document in pending)document.Dispose();}
        }
        finally{mutation.Gate.Release();}
    }
}
public sealed class OperationalLinkWorker(IServiceScopeFactory scopes,ILogger<OperationalLinkWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{using var scope=scopes.CreateScope();var result=await scope.ServiceProvider.GetRequiredService<OperationalLinks>().Reconcile(null,stoppingToken);if(result.Issues.Length>0)logger.LogWarning("Vínculo operacional automático: {Count} prédio(s) aguardando conferência.",result.Issues.Length);}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){logger.LogWarning("Vínculo operacional automático adiado ({Type}).",e.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromMinutes(5),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}
