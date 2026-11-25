using System.Net;
using System.Text.Json;
using InfinityUrban.Web;
static class OwnersChecks
{
 public static async Task Run(HttpClient client,FakeSupabase auth,UnifiedTransport remote,Action<bool,string> check,Func<Task<string>> token,Func<string,object,string?,Task<HttpResponseMessage>> post)
 {
  var csrf=await token();auth.SeedOwners();var ownerInput=new OwnerInput(Guid.NewGuid(),"Álvaro Proprietário","alvaro@example.com","+5511999999999");
  check((await client.GetAsync("/proprietarios")).IsSuccessStatusCode,"Owners page is available to the approved team");
  using(var listed=await client.GetAsync("/api/owners")){using var data=JsonDocument.Parse(await listed.Content.ReadAsStringAsync());var rows=data.RootElement.GetProperty("owners");check(rows.GetArrayLength()==5&&!rows.GetRawText().Contains("STAGING OWNER"),"Owners listing reads all capped Supabase pages and isolates the Channex environment");}
  var beforeCsrf=auth.OwnerWrites;check((await post("/api/owners",ownerInput,null)).StatusCode==HttpStatusCode.BadRequest&&auth.OwnerWrites==beforeCsrf,"Owner writes without CSRF protection cannot reach Supabase");
  var writes=remote.Writes.Count;using var created=await post("/api/owners",ownerInput,csrf);using var ownerDoc=JsonDocument.Parse(await created.Content.ReadAsStringAsync());check(created.IsSuccessStatusCode&&ownerDoc.RootElement.GetProperty("fullName").GetString()==ownerInput.FullName,"Owner contact is saved to Supabase with the end-user JWT");var ownerId=ownerDoc.RootElement.GetProperty("id").GetGuid();var revision=ownerDoc.RootElement.GetProperty("revision").GetGuid();
  check((await post("/api/owners",ownerInput,csrf)).IsSuccessStatusCode,"Repeating owner creation does not duplicate the contact");
  check((await post("/api/owners",ownerInput with{Id=Guid.NewGuid(),Email="invalid-address"},csrf)).StatusCode==HttpStatusCode.BadRequest,"Invalid owner contact data is rejected");
  auth.CurrentRole="funcionario";var ownerWrites=auth.OwnerWrites;check((await client.GetAsync("/api/owners")).IsSuccessStatusCode,"Staff may consult owners");check((await post("/api/owners",ownerInput,csrf)).StatusCode==HttpStatusCode.Forbidden&&auth.OwnerWrites==ownerWrites,"Staff cannot modify owner contacts");
  auth.CurrentRole="proprietario";check((await client.GetAsync("/api/owners")).StatusCode==HttpStatusCode.Forbidden,"Owner accounts cannot list other owners and apartment assignments");auth.CurrentRole="master";
  var roomId=Guid.Parse(UnifiedTransport.ExistingRoom);using var linked=await post($"/api/owners/rooms/{roomId}",new RoomOwnerInput(ownerId,null,null),csrf);using var linkDoc=JsonDocument.Parse(await linked.Content.ReadAsStringAsync());var link=linkDoc.RootElement.GetProperty("assignment");var linkRevision=link.GetProperty("revision").GetGuid();check(linked.IsSuccessStatusCode&&link.GetProperty("propertyId").GetGuid()==Guid.Parse(UnifiedTransport.ExistingProperty),"Room association is stored in Supabase using the parent verified by Channex");
  check((await post($"/api/owners/rooms/{roomId}",new RoomOwnerInput(ownerId,null,null),csrf)).IsSuccessStatusCode,"Retrying the same room-owner association is idempotent");
  var other=ownerInput with{Id=Guid.NewGuid(),FullName="Outro proprietário",Email=null};using var second=await post("/api/owners",other,csrf);
  check((await post($"/api/owners/rooms/{roomId}",new RoomOwnerInput(other.Id,null,null),csrf)).StatusCode==HttpStatusCode.Conflict,"A stale browser cannot replace an existing apartment owner");
  check((await post($"/api/owners/{ownerId}/delete",new OwnerDelete(revision),csrf)).StatusCode==HttpStatusCode.Conflict,"Owners with linked apartments cannot be deleted");
  using var updated=await post("/api/owners",ownerInput with{FullName="Álvaro Atualizado",ExpectedRevision=revision},csrf);using var updatedDoc=JsonDocument.Parse(await updated.Content.ReadAsStringAsync());var newRevision=updatedDoc.RootElement.GetProperty("revision").GetGuid();check(updated.IsSuccessStatusCode&&newRevision!=revision,"Editing a contact creates a new revision");check((await post("/api/owners",ownerInput with{ExpectedRevision=revision},csrf)).StatusCode==HttpStatusCode.Conflict,"Concurrent contact edits cannot silently overwrite newer data");
  using var moved=await post($"/api/owners/rooms/{roomId}",new RoomOwnerInput(other.Id,ownerId,linkRevision),csrf);using var movedDoc=JsonDocument.Parse(await moved.Content.ReadAsStringAsync());var movedRevision=movedDoc.RootElement.GetProperty("assignment").GetProperty("revision").GetGuid();check(moved.IsSuccessStatusCode,"A reviewed assignment can be transferred to another owner");
  check((await post($"/api/owners/rooms/{roomId}",new RoomOwnerInput(null,ownerId,linkRevision),csrf)).StatusCode==HttpStatusCode.Conflict,"Stale unlink requests cannot remove a newer owner's assignment");
  check((await post($"/api/owners/rooms/{roomId}",new RoomOwnerInput(null,other.Id,movedRevision),csrf)).IsSuccessStatusCode,"An apartment owner can be unlinked independently of Channex metadata");
  check((await post($"/api/owners/{ownerId}/delete",new OwnerDelete(newRevision),csrf)).IsSuccessStatusCode,"A contact without apartments may be deleted");
  auth.CurrentRole="admin";check((await post("/api/owners",ownerInput with{Id=Guid.NewGuid(),FullName="Cadastro por Admin"},csrf)).IsSuccessStatusCode,"Approved Admin may manage owner contacts without changing account permissions");auth.CurrentRole="master";
  check(remote.Writes.Count==writes,"Owner creation, editing and assignments never write to Channex");
 }
}
