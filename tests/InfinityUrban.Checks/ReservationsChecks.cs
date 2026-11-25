using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stayline;
using InfinityUrban.Web;
static class ReservationsChecks
{
 public static async Task Run(IServiceProvider services,HttpClient client,FakeSupabase auth,UnifiedTransport remote,Action<bool,string> check,Func<Task<string>> token,Func<string,object,string?,Task<HttpResponseMessage>> post)
 {
  var factory=services.GetRequiredService<IDbContextFactory<AppDb>>();var property=new Property{Name="Prédio operacional",Currency="BRL",ChannexId=UnifiedTransport.ExistingProperty};var room=new RoomType{PropertyId=property.Id,Name="AP 101",Quantity=1,Occupancy=2,ChannexId=UnifiedTransport.ExistingRoom};var rate=new RatePlan{RoomTypeId=room.Id,Name="Tarifa padrão",BasePrice=200,ChannexId=UnifiedTransport.MappingRate.ToString()};
  await using(var db=await factory.CreateDbContextAsync()){db.Properties.Add(property);db.RoomTypes.Add(room);db.RatePlans.Add(rate);await db.SaveChangesAsync();}
  auth.CurrentRole="funcionario";var csrf=await token();var arrival=PmsService.Today(property).AddDays(10);var input=new ManualReservationInput(Guid.NewGuid(),Guid.Parse(UnifiedTransport.ExistingRoom),rate.Id,"Reserva direta da equipe",arrival,arrival.AddDays(2));
  check((await client.GetAsync("/reservas")).IsSuccessStatusCode,"Approved employees can open the new list and calendar");
  var remoteWrites=remote.Writes.Count;using(var result=await client.GetAsync($"/api/reservations-dashboard?from={arrival:yyyy-MM-dd}&days=21")){using var data=JsonDocument.Parse(await result.Content.ReadAsStringAsync());check(result.IsSuccessStatusCode&&data.RootElement.GetProperty("rooms").EnumerateArray().Any(r=>r.GetProperty("id").GetString()==UnifiedTransport.ExistingRoom&&r.GetProperty("ready").GetBoolean()&&r.GetProperty("days")[0].GetProperty("available").GetInt32()==1),"Calendar reads real-format Channex availability and operational mapping status");}
  check((await post("/api/reservations-dashboard/manual",input,null)).StatusCode==HttpStatusCode.BadRequest,"Employee manual booking requires CSRF");
  check((await post("/api/reservations-dashboard/manual",input with{Departure=arrival},csrf)).StatusCode==HttpStatusCode.BadRequest,"Manual booking rejects zero-night stays");
  remote.NoAvailability=true;check((await post("/api/reservations-dashboard/manual",input,csrf)).StatusCode==HttpStatusCode.Conflict,"Manual booking cannot bypass unavailable dates reported by Channex");remote.NoAvailability=false;
  using var created=await post("/api/reservations-dashboard/manual",input,csrf);using var saved=JsonDocument.Parse(await created.Content.ReadAsStringAsync());var id=saved.RootElement.GetProperty("id").GetGuid();check(created.IsSuccessStatusCode,"Employee creates a direct reservation through the original PmsService");
  await using(var db=await factory.CreateDbContextAsync())check(await db.Outbox.AnyAsync(x=>x.PropertyId==property.Id&&x.Kind=="availability")&&await db.Reservations.AnyAsync(r=>r.Id==id&&r.Source=="direct"),"Manual reservation and availability delta are durably committed by the certified engine");
  check((await post("/api/reservations-dashboard/manual",input,csrf)).IsSuccessStatusCode,"Repeating the same manual operation returns its original reservation");
  await using(var db=await factory.CreateDbContextAsync())check(await db.Reservations.CountAsync(r=>r.PropertyId==property.Id&&r.Source=="direct")==1,"Idempotent manual retry cannot duplicate a reservation");
  check((await post("/api/reservations-dashboard/manual",input with{OperationId=Guid.NewGuid()},csrf)).StatusCode==HttpStatusCode.Conflict,"Local capacity validation still rejects a second overlapping booking even if remote availability lags");
  check((await post("/api/reservations-dashboard/manual",input with{OperationId=Guid.NewGuid(),RoomId=Guid.NewGuid()},csrf)).StatusCode==HttpStatusCode.Conflict,"Unmapped apartments cannot create unsynchronized manual reservations");
  var revision=Guid.NewGuid();var booking=Guid.NewGuid();remote.ReservationFeed.Add(JsonSerializer.SerializeToElement(new{id=revision,type="booking_revision",attributes=new{id=revision,property_id=UnifiedTransport.ExistingProperty,booking_id=booking,status="new",inserted_at=DateTimeOffset.UtcNow.ToString("O"),arrival_date=arrival.AddDays(20).ToString("yyyy-MM-dd"),departure_date=arrival.AddDays(22).ToString("yyyy-MM-dd"),customer=new{name="Hóspede",surname="Channex"},rooms=new[]{new{room_type_id=UnifiedTransport.ExistingRoom,rate_plan_id=UnifiedTransport.MappingRate,checkin_date=arrival.AddDays(20).ToString("yyyy-MM-dd"),checkout_date=arrival.AddDays(22).ToString("yyyy-MM-dd")}}}}));
  check((await post("/api/reservations-dashboard/refresh",new{},csrf)).IsSuccessStatusCode&&remote.ReservationAcks.Contains(revision.ToString()),"Refresh receives a Channex booking through the original feed and acknowledges after processing");
  using(var response=await client.GetAsync($"/api/reservations-dashboard?from={arrival:yyyy-MM-dd}&days=21")){using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var reservations=data.RootElement.GetProperty("reservations");check(reservations.EnumerateArray().Any(r=>r.GetProperty("source").GetString()=="Channex"&&r.GetProperty("code").GetString()==booking.ToString())&&reservations.EnumerateArray().Any(r=>r.GetProperty("id").GetGuid()==id),"The new dashboard lists both feed-delivered and manual reservations without inventing OTA values");}
  using(var response=await client.GetAsync($"/api/reservations-dashboard?from={arrival:yyyy-MM-dd}&days=21"))
  {
    using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var stay=data.RootElement.GetProperty("reservations").EnumerateArray().First(r=>r.GetProperty("id").GetGuid()==id).GetProperty("stays")[0];
    check(stay.GetProperty("roomName").GetString()=="Apartamento existente"&&stay.GetProperty("roomName").GetString()!=room.Name&&stay.GetProperty("propertyName").GetString()!=property.Name,"Reservation labels come from current Channex IDs rather than stale operational room and building names");
    check(stay.GetProperty("roomId").GetString()==UnifiedTransport.ExistingRoom&&stay.GetProperty("propertyId").GetString()==UnifiedTransport.ExistingProperty,"Refreshing display names preserves exact apartment and building links");
  }
  await using(var db=await factory.CreateDbContextAsync())check((await db.RoomTypes.SingleAsync(r=>r.Id==room.Id)).Name=="AP 101"&&(await db.RatePlans.SingleAsync(r=>r.Id==rate.Id)).BasePrice==200,"Display-name reconciliation never edits operational records or prices");
  var concurrent=input with{OperationId=Guid.NewGuid(),Arrival=arrival.AddDays(40),Departure=arrival.AddDays(42)};var another=concurrent with{OperationId=Guid.NewGuid()};
  var races=await Task.WhenAll(post("/api/reservations-dashboard/manual",concurrent,csrf),post("/api/reservations-dashboard/manual",another,csrf));
  check(races.Count(r=>r.IsSuccessStatusCode)==1&&races.Count(r=>r.StatusCode==HttpStatusCode.Conflict)==1,"Concurrent employees cannot both reserve the same apartment nights");foreach(var response in races)response.Dispose();
  remote.FailAvailability=true;
  using(var response=await client.GetAsync($"/api/reservations-dashboard?from={arrival:yyyy-MM-dd}&days=21")){using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());check(response.IsSuccessStatusCode&&data.RootElement.GetProperty("availabilityWarnings").GetArrayLength()>0&&data.RootElement.GetProperty("rooms")[0].GetProperty("days")[0].GetProperty("available").ValueKind==JsonValueKind.Null,"Remote availability failures leave booking history visible and mark unknown days instead of inventing free stock");}remote.FailAvailability=false;
  check((await client.GetAsync($"/api/reservations-dashboard?from={arrival:yyyy-MM-dd}&days=32")).StatusCode==HttpStatusCode.BadRequest,"Calendar rejects unbounded remote date windows");
  check(remote.Writes.Count==remoteWrites,"Dashboard reads and direct reservations never create OTA bookings or bypass the original ARI worker");
  auth.CurrentRole="proprietario";check((await client.GetAsync("/reservas")).StatusCode==HttpStatusCode.Forbidden&&(await client.GetAsync("/api/reservations-dashboard")).StatusCode==HttpStatusCode.Forbidden,"Owners cannot inspect global reservations or calendar data");
  check((await post("/api/reservations-dashboard/manual",input,csrf)).StatusCode==HttpStatusCode.Forbidden,"Owner cannot create a manual booking through a forged request");auth.CurrentRole="master";
 }
}
