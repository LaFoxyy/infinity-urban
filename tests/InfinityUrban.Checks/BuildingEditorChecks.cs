using System.Net;
using System.Text.Json;
using InfinityUrban.Web;
sealed partial class UnifiedTransport
{
 public string SeedEditableBuilding()=>SeedEditableBuilding(Guid.NewGuid().ToString());
 private string SeedEditableBuilding(string id){Store("properties",id,new{title="Prédio editável",currency="BRL",timezone="America/Sao_Paulo",property_type="hotel",settings=new{state_length=500,preserve="protected"},website="https://example.com",address="Rua original",country="BR",facilities=new[]{PropertyFacility},logo_url="https://img.channex.io/original-logo/",content=new{description="Descrição original",important_information="Informações originais",custom_field="Preservar",photos=new[]{new{url="https://img.channex.io/photo-one/",position=0,description="Legenda um",author="Autor original"},new{url="https://img.channex.io/photo-two/",position=1,description="Legenda dois",author="Autor original"}}}});return id;}
 public JsonElement EditedBuilding(string id)=>entities["properties/"+id];
}
static class BuildingEditorChecks
{
 public static async Task Run(HttpClient client,FakeSupabase auth,UnifiedTransport remote,Action<bool,string> check,Func<Task<string>> token,Func<string,object,string?,Task<HttpResponseMessage>> post)
 {
  auth.CurrentRole="master";var id=remote.SeedEditableBuilding();var path=$"/api/accommodations/properties/{id}/edit";var csrf=await token();
  async Task<JsonElement> Read(){using var response=await client.GetAsync(path);check(response.IsSuccessStatusCode,"Building editor loads a fresh remote snapshot");return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();}
  BuildingInput Input(JsonElement snapshot)=>snapshot.GetProperty("input").Deserialize<BuildingInput>(JsonSerializerOptions.Web)! with{PhotoTickets=snapshot.GetProperty("photos").EnumerateArray().Select(p=>p.GetProperty("ticket").GetString()!).ToArray(),LogoTicket=snapshot.GetProperty("logo").ValueKind==JsonValueKind.Null?null:snapshot.GetProperty("logo").GetProperty("ticket").GetString()};
  var original=await Read();var input=Input(original);var revision=original.GetProperty("revision").GetString()!;
  check(input.Title=="Prédio editável"&&input.PhotoTickets!.Length==2&&input.LogoTicket is not null,"Editor preloads existing photos, logo, text and facilities without local copies");
  foreach(var role in new[]{"funcionario","proprietario"}){auth.CurrentRole=role;check((await client.GetAsync(path)).StatusCode==HttpStatusCode.Forbidden&&(await post(path,new BuildingEditInput(input,revision),csrf)).StatusCode==HttpStatusCode.Forbidden,"Building editing and its private assets reject "+role);}auth.CurrentRole="master";
  var writes=remote.Writes.Count;
  check((await post(path,new BuildingEditInput(input,revision),null)).StatusCode==HttpStatusCode.BadRequest&&remote.Writes.Count==writes,"Building edits require anti-forgery protection before remote writes");
  check((await post(path,new BuildingEditInput(input with{Currency="USD"},revision),csrf)).StatusCode==HttpStatusCode.BadRequest&&(await post(path,new BuildingEditInput(input with{Timezone="UTC"},revision),csrf)).StatusCode==HttpStatusCode.BadRequest,"Editing cannot change currency or timezone");
  check((await post(path,new BuildingEditInput(input with{PhotoTickets=Enumerable.Repeat(input.PhotoTickets![0],31).ToArray()},revision),csrf)).StatusCode==HttpStatusCode.BadRequest,"Building edit enforces the 30-photo gallery limit");
  check((await post(path,new BuildingEditInput(input with{Facilities=[Guid.NewGuid()]},revision),csrf)).StatusCode==HttpStatusCode.BadRequest,"Unknown Channex facilities cannot be saved");
  var otherId=remote.SeedEditableBuilding();
  // Each snapshot is bound to a property; use its own revision so asset validation is exercised.
  using var otherRead=await client.GetAsync($"/api/accommodations/properties/{otherId}/edit");var otherSnapshot=JsonDocument.Parse(await otherRead.Content.ReadAsStringAsync()).RootElement;
  check((await post($"/api/accommodations/properties/{otherId}/edit",new BuildingEditInput(input,otherSnapshot.GetProperty("revision").GetString()!),csrf)).StatusCode==HttpStatusCode.BadRequest,"Existing image references cannot be reused on another property");
  check((await post(path,new BuildingEditInput(input with{PhotoTickets=["forged-reference"]},revision),csrf)).StatusCode==HttpStatusCode.BadRequest&&remote.Writes.Count==writes,"Untrusted image references are rejected before remote writes");
  var changed=input with{Title="Prédio atualizado",Address="Rua atualizada",City="São Paulo",State="SP",ZipCode="01234-000",Email="novo@example.com",Phone="+5511999999999",Description="Nova descrição",ImportantInformation="Novas orientações",PhotoTickets=input.PhotoTickets!.Reverse().ToArray()};
  using var saved=await post(path,new BuildingEditInput(changed,revision),csrf);var savedText=await saved.Content.ReadAsStringAsync();
  check(saved.IsSuccessStatusCode&&JsonDocument.Parse(savedText).RootElement.GetProperty("confirmed").GetBoolean(),"Building changes are sent to Channex and verified with a fresh read");
  var payload=remote.Writes.Last().Body.GetProperty("property");var attrs=remote.EditedBuilding(id).GetProperty("attributes");
  check(!payload.TryGetProperty("currency",out _)&&!payload.TryGetProperty("timezone",out _)&&!payload.TryGetProperty("settings",out _)&&!payload.TryGetProperty("property_type",out _)&&attrs.GetProperty("settings").GetProperty("preserve").GetString()=="protected"&&attrs.GetProperty("website").GetString()=="https://example.com","Metadata edits preserve operational settings and fields outside the form");
  check(attrs.GetProperty("content").GetProperty("custom_field").GetString()=="Preservar"&&!payload.TryGetProperty("logo_url",out _)&&attrs.GetProperty("logo_url").GetString()=="https://img.channex.io/original-logo/","Text edits preserve unknown content and the current logo");
  var originalIds=original.GetProperty("photos").EnumerateArray().Select(p=>p.GetProperty("url").GetString()).Reverse().ToArray();
  check(attrs.GetProperty("content").GetProperty("photos").EnumerateArray().OrderBy(p=>p.GetProperty("position").GetInt32()).Select(p=>p.GetProperty("url").GetString()).SequenceEqual(originalIds)&&payload.GetProperty("content").GetProperty("photos")[0].TryGetProperty("id",out _),"Reordering existing photos sends their IDs and preserves their identity");
  check((await post(path,new BuildingEditInput(input,revision),csrf)).StatusCode==HttpStatusCode.Conflict,"A stale editor cannot overwrite newer Channex content");
  using var photoRequest=new HttpRequestMessage(HttpMethod.Post,"/api/accommodations/photos");photoRequest.Headers.Add("X-CSRF-Token",csrf);var multipart=new MultipartFormDataContent();multipart.Add(new ByteArrayContent(new byte[]{255,216,255,0,0,0,0,0,0,0,0,0}),"photo","new.jpg");photoRequest.Content=multipart;using var uploaded=await client.SendAsync(photoRequest);var ticket=JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()).RootElement.GetProperty("ticket").GetString()!;
  var fresh=await Read();var freshInput=Input(fresh);var retained=freshInput.PhotoTickets![0];
  using var photosSaved=await post(path,new BuildingEditInput(freshInput with{PhotoTickets=[retained,ticket],LogoTicket=ticket},fresh.GetProperty("revision").GetString()!),csrf);
  check(photosSaved.IsSuccessStatusCode,"Editing adds new photos and replaces the logo using authenticated uploads");
  payload=remote.Writes.Last().Body.GetProperty("property");attrs=remote.EditedBuilding(id).GetProperty("attributes");
  check(payload.GetProperty("content").GetProperty("photos").EnumerateArray().Count(p=>p.TryGetProperty("is_removed",out var removed)&&removed.GetBoolean())==1&&attrs.GetProperty("content").GetProperty("photos").GetArrayLength()==2,"Removing a photo sends the explicit Channex removal flag without duplicating the gallery");
  fresh=await Read();freshInput=Input(fresh);auth.CurrentRole="admin";
  check((await post(path,new BuildingEditInput(freshInput with{PhotoTickets=[],LogoTicket=null,Address="",City="",State="",Country="",ZipCode="",Email="",Phone="",Description=""},fresh.GetProperty("revision").GetString()!),csrf)).IsSuccessStatusCode,"Administrators can edit minimal properties and explicitly clear photos or logo");auth.CurrentRole="master";
  check(remote.Writes.Skip(writes).All(w=>w.Resource=="properties/"+id),"Building editing never creates properties or changes rooms, prices, inventory or reservations");
 }
}
