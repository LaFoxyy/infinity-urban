namespace InfinityUrban.Web;
public static class ChannelMappingEndpoints
{
    public static void MapChannelMapping(this WebApplication app)
    {
        app.MapGet("/mapeamento",async(HttpContext context)=>{await IntegrationAccess.Operator(context);return Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","channel-mapping.html"),"text/html");}).RequireAuthorization();
        var api=app.MapGroup("/api/channel-mapping").RequireAuthorization().AddEndpointFilter(async(context,next)=>{await IntegrationAccess.Operator(context.HttpContext);return await next(context);});
        api.MapGet("",(ChannelMappingService service,HttpContext context)=>service.List(context.RequestAborted));
        api.MapGet("/{id:guid}",(Guid id,ChannelMappingService service,HttpContext context)=>service.View(id,context.RequestAborted));
        api.MapGet("/{id:guid}/general",(Guid id,ChannelGeneralSettings settings,HttpContext context)=>settings.View(id,context.RequestAborted));
        api.MapPost("/{id:guid}/general",(Guid id,ChannelGeneralInput input,ChannelGeneralSettings settings,HttpContext context)=>settings.Save(id,input,context.RequestAborted));
        api.MapPost("/{id:guid}/map",async(Guid id,ChannelMappingInput input,ChannelMappingService service,OperationalLinks links,HttpContext context)=>
        {
            var result=await service.Map(id,input,(await IntegrationAccess.Operator(context)).Id,context.RequestAborted);
            try{await links.Reconcile(null,context.RequestAborted);}catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidOperationException or PortalError){context.RequestServices.GetRequiredService<ILogger<OperationalLinks>>().LogWarning("Vínculo automático adiado após mapeamento ({Type}).",e.GetType().Name);}
            return result;
        });
    }
}
