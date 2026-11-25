using System.Net;
using System.Text.Json;
using InfinityUrban.Web;
static class ChannelMappingChecks
{
 public static async Task Run(HttpClient client,FakeSupabase auth,UnifiedTransport remote,Action<bool,string> check,Func<Task<string>> token,Func<string,object,string?,Task<HttpResponseMessage>> post)
 {
  auth.CurrentRole="master";remote.SeedMappingRates();var csrf=await token();var url="/api/channel-mapping/"+UnifiedTransport.MappingChannel;
  check((await client.GetAsync("/mapeamento")).IsSuccessStatusCode,"Master may open the native Airbnb mapping page");
  using var listed=await client.GetAsync("/api/channel-mapping");var listText=await listed.Content.ReadAsStringAsync();check(listed.IsSuccessStatusCode&&!listText.Contains("NEVER EXPOSE")&&!listText.Contains("private-host"),"Channel list excludes OAuth tokens and connection secrets");
  using var view=await client.GetAsync(url);var text=await view.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(text);var revision=doc.RootElement.GetProperty("revision").GetString()!;
  check(view.IsSuccessStatusCode&&doc.RootElement.GetProperty("listings").GetArrayLength()==3&&!text.Contains("NEVER EXPOSE")&&!text.Contains("private-host"),"Native mapping loads listing identities without leaking channel settings");
  check(doc.RootElement.GetProperty("properties").GetArrayLength()==1&&doc.RootElement.GetProperty("rooms").EnumerateArray().All(r=>r.GetProperty("propertyId").GetString()==UnifiedTransport.ExistingProperty),"Only accommodation inventory attached to the selected channel is offered");
  var input=new ChannelMappingInput("111111",UnifiedTransport.MappingRate,revision,true);var before=remote.MappingWrites;
  check((await post(url+"/map",input,null)).StatusCode==HttpStatusCode.BadRequest&&remote.MappingWrites==before,"Mapping without CSRF never reaches Channex");
  check((await post(url+"/map",input with{Confirm=false},csrf)).StatusCode==HttpStatusCode.BadRequest,"Mapping requires explicit destination confirmation");
  check((await post(url+"/map",input with{ListingId="999999"},csrf)).StatusCode==HttpStatusCode.BadRequest&&remote.MappingWrites==before,"A forged listing cannot be attached to a connected account");
  check((await post(url+"/map",input with{ExpectedRevision="old"},csrf)).StatusCode==HttpStatusCode.Conflict,"Stale channel snapshots cannot be saved");
  check((await post(url+"/map",input with{ListingId="222222"},csrf)).StatusCode==HttpStatusCode.Conflict,"Existing mapping cannot be silently replaced");
  using var saved=await post(url+"/map",input,csrf);check(saved.IsSuccessStatusCode&&remote.MappingWrites==before+1,"Explicit save sends one dedicated Airbnb mapping request");
  check((await post(url+"/map",input,csrf)).IsSuccessStatusCode&&remote.MappingWrites==before+1,"Retry of confirmed mapping does not resend or duplicate the association");
  using var refreshed=await client.GetAsync(url);using var current=JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());check(current.RootElement.GetProperty("listings").EnumerateArray().Any(l=>l.GetProperty("mappingId").ValueKind==JsonValueKind.String&&l.GetProperty("mappingId").GetString()==UnifiedTransport.ExistingMapping.ToString()),"Saving a listing preserves every previous channel mapping");
  remote.LoseMappingResponse=true;var uncertain=input with{ListingId="333333",ExpectedRevision=current.RootElement.GetProperty("revision").GetString()!};
  check((await post(url+"/map",uncertain,csrf)).StatusCode==HttpStatusCode.ServiceUnavailable,"A lost mapping response is marked uncertain instead of falsely reporting success");var writes=remote.MappingWrites;
  check((await post(url+"/map",uncertain,csrf)).StatusCode==HttpStatusCode.Conflict&&remote.MappingWrites==writes,"An uncertain delivery cannot be submitted twice");remote.LoseMappingResponse=false;
  auth.CurrentRole="funcionario";check((await client.GetAsync(url)).IsSuccessStatusCode&&(await post(url+"/map",input,csrf)).IsSuccessStatusCode,"Approved staff can access and maintain channel mappings");
  auth.CurrentRole="proprietario";check((await client.GetAsync("/mapeamento")).StatusCode==HttpStatusCode.Forbidden&&(await client.GetAsync("/api/channel-mapping")).StatusCode==HttpStatusCode.Forbidden,"Owner accounts cannot access other Airbnb listings through mapping");
  auth.CurrentRole="admin";check((await client.GetAsync(url)).IsSuccessStatusCode,"Approved Admin can manage connected channels");auth.CurrentRole="master";
 }
}
