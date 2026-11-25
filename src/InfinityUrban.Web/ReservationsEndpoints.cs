namespace InfinityUrban.Web;
public static class ReservationsEndpoints
{
    public static void MapReservations(this WebApplication app)
    {
        app.MapGet("/reservas",async(HttpContext context)=>{await IntegrationAccess.Operator(context);return Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","reservations.html"),"text/html");}).RequireAuthorization();
        app.MapGet("/calendario",()=>Results.Redirect("/reservas#calendario")).RequireAuthorization();
        var api=app.MapGroup("/api/reservations-dashboard").RequireAuthorization().AddEndpointFilter(async(context,next)=>{await IntegrationAccess.Operator(context.HttpContext);return await next(context);});
        api.MapGet("",(DateOnly? from,int? days,ReservationsService service,HttpContext context)=>service.List(from??DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"America/Sao_Paulo").DateTime),days??21,context.RequestAborted));
        api.MapPost("/refresh",async(ReservationsService service,OperationalLinks links,HttpContext context)=>
        {
            var linked=await links.Reconcile(null,context.RequestAborted);var feed=await service.Refresh(context.RequestAborted);
            return Results.Ok(new{linking=linked,feed});
        });
        api.MapPost("/manual",async(ManualReservationInput input,ReservationsService service,HttpContext context)=>await service.Create(input,(await IntegrationAccess.Operator(context)).Id,context.RequestAborted));
    }
}
