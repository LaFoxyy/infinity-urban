using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stayline;
namespace InfinityUrban.Web;

public sealed record ManualReservationInput(Guid OperationId,Guid RoomId,Guid RateId,string GuestName,DateOnly Arrival,DateOnly Departure);
public sealed record ManualReservationDelivery(Guid Actor,string Hash,string State,Guid? ReservationId);
public sealed class ReservationsService(IDbContextFactory<AppDb> factory,ChannexClient channex,PmsService pms,BookingInboxWorker inbox,CatalogFiles files)
{
    private static string? T(JsonElement value,string name)=>RemoteAccommodationService.Text(value,name);
    private static string? Parent(JsonElement value,string name)=>RemoteAccommodationService.Parent(value,name);
    private static bool Matches(string? value,Guid id)=>Guid.TryParse(value,out var parsed)&&parsed==id;
    private async Task<Dictionary<string,Dictionary<string,int>>> Availability(Guid property,DateOnly from,int days,CancellationToken ct)
    {
        var root=await channex.ReadStagingJsonAsync($"availability?filter[property_id]={property:D}&filter[date][gte]={from:yyyy-MM-dd}&filter[date][lte]={from.AddDays(days-1):yyyy-MM-dd}",ct);
        if(!root.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Object)throw new PortalError(502,"A Channex não confirmou a disponibilidade.");
        var result=new Dictionary<string,Dictionary<string,int>>();
        foreach(var room in data.EnumerateObject())
        {
            if(room.Value.ValueKind!=JsonValueKind.Object)continue;
            result[room.Name]=room.Value.EnumerateObject().Where(d=>d.Value.ValueKind==JsonValueKind.Number&&d.Value.TryGetInt32(out var n)&&n>=0).ToDictionary(d=>d.Name,d=>d.Value.GetInt32());
        }
        return result;
    }
    public async Task<object> List(DateOnly from,int days,CancellationToken ct)
    {
        if(from.Year is <2000 or >2099||days is <1 or >31)throw new PortalError(400,"Escolha um período de até 31 dias, entre 2000 e 2099.");
        var propertiesTask=channex.FetchCollectionAsync("properties",ct);var roomsTask=channex.FetchCollectionAsync("room_types",ct);var ratesTask=channex.FetchCollectionAsync("rate_plans",ct);
        await using var db=await factory.CreateDbContextAsync(ct);
        var properties=await db.Properties.AsNoTracking().ToListAsync(ct);var rooms=await db.RoomTypes.AsNoTracking().ToListAsync(ct);var rates=await db.RatePlans.AsNoTracking().ToListAsync(ct);
        var bookings=await db.Reservations.AsNoTracking().Include(r=>r.Stays).OrderByDescending(r=>r.Arrival).Take(20001).ToListAsync(ct);
        if(bookings.Count>20000)throw new PortalError(503,"O volume de reservas excede esta consulta. Solicite a paginação ampliada antes de continuar.");
        var pending=await db.BookingReceipts.CountAsync(r=>r.AckedAt==null,ct);var failed=await db.BookingReceipts.CountAsync(r=>r.AckedAt==null&&r.Error!=null,ct);
        var pendingChanges=await db.Outbox.CountAsync(r=>r.ProcessedAt==null,ct);
        await Task.WhenAll(propertiesTask,roomsTask,ratesTask);var remoteProperties=await propertiesTask;var remoteRooms=await roomsTask;var remoteRates=await ratesTask;
        using var gate=new SemaphoreSlim(4);
        var snapshots=await Task.WhenAll(remoteProperties.Select(async p=>
        {
            await gate.WaitAsync(ct);try{return(id:T(p,"id")!,data:await Availability(Guid.Parse(T(p,"id")!),from,days,ct),error:(string?)null);}
            catch(Exception e)when(e is HttpRequestException or TaskCanceledException or PortalError){if(ct.IsCancellationRequested)throw;return(id:T(p,"id")!,data:new Dictionary<string,Dictionary<string,int>>(),error:(string?)"Disponibilidade indisponível; atualize para tentar novamente.");}
            finally{gate.Release();}
        }));
        var readyProperties=new HashSet<Guid>();foreach(var property in properties)if((await SyncReadiness.CheckAsync(db,property,channex.IsEnabled,ct)).Ready)readyProperties.Add(property.Id);
        var dates=Enumerable.Range(0,days).Select(from.AddDays).ToArray();
        var roomViews=remoteRooms.Select(r=>
        {
            var id=Guid.Parse(T(r,"id")!);var propertyId=Parent(r,"property");var local=rooms.Where(x=>Matches(x.ChannexId,id)).ToArray();
            var linked=local.Length==1&&properties.Any(p=>p.Id==local[0].PropertyId&&p.ChannexId==propertyId);
            var localRates=linked?rates.Where(rate=>rate.RoomTypeId==local[0].Id&&remoteRates.Any(rr=>Matches(T(rr,"id"),Guid.TryParse(rate.ChannexId,out var rid)?rid:Guid.Empty)&&Parent(rr,"room_type")==id.ToString()&&Parent(rr,"property")==propertyId)).ToArray():[];
            var snapshot=snapshots.FirstOrDefault(s=>s.id==propertyId);var daily=snapshot.data?.GetValueOrDefault(id.ToString());
            return new{id,title=T(RemoteAccommodationService.Attributes(r),"title"),propertyId,ready=linked&&localRates.Length>0&&readyProperties.Contains(local[0].PropertyId),rates=localRates.Select(rate=>new{id=rate.Id,title=rate.Name}),days=dates.Select(date=>new{date,available=daily is not null&&daily.TryGetValue(date.ToString("yyyy-MM-dd"),out var available)?(int?)available:null})};
        }).ToArray();
        // Resolve labels by immutable remote IDs. Operational mapping can retain names from a prior setup.
        var remoteRoomNames=remoteRooms.ToDictionary(r=>T(r,"id")!,r=>T(RemoteAccommodationService.Attributes(r),"title"),StringComparer.OrdinalIgnoreCase);
        var remotePropertyNames=remoteProperties.ToDictionary(p=>T(p,"id")!,p=>T(RemoteAccommodationService.Attributes(p),"title"),StringComparer.OrdinalIgnoreCase);
        string RoomName(RoomType? room)=>room is null?"Apartamento não identificado":room.ChannexId is null?room.Name:remoteRoomNames.GetValueOrDefault(room.ChannexId)??"Apartamento indisponível na Channex";
        string? PropertyName(Property? property)=>property?.ChannexId is null?property?.Name:remotePropertyNames.GetValueOrDefault(property.ChannexId)??"Prédio indisponível na Channex";
        var bookingViews=bookings.Select(reservation=>new{id=reservation.Id,guestName=reservation.GuestName,code=reservation.ExternalBookingId??reservation.Id.ToString("D"),status=reservation.Status,source=reservation.Source=="direct"?"Manual":"Channex",arrival=reservation.Arrival,departure=reservation.Departure,manual=reservation.Source=="direct",stays=reservation.Stays.Select(stay=>{var room=rooms.SingleOrDefault(r=>r.Id==stay.RoomTypeId);var property=properties.SingleOrDefault(p=>p.Id==reservation.PropertyId);return new{roomId=room?.ChannexId,roomName=RoomName(room),propertyId=property?.ChannexId,propertyName=PropertyName(property),arrival=stay.Arrival??reservation.Arrival,departure=stay.Departure??reservation.Departure,quantity=stay.Quantity};})});
        return new{properties=remoteProperties.Select(p=>new{id=T(p,"id"),title=T(RemoteAccommodationService.Attributes(p),"title")}),rooms=roomViews,reservations=bookingViews,from,days,dates,pendingBookings=pending,failedBookings=failed,pendingChanges,availabilityWarnings=snapshots.Where(s=>s.error is not null).Select(s=>new{propertyId=s.id,message=s.error}),fetchedAt=DateTimeOffset.UtcNow};
    }
    public async Task<object> Refresh(CancellationToken ct)
    {
        await inbox.CheckFeedAsync(ct);await using var db=await factory.CreateDbContextAsync(ct);
        return new{checkedFeed=true,pending=await db.BookingReceipts.CountAsync(r=>r.AckedAt==null,ct)};
    }
    public async Task<object> Create(ManualReservationInput input,Guid actor,CancellationToken ct)
    {
        if(input.OperationId==Guid.Empty||input.RoomId==Guid.Empty||input.RateId==Guid.Empty||string.IsNullOrWhiteSpace(input.GuestName)||input.GuestName.Trim().Length>120||input.Departure<=input.Arrival||(input.Departure.DayNumber-input.Arrival.DayNumber)>500)throw new PortalError(400,"Revise hóspede, apartamento, tarifa e datas da reserva.");
        using var operationLock=files.Lock(input.OperationId);var key="manual-reservation-"+input.OperationId;var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        var job=await files.Load<ManualReservationDelivery>(key);
        if(job is not null&&job.State!="rejected")
        {
            if(job.Actor!=actor||job.Hash!=hash)throw new PortalError(409,"Retome a reserva com os mesmos dados antes de abrir outro cadastro.");
            if(job.State=="confirmed")return new{id=job.ReservationId,saved=true,synchronization="queued"};
            throw new PortalError(409,"A reserva pode ter sido salva. Consulte a lista antes de repetir o cadastro.");
        }
        await using var db=await factory.CreateDbContextAsync(ct);
        var matches=await db.RoomTypes.Where(r=>r.ChannexId==input.RoomId.ToString()).ToListAsync(ct);
        if(matches.Count!=1)throw new PortalError(409,"O vínculo deste apartamento ainda está pendente. Atualize as reservas para tentar a preparação automática.");var room=matches[0];
        var property=await db.Properties.SingleAsync(p=>p.Id==room.PropertyId,ct);var rate=await db.RatePlans.SingleOrDefaultAsync(r=>r.Id==input.RateId&&r.RoomTypeId==room.Id,ct);
        if(rate is null||!Guid.TryParse(rate.ChannexId,out var remoteRate)||!Guid.TryParse(property.ChannexId,out var remoteProperty))throw new PortalError(409,"A preparação automática do prédio e da tarifa ainda não foi concluída. Atualize as reservas.");
        var readiness=await SyncReadiness.CheckAsync(db,property,channex.IsEnabled,ct);
        if(!readiness.Ready)throw new PortalError(409,"Há dados operacionais pendentes neste prédio. Atualize as reservas; se persistir, confira os dados na Channex.");
        if(input.Arrival<PmsService.Today(property)||input.Departure.AddDays(-1)>PmsService.Today(property).AddDays(730))throw new PortalError(400,"A reserva deve iniciar hoje ou no futuro, dentro dos próximos dois anos.");
        await inbox.CheckFeedAsync(ct);
        if(await db.BookingReceipts.AnyAsync(r=>r.AckedAt==null&&(r.PropertyId==property.Id||r.PropertyId==null),ct))throw new PortalError(409,"Há reservas recebidas aguardando processamento. Confira a sincronização antes de cadastrar outra reserva.");
        var remoteRoom=(await channex.ReadStagingJsonAsync($"room_types/{input.RoomId:D}",ct)).GetProperty("data");var remotePlan=(await channex.ReadStagingJsonAsync($"rate_plans/{remoteRate:D}",ct)).GetProperty("data");
        if(Parent(remoteRoom,"property")!=remoteProperty.ToString()||Parent(remotePlan,"property")!=remoteProperty.ToString()||Parent(remotePlan,"room_type")!=input.RoomId.ToString())throw new PortalError(409,"O vínculo do apartamento ou da tarifa mudou na Channex. Revise a sincronização.");
        var availability=await Availability(remoteProperty,input.Arrival,input.Departure.DayNumber-input.Arrival.DayNumber,ct);var daily=availability.GetValueOrDefault(input.RoomId.ToString());
        for(var date=input.Arrival;date<input.Departure;date=date.AddDays(1))if(daily is null||!daily.TryGetValue(date.ToString("yyyy-MM-dd"),out var available)||available<1)throw new PortalError(409,$"A Channex não confirmou disponibilidade em {date:dd/MM/yyyy}.");
        await files.Save(key,new ManualReservationDelivery(actor,hash,"sending",null));
        try
        {
            var saved=await pms.SaveReservationAsync(property.Id,null,new ReservationInput(input.GuestName,room.Id,rate.Id,1,input.Arrival,input.Departure),ct);
            await files.Save(key,new ManualReservationDelivery(actor,hash,"confirmed",saved.Id));return new{id=(Guid?)saved.Id,saved=true,synchronization="queued"};
        }
        catch(AppError e){await files.Save(key,new ManualReservationDelivery(actor,hash,"rejected",null));throw new PortalError(e.Message.Contains("availability",StringComparison.OrdinalIgnoreCase)?409:e.Status,"A reserva não foi criada. Verifique o período e a disponibilidade; outra reserva pode ter ocupado essas datas.");}
    }
}
