using System.Net;
using Microsoft.Extensions.Options;
using Stayline;
using InfinityUrban.Web;
static class PerformanceChecks
{
 public static async Task Run(HttpClient client,FakeSupabase auth,Action<bool,string> check)
 {
  auth.CurrentRole="master";var before=auth.Paths.Count;
  check((await client.GetAsync("/api/auth/session")).IsSuccessStatusCode,"Optimized authentication still verifies the active Supabase session");
  var reads=auth.Paths.Skip(before).ToArray();
  check(reads.Count(p=>p.StartsWith("/rest/v1/profiles"))==1&&reads.Count(p=>p.StartsWith("/rest/v1/user_roles"))==1&&reads.Any(p=>p.StartsWith("/auth/v1/user")),"Every authenticated request checks identity, status and roles without duplicate profile reads");
  using var handler=new DelayedReads{InnerHandler=new UnifiedTransport()};using var http=new HttpClient(handler);
  var options=Options.Create(new ChannexOptions{Enabled=true,Environment="production",ApiKey="catalog-test-key"});
  var service=new RemoteAccommodationService(new ChannexClient(http,options),null!,null!,options);
  var catalog=System.Text.Json.JsonSerializer.SerializeToElement(await service.List(default),System.Text.Json.JsonSerializerOptions.Web);
  check(handler.Maximum>1&&handler.Maximum<=4,"Independent Channex catalog reads overlap with at most four remote requests at once");
  check(catalog.GetProperty("properties").GetArrayLength()==2&&catalog.GetProperty("rooms").GetArrayLength()==2,"Concurrent catalog preserves all hydrated buildings and apartments");
  var first=handler.Total;await service.List(default);
  check(handler.Total==first*2,"Refreshing reads authoritative Channex data again without a cross-request data cache");
  var start=handler.Total;await service.ExpenseCatalog(default);
  check(handler.Total-start==2,"Selection lists request only the building and apartment collections");
  check((await client.GetAsync("/api/accommodations/options")).IsSuccessStatusCode,"Internal accounts can use the lightweight apartment selector");
  auth.CurrentRole="proprietario";
  check((await client.GetAsync("/api/accommodations/options")).StatusCode==HttpStatusCode.Forbidden,"Lightweight selectors cannot expose other apartments to owner accounts");auth.CurrentRole="master";
 }
 private sealed class DelayedReads:DelegatingHandler
 {
  private int active,total,maximum;public int Total=>total;public int Maximum=>maximum;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   Interlocked.Increment(ref total);var count=Interlocked.Increment(ref active);
   int old;do{old=maximum;if(old>=count)break;}while(Interlocked.CompareExchange(ref maximum,count,old)!=old);
   try{await Task.Delay(20,ct);return await base.SendAsync(request,ct);}finally{Interlocked.Decrement(ref active);}
  }
 }
}
