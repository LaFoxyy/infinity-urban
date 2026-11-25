using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stayline;

var builder = WebApplication.CreateBuilder(args);
// Local fallback for machines where the user-profile Secret Manager store is unavailable.
// This directory is ignored by Git, excluded from source archives, and outside wwwroot.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddJsonFile(".local/production-secrets.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables().AddCommandLine(args);
var directory = builder.Configuration["DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", builder.Configuration["Channex:Environment"] ?? "production");
Directory.CreateDirectory(directory);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddDbContextFactory<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(directory, "stayline.db")};Default Timeout=30"));
builder.Services.AddSingleton<MutationLock>();
builder.Services.AddSingleton<PmsService>();
builder.Services.AddSingleton<StagingImportService>();
builder.Services.AddSingleton<StagingProvisionService>();
builder.Services.AddChannex(builder.Configuration);
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();
    await AppSchema.EnsureAsync(db);
    await ChannexEnvironmentGuard.EnsureAsync(db, app.Services.GetRequiredService<IOptions<ChannexOptions>>().Value);
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try
    {
        if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !context.Request.Path.Equals("/api/channex/webhook"))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)))
                throw new AppError("Cross-origin writes are not allowed.", 403);
            if (context.Request.ContentLength is > 0 && !context.Request.HasJsonContentType())
                throw new AppError("Send application/json.", 415);
        }
        await next(context);
    }
    catch (AppError e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (BadHttpRequestException e) { context.Response.StatusCode = e.StatusCode; await context.Response.WriteAsJsonAsync(new { error = "Invalid request. Check the fields and date formats." }); }
    catch (HttpRequestException) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = "Channex could not be reached. Check the staging key and try again." }); }
    catch (DbUpdateException e)
    {
        app.Logger.LogWarning(e, "Database update rejected");
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "The data conflicts with an existing record or mapping. Reload and check your changes." });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception e)
    {
        app.Logger.LogError(e, "Request failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "The operation could not be completed. Check the server log." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapPmsEndpoints();
app.MapChannexEndpoints();
app.MapStagingImportEndpoints();
app.MapStagingProvisionEndpoints();
app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "DELETE", "PATCH"], () => Results.NotFound(new { error = "API route not found." }));
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program;
