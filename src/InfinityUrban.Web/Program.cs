using System.Net.Mail;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using InfinityUrban.Web;

var app = PortalApplication.Build(args);
app.Run();

public static class PortalApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        UnifiedRuntime.Configure(builder);
        var protection = builder.Services.AddDataProtection().SetApplicationName("UrbanInfinity.Portal");
        protection.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".local",
            builder.Environment.IsDevelopment() ? "development-keys" : "data-protection")));
        if (builder.Environment.IsDevelopment()) protection.UseEphemeralDataProtectionProvider();
        else
        {
            if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        }
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
        builder.Services.AddHttpClient<SupabaseAuth>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .RedactLoggedHeaders(["apikey", "Authorization"]);
        builder.Services.AddScoped<ManagementService>();
        builder.Services.AddScoped<OwnersService>();
        builder.Services.AddScoped<ExpensesService>();
        builder.Services.AddScoped<ReservationsService>();
        builder.Services.AddScoped<OperationalLinks>();
        builder.Services.AddScoped<ChannelGeneralSettings>();
        builder.Services.AddScoped<BuildingEditor>();
        builder.Services.AddScoped<RoomEditor>();
        builder.Services.AddScoped<AccommodationDeletion>();
        builder.Services.AddHostedService<OperationalLinkWorker>();
        builder.Services.AddScoped<ChannelMappingService>();
        builder.Services.AddHttpClient<ChannexMappingWriter>(client=>client.Timeout=TimeSpan.FromSeconds(100)).RedactLoggedHeaders(["user-api-key"]);
        builder.Services.AddScoped<AssignmentsService>();
        builder.Services.AddScoped<PortfolioAccess>();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = "urban-infinity.csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "urban-infinity.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                if (!context.Request.Path.StartsWithSegments("/api")) context.Response.Redirect("/login");
                else context.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async context =>
            {
                var token = context.Properties.GetTokenValue("access_token");
                if (token is null) { context.RejectPrincipal(); return; }
                try
                {
                    var auth = context.HttpContext.RequestServices.GetRequiredService<SupabaseAuth>();
                    var userId=context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    if(!Guid.TryParse(userId,out _)){context.RejectPrincipal();return;}
                    var userTask=auth.GetUser(token,context.HttpContext.RequestAborted);
                    var memberTask=context.HttpContext.RequestServices.GetRequiredService<ManagementService>().CurrentMember(token,userId,context.HttpContext.RequestAborted);
                    await Task.WhenAll(userTask,memberTask);var user=await userTask;var member=await memberTask;
                    if(user is null||user.Id!=userId||member?.Status!="approved")context.RejectPrincipal();
                    else context.HttpContext.Items["PortalMember"]=member;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or PortalError)
                {
                    // Fail closed if the identity service is unavailable.
                    context.RejectPrincipal();
                }
            };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("management", policy => policy.RequireAuthenticatedUser()
            .RequireAssertion(context => context.Resource is HttpContext http && http.Items["PortalMember"] is PortalMember { CanManage: true })));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        configure?.Invoke(builder);
        var app = builder.Build();
        if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }
        app.Use(async (context, next) =>
        {
            var legacyDocument=false;
            context.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; style-src 'self'{(legacyDocument?" 'unsafe-inline'":"")}; img-src 'self' https: data: blob:; connect-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors {(legacyDocument?"'self'":"'none'")}; form-action 'self'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cache-Control"] = "no-store";
            var bodyLimit=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if(bodyLimit is {IsReadOnly:false})bodyLimit.MaxRequestBodySize=context.Request.Path=="/api/accommodations/photos"?11*1024*1024:
                context.Request.Path.StartsWithSegments("/api/auth")?16*1024:1024*1024;
            try { await next(); }
            catch (Stayline.AppError error)
            {
                context.Response.StatusCode=error.Status;await context.Response.WriteAsJsonAsync(new{error=error.Message});
            }
            catch (PortalError error)
            {
                context.Response.StatusCode = error.Status;
                await context.Response.WriteAsJsonAsync(new { error = error.Message });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (context.RequestAborted.IsCancellationRequested) return;
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { error = "Não foi possível conectar ao serviço solicitado. Tente novamente." });
            }
        });
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(async(context,next)=>
        {
            if(IntegrationAccess.IsLegacyApi(context.Request.Path,context.Request.Method))
            {
                if(context.User.Identity?.IsAuthenticated!=true){context.Response.StatusCode=401;return;}
                await IntegrationAccess.Operator(context,context.Request.Method!="GET");
            }
            await next();
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path!="/api/channex/webhook" && context.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH")
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Sessão do formulário expirada. Atualize a página." }); return; }
            }
            await next();
        });
        app.MapGet("/api/auth/form", (HttpContext context, IAntiforgery antiforgery, SupabaseAuth auth) =>
            Results.Json(new { csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken, configured = auth.IsConfigured }));
        app.MapPost("/api/auth/login", async (LoginRequest input, HttpContext context, SupabaseAuth auth) =>
        {
            if (!auth.IsConfigured) return Results.Json(new { error = "O login aguarda a configuração do Supabase." }, statusCode: 503);
            var email = input.Email?.Trim();
            if (string.IsNullOrEmpty(email) || email.Length > 254 || !MailAddress.TryCreate(email, out var address)
                || address.Address != email || string.IsNullOrEmpty(input.Password) || input.Password.Length > 4096)
                return Results.BadRequest(new { error = "Informe um email válido e sua senha." });
            var session = await auth.SignIn(email, input.Password, context.RequestAborted);
            if (session?.User is null) return Results.Json(new { error = "Email ou senha inválidos, ou email ainda não confirmado." }, statusCode: 401);
            var accountStatus = await auth.GetAccountStatus(session.AccessToken, session.User.Id, context.RequestAborted);
            if (accountStatus != "approved")
            {
                // No portal cookie is issued for pending, rejected or missing profiles.
                try { await auth.SignOut(session.AccessToken, context.RequestAborted); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                return Results.Json(new { error = accountStatus == "pending"
                    ? "Sua conta está pendente de aprovação de um administrador."
                    : "Sua conta não está autorizada a acessar o portal. Contate a administração." }, statusCode: 403);
            }
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, session.User.Id),
                new Claim(ClaimTypes.Email, session.User.Email ?? email)
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            var properties = new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(session.ExpiresIn, 3600)) };
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = session.AccessToken }]);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
            return Results.Ok(new { authenticated = true });
        }).RequireRateLimiting("login");
        app.MapGet("/api/auth/session", (HttpContext context) =>
        {
            var member = context.Items["PortalMember"] as PortalMember;
            return Results.Ok(new { email = context.User.FindFirstValue(ClaimTypes.Email), member, canManage = member?.CanManage == true });
        }).RequireAuthorization();
        app.MapPost("/api/auth/logout", async (HttpContext context, SupabaseAuth auth) =>
        {
            var token = await context.GetTokenAsync("access_token");
            var revoked = true;
            try { if (token is not null) await auth.SignOut(token, context.RequestAborted); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { revoked = false; }
            finally { await context.SignOutAsync(); }
            return Results.Ok(new { signedOut = true, revoked });
        });
        app.MapGet("/login", () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html"));
        app.MapGet("/", () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html"));
        app.MapManagement();
        app.MapOwners();
        app.MapExpenses();
        app.MapReservations();
        app.MapChannelMapping();
        app.MapAssignments();
        app.MapUnifiedAccommodations();
        UnifiedRuntime.Map(app);
        return app;
    }
}
