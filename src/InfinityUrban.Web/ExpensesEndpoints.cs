using Microsoft.AspNetCore.Authentication;
namespace InfinityUrban.Web;
public static class ExpensesEndpoints
{
    public static void MapExpenses(this WebApplication app)
    {
        app.MapGet("/despesas-recorrentes",async(HttpContext context)=>{await IntegrationAccess.Operator(context);return Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","expenses.html"),"text/html");}).RequireAuthorization();
        var api=app.MapGroup("/api/expenses").RequireAuthorization().AddEndpointFilter(async(context,next)=>{await IntegrationAccess.Operator(context.HttpContext,context.HttpContext.Request.Method!="GET");return await next(context);});
        api.MapGet("/catalog",async(HttpContext context,RemoteAccommodationService service)=>await service.ExpenseCatalog(context.RequestAborted));
        api.MapGet("",async(HttpContext context,ExpensesService service)=>await service.List(await Token(context),context.RequestAborted));
        api.MapPost("",async(ExpenseInput input,HttpContext context,ExpensesService service)=>await service.Save(await Token(context),input,context.RequestAborted));
        api.MapPost("/{id:guid}/delete",async(Guid id,ExpenseDelete input,HttpContext context,ExpensesService service)=>{await service.Delete(await Token(context),id,input.ExpectedRevision,context.RequestAborted);return Results.Ok(new{deleted=true});});
    }
    private static async Task<string> Token(HttpContext context)=>await context.GetTokenAsync("access_token")??throw new PortalError(401,"Sua sessão expirou. Entre novamente.");
}
