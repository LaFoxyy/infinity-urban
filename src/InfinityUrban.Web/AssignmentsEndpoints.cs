using Microsoft.AspNetCore.Authentication;
namespace InfinityUrban.Web;
public static class AssignmentsEndpoints
{
    public static void MapAssignments(this WebApplication app)
    {
        app.MapGet("/designacoes",()=>Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","assignments.html"),"text/html")).RequireAuthorization("management");
        var api=app.MapGroup("/api/assignments").RequireAuthorization("management");
        api.MapGet("",async(HttpContext context,AssignmentsService service)=>await service.List(await Token(context),context.RequestAborted));
        api.MapPost("",async(AssignmentInput input,HttpContext context,AssignmentsService service)=>await service.Add(await Token(context),input,context.RequestAborted));
        api.MapPost("/{id:guid}/remove",async(Guid id,HttpContext context,AssignmentsService service)=>{await service.Remove(await Token(context),id,context.RequestAborted);return Results.Ok(new{removed=true});});
    }
    private static async Task<string> Token(HttpContext context)=>await context.GetTokenAsync("access_token")??throw new PortalError(401,"Sua sessão expirou. Entre novamente.");
}
