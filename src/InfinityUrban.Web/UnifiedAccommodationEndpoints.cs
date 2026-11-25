using Stayline;
using Microsoft.Extensions.Options;
namespace InfinityUrban.Web;
public static class UnifiedAccommodationEndpoints
{
    public static void MapUnifiedAccommodations(this WebApplication app)
    {
        app.MapGet("/acomodacoes",async(HttpContext context)=>{await IntegrationAccess.PortfolioReader(context);return Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","accommodations.html"),"text/html");}).RequireAuthorization();
        var api=app.MapGroup("/api/accommodations").RequireAuthorization().AddEndpointFilter(async(context,next)=>{if(context.HttpContext.Request.Method=="GET")await IntegrationAccess.PortfolioReader(context.HttpContext);else await IntegrationAccess.Operator(context.HttpContext,true);return await next(context);});
        api.MapGet("",(HttpContext context,PortfolioAccess access)=>access.List(context));
        api.MapGet("/options",async(HttpContext context,RemoteAccommodationService service)=>{await IntegrationAccess.Operator(context);return await service.ExpenseCatalog(context.RequestAborted);});
        api.MapGet("/rooms/{id:guid}",(Guid id,HttpContext context,PortfolioAccess access)=>access.Room(context,id));
        api.MapGet("/properties/{id:guid}/edit",async(Guid id,HttpContext context,BuildingEditor editor)=>await editor.Read(id,(await IntegrationAccess.Operator(context,true)).Id,context.RequestAborted));
        api.MapPost("/properties/{id:guid}/edit",async(Guid id,BuildingEditInput input,HttpContext context,BuildingEditor editor)=>await editor.Save(id,input,(await IntegrationAccess.Operator(context,true)).Id,context.RequestAborted));
        api.MapGet("/rooms/{id:guid}/edit",async(Guid id,HttpContext context,RoomEditor editor)=>await editor.Read(id,(await IntegrationAccess.Operator(context,true)).Id,context.RequestAborted));
        api.MapPost("/rooms/{id:guid}/edit",async(Guid id,RoomEditInput input,HttpContext context,RoomEditor editor)=>await editor.Save(id,input,(await IntegrationAccess.Operator(context,true)).Id,context.RequestAborted));
        foreach(var kind in new[]{"properties","room_types"}){
            var resource=kind;
            api.MapPost("/"+resource+"/{id:guid}/delete",async(Guid id,AccommodationDeleteInput input,HttpContext context,AccommodationDeletion deletion)=>{
                var member=await IntegrationAccess.Operator(context,true);
                if(!member.Roles.Any(r=>r.Role=="admin"))throw new PortalError(403,"Somente administradores podem excluir prédios e apartamentos.");
                return await deletion.Delete(resource,id,input,context.RequestAborted);
            });
        }
        api.MapPost("/properties",async(BuildingInput input,HttpContext context,RemoteAccommodationService service)=>await service.CreateBuilding(input,(await IntegrationAccess.Operator(context,true)).Id,context.RequestAborted));
        api.MapPost("/rooms",async(RoomInput input,HttpContext context,RemoteAccommodationService service,OperationalLinks links)=>
        {
            var result=await service.CreateRoom(input,(await IntegrationAccess.Operator(context,true)).Id,context.RequestAborted);
            try{await links.Reconcile(input.PropertyId,context.RequestAborted);}catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidOperationException or PortalError){context.RequestServices.GetRequiredService<ILogger<OperationalLinks>>().LogWarning("Vínculo automático adiado após cadastro ({Type}).",e.GetType().Name);}
            return result;
        });
        api.MapGet("/property-facilities",async(HttpContext context,ChannexClient client,CancellationToken ct)=>{await IntegrationAccess.Operator(context);return await client.FetchCollectionAsync("property_facilities/options",ct);});
        api.MapGet("/facilities",async(HttpContext context,ChannexClient client,CancellationToken ct)=>{await IntegrationAccess.Operator(context);return await client.FetchCollectionAsync("room_facilities/options",ct);});
        api.MapPost("/photos",async(HttpContext context,ChannexContentClient media)=>{if(!context.Request.HasFormContentType)throw new PortalError(415,"Envie uma imagem.");var form=await context.Request.ReadFormAsync(context.RequestAborted);return await media.Upload(form.Files.GetFile("photo")??throw new PortalError(400,"Selecione uma imagem."),(await IntegrationAccess.Operator(context,true)).Id,context.RequestAborted);});
    }
}
