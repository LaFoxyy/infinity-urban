using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;

// Composition only: services, schema, environment guard, API handlers and workers are the approved implementations.
public static class UnifiedRuntime
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile(".local/channex-secrets.json",optional:true);
        if(builder.Configuration["Hosting:SecretsFile"] is {Length:>0} secretsFile)
            builder.Configuration.AddJsonFile(secretsFile,optional:false,reloadOnChange:false);
        builder.Configuration.AddEnvironmentVariables();
        builder.Services.AddDbContextFactory<AppDb>(o=>
        {
            var directory=builder.Configuration["Stayline:DataDirectory"]??Path.Combine(builder.Environment.ContentRootPath,".local","runtime",builder.Configuration["Channex:Environment"]??"production");
            Directory.CreateDirectory(directory);
            o.UseSqlite($"Data Source={Path.Combine(directory,"stayline.db")};Default Timeout=30");
        });
        builder.Services.AddSingleton<MutationLock>();
        builder.Services.AddSingleton<PmsService>();
        builder.Services.AddSingleton<StagingImportService>();
        builder.Services.AddSingleton<StagingProvisionService>();
        builder.Services.AddHostedService<InitializeUnifiedDatabase>();
        // This original registration supplies precisely the original transport and both workers.
        builder.Services.AddChannex(builder.Configuration);
        builder.Services.AddHttpClient<ChannexContentClient>(http=>http.Timeout=TimeSpan.FromSeconds(45))
            .RedactLoggedHeaders(["user-api-key"]).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false});
        builder.Services.AddSingleton<CatalogFiles>();
        builder.Services.AddScoped<RemoteAccommodationService>();
    }
    public static void Map(WebApplication app)
    {
        // A protected route group wraps existing handlers without changing their payloads or implementation.
        var api=app.MapGroup("").RequireAuthorization().AddEndpointFilter(async(context,next)=>
        {
            await IntegrationAccess.Operator(context.HttpContext,!HttpMethods.IsGet(context.HttpContext.Request.Method));
            return await next(context);
        });
        // PmsEndpoints accepts WebApplication, so apply the same authorization in the middleware below.
        app.MapPmsEndpoints();
        api.MapStagingImportEndpoints();api.MapStagingProvisionEndpoints();
        // Remote inventory is unchanged; the webhook keeps its original secret-based authentication.
        app.MapChannexEndpoints();
        app.MapGet("/operacoes",async(HttpContext context)=>{await IntegrationAccess.Operator(context);return Results.File(Path.Combine(app.Environment.ContentRootPath,"Pages","operations.html"),"text/html");}).RequireAuthorization();
        app.MapGet("/operations-original.js",async(HttpContext context)=>{await IntegrationAccess.Operator(context);return Results.File(Path.Combine(IntegrationAccess.ChannexSourceRoot(app.Configuration,app.Environment.ContentRootPath),"Assets","operations.js"),"text/javascript");}).RequireAuthorization();
        app.MapGet("/stayline/{view}",(string view)=>Results.Redirect(view=="properties"?"/acomodacoes":"/operacoes#"+Uri.EscapeDataString(view))).RequireAuthorization();
    }
}
public sealed class InitializeUnifiedDatabase(IDbContextFactory<AppDb> factory,IOptions<ChannexOptions> options):IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        await AppSchema.EnsureAsync(db);await ChannexEnvironmentGuard.EnsureAsync(db,options.Value,ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;",ct);
    }
    public Task StopAsync(CancellationToken ct)=>Task.CompletedTask;
}
