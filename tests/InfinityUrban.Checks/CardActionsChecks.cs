using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Stayline;
using InfinityUrban.Web;
sealed partial class UnifiedTransport
{
 public bool RejectDelete;
 public string SeedEditableRoom(string property){var id=Guid.NewGuid().ToString();Store("room_types",id,new{title="Apartamento editável",property_id=property,occ_adults=3,occ_children=0,default_occupancy=2,count_of_rooms=1,content=new{description="Descrição do quarto",preserve="unknown content",photos=new[]{new{url="https://img.channex.io/room-one/",position=0}}}});return id;}
 public JsonElement EditedRoom(string id)=>entities["room_types/"+id];
}
static class CardActionsChecks
{
 public static async Task Run(HttpClient client,FakeSupabase auth,UnifiedTransport remote,IServiceProvider services,Action<bool,string> check,Func<Task<string>> token,Func<string,object,string?,Task<HttpResponseMessage>> post)
 {
  auth.CurrentRole="master";var csrf=await token();var property=remote.SeedEditableBuilding();var room=remote.SeedEditableRoom(property);var path=$"/api/accommodations/rooms/{room}/edit";
  async Task<JsonElement> Read(string url){using var response=await client.GetAsync(url);if(!response.IsSuccessStatusCode)throw new Exception(await response.Content.ReadAsStringAsync());return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();}
  var snapshot=await Read(path);var image=snapshot.GetProperty("photos")[0].GetProperty("ticket").GetString()!;
  var edit=new RoomEditInput("Apartamento atualizado","101",1,2,1,"Descrição nova",[],[image],snapshot.GetProperty("revision").GetString()!);
  foreach(var role in new[]{"funcionario","proprietario"}){auth.CurrentRole=role;check((await client.GetAsync(path)).StatusCode==HttpStatusCode.Forbidden&&(await post(path,edit,csrf)).StatusCode==HttpStatusCode.Forbidden,"Apartment edits reject "+role);}auth.CurrentRole="master";
  var before=remote.Writes.Count;
  check((await post(path,edit,null)).StatusCode==HttpStatusCode.BadRequest&&remote.Writes.Count==before,"Apartment edit requires CSRF before writing");
  check((await post(path,edit with{PhotoTickets=Enumerable.Repeat(image,11).ToArray()},csrf)).StatusCode==HttpStatusCode.BadRequest,"Apartment edit rejects more than 10 images");
  check((await post(path,edit with{PhotoTickets=["forged"]},csrf)).StatusCode==HttpStatusCode.BadRequest,"Apartment edit rejects arbitrary photo references");
  using var saved=await post(path,edit,csrf);check(saved.IsSuccessStatusCode,"Apartment text, details and photos save directly to Channex");
  var attrs=remote.EditedRoom(room).GetProperty("attributes");var payload=remote.Writes.Last().Body.GetProperty("room_type");
  check(attrs.GetProperty("occ_adults").GetInt32()==3&&attrs.GetProperty("default_occupancy").GetInt32()==2&&attrs.GetProperty("content").GetProperty("preserve").GetString()=="unknown content"&&!payload.TryGetProperty("property_id",out _)&&!payload.TryGetProperty("occ_adults",out _),"Apartment edit preserves parent, occupancy, tariffs and unknown content");
  check(payload.GetProperty("content").GetProperty("photos")[0].TryGetProperty("id",out _)&&attrs.GetProperty("content").GetProperty("description").GetString()!.Contains("Código: 101"),"Apartment editing preserves photo IDs and saves apartment details in Channex");
  check((await post(path,edit,csrf)).StatusCode==HttpStatusCode.Conflict,"Stale apartment edits cannot overwrite newer content");
  var otherRoom=remote.SeedEditableRoom(property);var other=await Read($"/api/accommodations/rooms/{otherRoom}/edit");check((await post($"/api/accommodations/rooms/{otherRoom}/edit",edit with{ExpectedRevision=other.GetProperty("revision").GetString()!},csrf)).StatusCode==HttpStatusCode.BadRequest,"Photo references are bound to the correct apartment");
  snapshot=await Read(path);using var removed=await post(path,edit with{PhotoTickets=[],ExpectedRevision=snapshot.GetProperty("revision").GetString()!},csrf);check(removed.IsSuccessStatusCode&&remote.EditedRoom(room).GetProperty("attributes").GetProperty("content").GetProperty("photos").GetArrayLength()==0,"Apartment photos can be removed through explicit Channex removal flags");
  var deleteUrl=$"/api/accommodations/room_types/{room}/delete";var deletion=new AccommodationDeleteInput(edit.Title,true);
  foreach(var role in new[]{"master","funcionario","proprietario"}){auth.CurrentRole=role;check((await post(deleteUrl,deletion,csrf)).StatusCode==HttpStatusCode.Forbidden,"Deletion is admin-only, including rejection of "+role);}auth.CurrentRole="admin";
  check((await post(deleteUrl,deletion,null)).StatusCode==HttpStatusCode.BadRequest,"Deletion requires CSRF protection");
  check((await post(deleteUrl,deletion with{Confirm=false},csrf)).StatusCode==HttpStatusCode.BadRequest&&(await post(deleteUrl,deletion with{ExpectedTitle="wrong"},csrf)).StatusCode==HttpStatusCode.Conflict,"Deletion requires confirmation of the current record name");
  check((await post($"/api/accommodations/properties/{property}/delete",new AccommodationDeleteInput("Prédio editável",true),csrf)).StatusCode==HttpStatusCode.Conflict,"Deleting a building with apartments is blocked before remote deletion");
  remote.RejectDelete=true;check((await post(deleteUrl,deletion,csrf)).StatusCode==HttpStatusCode.Conflict,"Channex linked-inventory deletion refusal is respected without force");remote.RejectDelete=false;
  Guid detachedRoom,detachedRate;
  using(var scope=services.CreateScope()){await using var db=await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();var p=new Property{Name="detachable",ChannexId=property};var r=new RoomType{PropertyId=p.Id,ChannexId=room,Name="detachable",Quantity=1};var rate=new RatePlan{RoomTypeId=r.Id,ChannexId=Guid.NewGuid().ToString()};db.AddRange(p,r,rate);await db.SaveChangesAsync();detachedRoom=r.Id;detachedRate=rate.Id;}
  using var deleted=await post(deleteUrl,deletion,csrf);check(deleted.IsSuccessStatusCode,"Admin deletion is confirmed by a Channex not-found response");
  using(var scope=services.CreateScope()){await using var db=await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();check((await db.RoomTypes.SingleAsync(r=>r.Id==detachedRoom)).ChannexId is null&&(await db.RatePlans.SingleAsync(r=>r.Id==detachedRate)).ChannexId is null,"Confirmed deletion detaches operational remote IDs without deleting historical records");}
  var empty=remote.SeedEditableBuilding();check((await post($"/api/accommodations/properties/{empty}/delete",new AccommodationDeleteInput("Prédio editável",true),csrf)).IsSuccessStatusCode,"Admin can delete an empty building directly from its card");
  // Locally received/manual reservations must also protect a room, even if remote metadata is otherwise removable.
  using(var scope=services.CreateScope()){await using var db=await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();var p=await db.Properties.SingleAsync(p=>p.ChannexId==property);var r=new RoomType{PropertyId=p.Id,ChannexId=otherRoom,Name="protected",Quantity=1};var rate=new RatePlan{RoomTypeId=r.Id};var reservation=new Reservation{PropertyId=p.Id,GuestName="Protected",Arrival=new(2026,11,25),Departure=new(2026,11,26),Stays=[new ReservationStay{RoomTypeId=r.Id,RatePlanId=rate.Id}]};db.AddRange(r,rate,reservation);await db.SaveChangesAsync();}
  check((await post($"/api/accommodations/room_types/{otherRoom}/delete",new AccommodationDeleteInput("Apartamento editável",true),csrf)).StatusCode==HttpStatusCode.Conflict,"Apartment history protects reservations from deletion");
  auth.CurrentRole="funcionario";var channel=$"/api/channel-mapping/{UnifiedTransport.MappingChannel}";var general=await Read(channel+"/general");var selected=general.GetProperty("selectedPropertyIds").EnumerateArray().Select(p=>p.GetGuid()).Append(Guid.Parse(property)).ToArray();
  var settings=new ChannelGeneralInput("Canal atualizado pela equipe",selected,general.GetProperty("revision").GetString()!,true);var writes=remote.GeneralWrites;
  check((await client.GetAsync("/mapeamento")).IsSuccessStatusCode&&general.GetProperty("properties").GetArrayLength()>1&&!general.GetRawText().Contains("NEVER EXPOSE"),"Staff can open mapping and see all selectable channel properties without credentials");
  check((await post(channel+"/general",settings,null)).StatusCode==HttpStatusCode.BadRequest&&remote.GeneralWrites==writes,"Channel general settings require CSRF");
  check((await post(channel+"/general",settings with{PropertyIds=[Guid.NewGuid()]},csrf)).StatusCode==HttpStatusCode.BadRequest,"Channel selection rejects inaccessible property IDs");
  check((await post(channel+"/general",settings with{Confirm=false},csrf)).StatusCode==HttpStatusCode.BadRequest,"Channel settings require explicit synchronization confirmation");
  using var settingsSaved=await post(channel+"/general",settings,csrf);check(settingsSaved.IsSuccessStatusCode&&remote.GeneralWrites==writes+1,"Staff can attach a property to the existing Airbnb connection");
  var channelPayload=remote.GeneralPayload.GetProperty("channel");check(channelPayload.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"channel","properties","title"}),"Channel update omits credentials, pricing settings, group, currency and the mapping set");
  var view=await Read(channel);check(view.GetProperty("properties").EnumerateArray().Any(p=>p.GetProperty("id").GetString()==property)&&view.GetProperty("rooms").EnumerateArray().Any(r=>r.GetProperty("id").GetString()==otherRoom),"Newly selected properties and apartments appear immediately in mapping");
  check((await post(channel+"/general",settings,csrf)).StatusCode==HttpStatusCode.Conflict,"Stale channel settings cannot overwrite another operator");
  general=await Read(channel+"/general");check((await post(channel+"/general",settings with{PropertyIds=[Guid.Parse(property)],ExpectedRevision=general.GetProperty("revision").GetString()!},csrf)).StatusCode==HttpStatusCode.Conflict,"Removing a property with mappings is refused");
  auth.CurrentRole="proprietario";check((await client.GetAsync(channel+"/general")).StatusCode==HttpStatusCode.Forbidden&&(await post(channel+"/general",settings,csrf)).StatusCode==HttpStatusCode.Forbidden,"Owners cannot read or change channel property selection");auth.CurrentRole="master";
 }
}
