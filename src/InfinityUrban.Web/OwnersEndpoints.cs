using Microsoft.AspNetCore.Authentication;
namespace InfinityUrban.Web;
public static class OwnersEndpoints
{
    public static void MapOwners(this WebApplication app)
    {
        app.MapGet("/proprietarios",async(HttpContext context)=>{await IntegrationAccess.Operator(context);return Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","owners.html"),"text/html");}).RequireAuthorization();
        var api=app.MapGroup("/api/owners").RequireAuthorization().AddEndpointFilter(async(context,next)=>{await IntegrationAccess.Operator(context.HttpContext,context.HttpContext.Request.Method!="GET");return await next(context);});
        api.MapGet("",async(HttpContext context,OwnersService service)=>await service.List(await Token(context),context.RequestAborted));
        api.MapPost("",async(OwnerInput input,HttpContext context,OwnersService service)=>await service.Save(await Token(context),input,context.RequestAborted));
        api.MapPost("/{id:guid}/delete",async(Guid id,OwnerDelete input,HttpContext context,OwnersService service)=>{await service.Delete(await Token(context),id,input.ExpectedRevision,context.RequestAborted);return Results.Ok(new{deleted=true});});
        api.MapPost("/rooms/{id:guid}",async(Guid id,RoomOwnerInput input,HttpContext context,OwnersService service)=>Results.Ok(new{assignment=await service.Link(await Token(context),id,input,context.RequestAborted),confirmed=true}));
    }
    private static async Task<string> Token(HttpContext context)=>await context.GetTokenAsync("access_token")??throw new PortalError(401,"Sua sessão expirou. Entre novamente.");
}
