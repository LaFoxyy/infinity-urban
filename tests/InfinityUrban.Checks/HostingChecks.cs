using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using InfinityUrban.Web;
static class HostingChecks
{
 public static async Task Run(HttpClient client,Action<bool,string> check)
 {
  using var health=await client.GetAsync("/health/live");var text=await health.Content.ReadAsStringAsync();
  check(health.IsSuccessStatusCode&&text.Contains("up")&&!text.Contains("Supabase"),"Container liveness is public and exposes no application data");
  var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Hosting:ProxyNetwork","10.0.1.0/24"}}).Build();
  var options=ProductionHosting.ProxyOptions(configuration);
  check(options.KnownIPNetworks.Count==1&&options.KnownProxies.Count==0&&options.ForwardLimit==1,"Only the observed Traefik overlay network is trusted, for one proxy hop");
  async Task<DefaultHttpContext> Apply(string peer){var context=new DefaultHttpContext();context.Connection.RemoteIpAddress=IPAddress.Parse(peer);context.Request.Scheme="http";context.Request.Host=new("infinityboard.urbaninfinity.com.br");context.Request.Headers["X-Forwarded-Proto"]="https";context.Request.Headers["X-Forwarded-For"]="198.51.100.25";context.Request.Headers["X-Forwarded-Host"]="other-site.invalid";var middleware=new ForwardedHeadersMiddleware(_=>Task.CompletedTask,NullLoggerFactory.Instance,Options.Create(options));await middleware.Invoke(context);return context;}
  var trusted=await Apply("10.0.1.15");check(trusted.Request.IsHttps&&trusted.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("198.51.100.25")),"Traefik HTTPS and client IP are forwarded before authentication");
  check((await Apply("::ffff:10.0.1.15")).Request.IsHttps,"Mapped IPv4 connections from the trusted overlay also preserve HTTPS");
  check(trusted.Request.Host.Host=="infinityboard.urbaninfinity.com.br","Forwarded host headers cannot redirect authentication to another site");
  var untrusted=await Apply("198.51.100.40");check(!untrusted.Request.IsHttps&&untrusted.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("198.51.100.40")),"Unknown sources cannot spoof HTTPS or a client IP");
  check(ProductionHosting.ProxyOptions(new ConfigurationBuilder().Build()).ForwardedHeaders==ForwardedHeaders.None,"Local development does not trust arbitrary forwarded headers");
  foreach(var invalid in new[]{"0.0.0.0/0","::/0","not-a-network"}){var rejected=false;try{ProductionHosting.ProxyOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Hosting:ProxyNetwork",invalid}}).Build());}catch(InvalidOperationException){rejected=true;}check(rejected,"Invalid or unrestricted proxy configuration is rejected: "+invalid);}
 }
}
