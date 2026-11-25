using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stayline;
using InfinityUrban.Web;
static class OperationalLinksChecks
{
 public static async Task Run(IServiceProvider services,UnifiedTransport remote,Action<bool,string> check)
 {
  remote.SeedOperationalProperty();var factory=services.GetRequiredService<IDbContextFactory<AppDb>>();using var scope=services.CreateScope();var links=scope.ServiceProvider.GetRequiredService<OperationalLinks>();
  int before;await using(var db=await factory.CreateDbContextAsync())before=await db.Outbox.CountAsync();var writes=remote.Writes.Count;
  remote.IncompleteOperationalSnapshot=true;var incomplete=await links.Reconcile(remote.OperationalProperty,default);
  check(incomplete.Issues.Length==1,"Incomplete Channex calendar is reported rather than initialized with guessed values");
  await using(var db=await factory.CreateDbContextAsync())check(!await db.Properties.AnyAsync(p=>p.ChannexId==remote.OperationalProperty.ToString()),"Incomplete automatic setup rolls back every property/room/rate change");
  remote.IncompleteOperationalSnapshot=false;var first=await links.Reconcile(remote.OperationalProperty,default);
  check(first.AddedRooms==1&&first.AddedRates==1&&first.Issues.Length==0,"A valid Channex apartment acquires its operational links automatically");
  Guid roomId,rateId;DateOnly today;
  await using(var db=await factory.CreateDbContextAsync())
  {
   var property=await db.Properties.SingleAsync(p=>p.ChannexId==remote.OperationalProperty.ToString());var room=await db.RoomTypes.SingleAsync(r=>r.ChannexId==remote.OperationalRoom.ToString());var rate=await db.RatePlans.SingleAsync(r=>r.ChannexId==remote.OperationalRate.ToString());roomId=room.Id;rateId=rate.Id;today=PmsService.Today(property);
   check(property.Name=="Edifício automático"&&room.Name=="Apartamento automático"&&room.Occupancy==3,"Automatic links use remote IDs, names, currency and occupancy");
   check(await db.DailyInventories.CountAsync(d=>d.RoomTypeId==room.Id)==500&&await db.DailyRates.CountAsync(d=>d.RatePlanId==rate.Id)==500,"Automatic preparation reads the complete 500-day authoritative calendar");
   check(rate.BasePrice==348m&&await db.DailyRates.AllAsync(d=>d.RatePlanId!=rate.Id||d.Rate==245m&&d.MinStayArrival==2&&d.MaxStay==30),"Daily prices and restrictions are copied from Channex instead of replacing them with a base price");
   check((await SyncReadiness.CheckAsync(db,property,true,default)).Ready,"Prepared apartments satisfy the unchanged certified engine readiness checks");
  }
  remote.OperationalCalendarPrice=777;var repeat=await links.Reconcile(remote.OperationalProperty,default);
  check(repeat.AddedRooms==0&&repeat.AddedRates==0&&repeat.Issues.Length==0,"Automatic reconciliation is idempotent and does not duplicate inventory");
  await using(var db=await factory.CreateDbContextAsync())check((await db.RatePlans.SingleAsync(r=>r.Id==rateId)).BasePrice==348m&&(await db.DailyRates.FirstAsync(r=>r.RatePlanId==rateId)).Rate==245m,"Existing operational prices survive later automatic refreshes unchanged");
  check(remote.Writes.Count==writes,"Automatic preparation performs no Channex metadata or ARI writes");
  await using(var db=await factory.CreateDbContextAsync())check(await db.Outbox.CountAsync()==before,"Automatic preparation never queues a full synchronization or any ARI delta");
  remote.OperationalProperty=Guid.NewGuid();remote.OperationalRoom=Guid.NewGuid();remote.OperationalRate=Guid.NewGuid();remote.SeedOperationalProperty();remote.NoAvailability=true;remote.OperationalCalendarPrice=275;
  var revision=Guid.NewGuid();var booking=Guid.NewGuid();var arrival=today.AddDays(3);var departure=today.AddDays(5);
  remote.ReservationFeed.Add(JsonSerializer.SerializeToElement(new{id=revision,type="booking_revision",attributes=new{id=revision,property_id=remote.OperationalProperty,booking_id=booking,status="new",inserted_at=DateTimeOffset.UtcNow.ToString("O"),arrival_date=arrival.ToString("yyyy-MM-dd"),departure_date=departure.ToString("yyyy-MM-dd"),customer=new{name="Reserva pendente"},rooms=new[]{new{room_type_id=remote.OperationalRoom,rate_plan_id=remote.OperationalRate,checkin_date=arrival.ToString("yyyy-MM-dd"),checkout_date=departure.ToString("yyyy-MM-dd")}}}}));
  var pendingResult=await links.Reconcile(remote.OperationalProperty,default);check(pendingResult.AddedRooms==1&&pendingResult.Issues.Length==0,"Automatic setup accounts for authoritative pending booking revisions before linking a new apartment");
  Guid pendingLocalRoom;await using(var db=await factory.CreateDbContextAsync())
  {
    pendingLocalRoom=(await db.RoomTypes.SingleAsync(r=>r.ChannexId==remote.OperationalRoom.ToString())).Id;
    check((await db.DailyInventories.SingleAsync(d=>d.RoomTypeId==pendingLocalRoom&&d.Date==arrival)).Capacity==1&&(await db.DailyInventories.SingleAsync(d=>d.RoomTypeId==pendingLocalRoom&&d.Date==today)).Capacity==0,"Closed stock stays closed while booked stock restores its capacity before the feed subtracts the booking");
    check(await db.BookingReceipts.AnyAsync(r=>r.Id==revision.ToString()&&r.AckedAt==null)&&await db.Outbox.CountAsync()==before,"Preparing operational links neither acknowledges bookings nor queues remote changes");
  }
  await services.GetRequiredService<BookingInboxWorker>().CheckFeedAsync(default);
  await using(var db=await factory.CreateDbContextAsync())
  {
    var reservation=await db.Reservations.Include(r=>r.Stays).SingleAsync(r=>r.ExternalBookingId==booking.ToString());
    var capacity=(await db.DailyInventories.SingleAsync(d=>d.RoomTypeId==pendingLocalRoom&&d.Date==arrival)).Capacity;
    check(capacity-PmsService.Booked(new[]{reservation},pendingLocalRoom,arrival)==0,"The unchanged feed consumer applies the newly linked booking exactly once without double-counting availability");
  }
  remote.NoAvailability=false;
  remote.OperationalProperty=Guid.NewGuid();remote.OperationalRoom=Guid.NewGuid();remote.OperationalRate=Guid.NewGuid();remote.SeedOperationalProperty();remote.RemoveRoomOnRead=remote.OperationalRoom.ToString();
  var deletedDuringRead=await links.Reconcile(remote.OperationalProperty,default);
  check(deletedDuringRead.Issues.Length==1,"Automatic adoption rejects an apartment deleted after the initial metadata snapshot");
  await using(var db=await factory.CreateDbContextAsync())check(!await db.RoomTypes.AnyAsync(r=>r.ChannexId==remote.OperationalRoom.ToString())&&!await db.Properties.AnyAsync(p=>p.ChannexId==remote.OperationalProperty.ToString()),"A stale read cannot recreate operational links for deleted inventory");

 }
}
