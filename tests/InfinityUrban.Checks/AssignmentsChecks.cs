using System.Net;
using System.Text.Json;
using InfinityUrban.Web;
static class AssignmentsChecks
{
 public static async Task Run(HttpClient client,FakeSupabase auth,UnifiedTransport remote,Action<bool,string> check,Func<Task<string>> token,Func<string,object,string?,Task<HttpResponseMessage>> post)
 {
  var csrf=await token();var beforeWrites=remote.Writes.Count;auth.CurrentRole="master";
  check((await client.GetAsync("/designacoes")).IsSuccessStatusCode,"Master can open the account assignment screen");
  using var listed=await client.GetAsync("/api/assignments");using var listDoc=JsonDocument.Parse(await listed.Content.ReadAsStringAsync());var account=listDoc.RootElement.GetProperty("accounts").EnumerateArray().First();var target=account.GetProperty("id").GetGuid();
  var input=new AssignmentInput(target,[Guid.Parse(UnifiedTransport.ExistingRoom),Guid.Parse(UnifiedTransport.SiblingRoom)]);
  var before=auth.AssignmentWrites;check((await post("/api/assignments",input,null)).StatusCode==HttpStatusCode.BadRequest&&auth.AssignmentWrites==before,"Assignment changes require CSRF protection");
  check((await post("/api/assignments",input with{UserId=Guid.Parse(FakeSupabase.CurrentId)},csrf)).StatusCode==HttpStatusCode.BadRequest,"A team account cannot be mislabeled as a restricted owner account");
  using var added=await post("/api/assignments",input,csrf);using var addedDoc=JsonDocument.Parse(await added.Content.ReadAsStringAsync());check(added.IsSuccessStatusCode&&addedDoc.RootElement.GetProperty("assignments").GetArrayLength()==2,"An approved owner account can receive multiple individual Room assignments");
  check(addedDoc.RootElement.GetProperty("assignments").EnumerateArray().All(a=>a.GetProperty("propertyId").GetGuid()==Guid.Parse(UnifiedTransport.ExistingProperty)),"Assignment parent IDs are derived from Channex rather than browser input");
  check((await post("/api/assignments",input,csrf)).IsSuccessStatusCode,"Retrying assignment creation does not duplicate access pairs");
  auth.CurrentRole="funcionario";check((await client.GetAsync("/api/assignments")).StatusCode==HttpStatusCode.Forbidden&&(await post("/api/assignments",input,csrf)).StatusCode==HttpStatusCode.Forbidden,"Staff cannot administer account designations");
  auth.CurrentRole="proprietario";var calls=remote.Hosts.Count;
  using(var empty=await client.GetAsync("/api/accommodations")){using var data=JsonDocument.Parse(await empty.Content.ReadAsStringAsync());check(empty.IsSuccessStatusCode&&data.RootElement.GetProperty("rooms").GetArrayLength()==0&&data.RootElement.GetProperty("properties").GetArrayLength()==0&&remote.Hosts.Count==calls,"An unassigned owner gets no portfolio data and triggers no Channex metadata reads");}
  var self=Guid.Parse(FakeSupabase.CurrentId);var allowed=auth.SeedAssignment(self,Guid.Parse(UnifiedTransport.ExistingRoom),Guid.Parse(UnifiedTransport.ExistingProperty));
  using(var owned=await client.GetAsync("/api/accommodations")){var text=await owned.Content.ReadAsStringAsync();using var data=JsonDocument.Parse(text);check(owned.IsSuccessStatusCode&&data.RootElement.GetProperty("rooms").GetArrayLength()==1&&data.RootElement.GetProperty("rooms")[0].GetProperty("id").GetGuid()==Guid.Parse(UnifiedTransport.ExistingRoom),"Owner sees only the assigned Room even when its building contains another apartment");check(!text.Contains("PRIVATE SIBLING")&&!text.Contains("private-sibling-photo")&&!text.Contains("99999")&&!text.Contains("private-building@example.com")&&!text.Contains("property-photo-must-not-show"),"Other apartment photos, tariffs and private building content are not included in the owner response");}
  using(var injected=await client.GetAsync($"/api/accommodations?userId={target}&roomId={UnifiedTransport.SiblingRoom}")){using var data=JsonDocument.Parse(await injected.Content.ReadAsStringAsync());check(data.RootElement.GetProperty("rooms").GetArrayLength()==1&&data.RootElement.GetProperty("rooms")[0].GetProperty("id").GetGuid()==Guid.Parse(UnifiedTransport.ExistingRoom),"Query parameters cannot impersonate another assigned account");}
  check((await client.GetAsync($"/api/accommodations/rooms/{UnifiedTransport.SiblingRoom}")).StatusCode==HttpStatusCode.NotFound,"A direct URL to another apartment is denied on the server");
  check((await client.GetAsync($"/api/accommodations/rooms/{UnifiedTransport.ExistingRoom}")).IsSuccessStatusCode,"The assigned Room detail can be read directly");
  foreach(var path in new[]{"/api/properties","/API/PROPERTIES/","/api/channex/remote","/api/owners","/api/assignments","/api/management/users","/api/accommodations/facilities","/operacoes"})check((await client.GetAsync(path)).StatusCode==HttpStatusCode.Forbidden,"Restricted owner cannot bypass its portfolio through "+path);
  before=auth.AssignmentWrites;check((await post("/api/assignments",input with{UserId=self},csrf)).StatusCode==HttpStatusCode.Forbidden&&auth.AssignmentWrites==before,"An owner cannot grant itself access to another apartment");
  auth.FailAssignments=true;check((await client.GetAsync("/api/accommodations")).StatusCode==HttpStatusCode.ServiceUnavailable,"An unavailable assignment store blocks access instead of falling back to the full catalog");auth.FailAssignments=false;
  auth.RevokeAssignmentsAfterRead=true;check((await client.GetAsync("/api/accommodations")).StatusCode==HttpStatusCode.Conflict,"Revocation during a metadata fetch prevents the previously fetched response from being released");
  var renewed=auth.SeedAssignment(self,Guid.Parse(UnifiedTransport.ExistingRoom),Guid.Parse(UnifiedTransport.ExistingProperty));auth.CurrentRole="admin";check((await post($"/api/assignments/{renewed.Id}/remove",new{},csrf)).IsSuccessStatusCode,"Admin can revoke a specific Room assignment");auth.CurrentRole="proprietario";
  using(var revoked=await client.GetAsync("/api/accommodations")){using var data=JsonDocument.Parse(await revoked.Content.ReadAsStringAsync());check(data.RootElement.GetProperty("rooms").GetArrayLength()==0,"Revoked owner loses Room visibility on the next request");}
  check(remote.Writes.Count==beforeWrites,"Account designations never change the approved Channex connection or metadata");auth.CurrentRole="master";
 }
}
