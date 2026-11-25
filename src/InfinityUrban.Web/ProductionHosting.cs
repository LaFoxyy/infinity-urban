using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
namespace InfinityUrban.Web;

// Hosting adapter only: no certified API, worker, payload or database schema changes.
public static class ProductionHosting
{
    public static ForwardedHeadersOptions ProxyOptions(IConfiguration config)
    {
        var options=new ForwardedHeadersOptions();
        if(string.IsNullOrWhiteSpace(config["Hosting:ProxyNetwork"]))return options;
        if(!System.Net.IPNetwork.TryParse(config["Hosting:ProxyNetwork"],out var network)||network.PrefixLength==0)
            throw new InvalidOperationException("Hosting:ProxyNetwork must be the trusted proxy network, not an unrestricted range.");
        options.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;
        options.ForwardLimit=1;
        options.KnownProxies.Clear();options.KnownIPNetworks.Clear();options.KnownIPNetworks.Add(network);
        return options;
    }
    public static void UseProductionHosting(this WebApplication app)
    {
        app.UseForwardedHeaders(ProxyOptions(app.Configuration));
        if(!app.Environment.IsDevelopment())app.UseWhen(context=>context.Request.Path!="/health/live",branch=>
        {
            branch.UseHsts();branch.UseHttpsRedirection();
        });
        // No external service calls or application data. Also works for the local container probe over HTTP.
        app.MapGet("/health/live",()=>Results.Json(new{status="up",version=app.Configuration["Hosting:Revision"]??"local"})).AllowAnonymous();
    }
}
