using System.Text;
namespace InfinityUrban.Web;

public static class AnalyticsEndpoints
{
    public static void MapAnalytics(this WebApplication app)
    {
        foreach(var path in new[]{"/dashboard","/relatorios"})app.MapGet(path,async(HttpContext context)=>
        {await IntegrationAccess.PortfolioReader(context);return Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","analytics.html"),"text/html");}).RequireAuthorization();
        var api=app.MapGroup("/api/analytics").RequireAuthorization();
        static (DateOnly From,DateOnly To) Period(DateOnly? from,DateOnly? to)
        {
            var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"America/Sao_Paulo").DateTime);
            return(from??new DateOnly(today.Year,today.Month,1),to??new DateOnly(today.Year,today.Month,DateTime.DaysInMonth(today.Year,today.Month)));
        }
        api.MapGet("",async(HttpContext context,AnalyticsService service,DateOnly? from,DateOnly? to,Guid? roomId,string? currency)=>
        {var period=Period(from,to);return await service.Read(context,period.From,period.To,roomId,currency??"BRL");});
        api.MapGet("/export.csv",async(HttpContext context,AnalyticsService service,DateOnly? from,DateOnly? to,Guid? roomId,string? currency)=>
        {var period=Period(from,to);var report=await service.Read(context,period.From,period.To,roomId,currency??"BRL");return Results.File(Encoding.UTF8.GetBytes(AnalyticsService.Csv(report,context.Request.Cookies["urban-language"]??"pt-BR")),"text/csv; charset=utf-8",$"infinity-urban-{period.From:yyyy-MM-dd}-{period.To:yyyy-MM-dd}.csv");});
    }
}
