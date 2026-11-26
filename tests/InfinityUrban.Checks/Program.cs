using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using InfinityUrban.Web;

var root = Environment.GetEnvironmentVariable("URBAN_PORTAL_TEST_ROOT") ?? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "InfinityUrban.Web"));
if(args.Contains("--reservations-read")||args.Contains("--mapping-read")||args.Contains("--performance-read")||args.Contains("--expense-catalog")||args.Contains("--production-read")||args.Contains("--repair-pending"))
{
    var configuration=new ConfigurationBuilder().SetBasePath(root).AddJsonFile(".local/channex-secrets.json").Build();
    var options=new Stayline.ChannexOptions();configuration.GetSection("Channex").Bind(options);
    if(options.EnvironmentName!="production")throw new Exception("Production-only diagnostic.");
    using var transport=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(15)};
    var approved=new Stayline.ChannexClient(transport,Microsoft.Extensions.Options.Options.Create(options));
    if(args.Contains("--reservations-read"))
    {
        var database=Path.Combine(root,".local","runtime","production","stayline.db");
        var dbOptions=new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Stayline.AppDb>();
        Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(dbOptions,"Data Source="+database+";Mode=ReadOnly");
        var dbFactory=new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<Stayline.AppDb>(dbOptions.Options);
        var service=new ReservationsService(dbFactory,approved,null!,null!,null!);
        var now=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"America/Sao_Paulo").DateTime);
        var result=JsonSerializer.SerializeToElement(await service.List(now,21,default),JsonSerializerOptions.Web);
        var currentTitles=result.GetProperty("rooms").EnumerateArray().ToDictionary(r=>r.GetProperty("id").GetGuid(),r=>r.GetProperty("title").GetString());
        var mismatchedLabels=result.GetProperty("reservations").EnumerateArray().SelectMany(r=>r.GetProperty("stays").EnumerateArray()).Count(stay=>stay.GetProperty("roomId").ValueKind==JsonValueKind.String&&Guid.TryParse(stay.GetProperty("roomId").GetString(),out var roomId)&&currentTitles.TryGetValue(roomId,out var title)&&title!=stay.GetProperty("roomName").GetString());
        Console.WriteLine(JsonSerializer.Serialize(new{mismatchedLabels,properties=result.GetProperty("properties").GetArrayLength(),rooms=result.GetProperty("rooms").GetArrayLength(),readyRooms=result.GetProperty("rooms").EnumerateArray().Count(r=>r.GetProperty("ready").GetBoolean()),reservations=result.GetProperty("reservations").GetArrayLength(),pendingBookings=result.GetProperty("pendingBookings").GetInt32(),availabilityWarnings=result.GetProperty("availabilityWarnings").GetArrayLength(),readOnly=true}));return;
    }
    if(args.Contains("--mapping-read"))
    {
        var host=WebApplication.CreateBuilder(new WebApplicationOptions{ContentRootPath=root}).Environment;
        var files=new CatalogFiles(host,Microsoft.Extensions.Options.Options.Create(options),configuration);
        var service=new ChannelMappingService(approved,new ChannexMappingWriter(transport,Microsoft.Extensions.Options.Options.Create(options)),files);
        var list=JsonSerializer.SerializeToElement(await service.List(default),JsonSerializerOptions.Web);var listingCount=0;var mapped=0;var properties=0;var rooms=0;var rates=0;
        foreach(var channel in list.GetProperty("channels").EnumerateArray())
        {
            var view=JsonSerializer.SerializeToElement(await service.View(channel.GetProperty("id").GetGuid(),default),JsonSerializerOptions.Web);
            listingCount+=view.GetProperty("listings").GetArrayLength();mapped+=view.GetProperty("listings").EnumerateArray().Count(l=>l.GetProperty("mappingId").ValueKind==JsonValueKind.String);
            properties+=view.GetProperty("properties").GetArrayLength();rooms+=view.GetProperty("rooms").GetArrayLength();rates+=view.GetProperty("rates").GetArrayLength();
            if(view.GetRawText().Contains("tokens")||view.GetRawText().Contains("access_token"))throw new Exception("Sensitive connection settings in public view");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{channels=list.GetProperty("channels").GetArrayLength(),listings=listingCount,mapped,properties,rooms,rates,readOnly=true}));return;
    }
    if(args.Contains("--performance-read"))
    {
        var service=new RemoteAccommodationService(approved,null!,null!,Microsoft.Extensions.Options.Options.Create(options));
        var timer=System.Diagnostics.Stopwatch.StartNew();var perfCatalog=JsonSerializer.SerializeToElement(await service.List(default),JsonSerializerOptions.Web);var full=timer.ElapsedMilliseconds;
        timer.Restart();await service.ExpenseCatalog(default);var picker=timer.ElapsedMilliseconds;
        Console.WriteLine(JsonSerializer.Serialize(new{catalogMilliseconds=full,pickerMilliseconds=picker,buildings=perfCatalog.GetProperty("properties").GetArrayLength(),apartments=perfCatalog.GetProperty("rooms").GetArrayLength(),readOnly=true}));return;
    }
    if(args.Contains("--expense-catalog")){var service=new RemoteAccommodationService(approved,null!,null!,Microsoft.Extensions.Options.Options.Create(options));var result=JsonSerializer.SerializeToElement(await service.ExpenseCatalog(default),JsonSerializerOptions.Web);Console.WriteLine($"Production expense catalog: buildings={result.GetProperty("properties").GetArrayLength()}; apartments={result.GetProperty("rooms").GetArrayLength()}; original client; GET only.");return;}
    if(args.Contains("--repair-pending")){await ProductionRepair.Run(root,approved,options);return;}
    try{var properties=await approved.FetchCollectionAsync("properties");var rooms=await approved.FetchCollectionAsync("room_types");var facilities=await approved.FetchCollectionAsync("room_facilities/options");Console.WriteLine($"Production read OK via original .NET client; properties={properties.Count}; rooms={rooms.Count}; room facilities={facilities.Count}; no writes or workers.");
        var catalogService=new RemoteAccommodationService(approved,null!,null!,Microsoft.Extensions.Options.Options.Create(options));var catalogData=JsonSerializer.SerializeToElement(await catalogService.List(default),JsonSerializerOptions.Web);Console.WriteLine($"Actual site catalog: rooms with photos={catalogData.GetProperty("rooms").EnumerateArray().Count(r=>r.GetProperty("photos").GetArrayLength()>0)}; total photos={catalogData.GetProperty("rooms").EnumerateArray().Sum(r=>r.GetProperty("photos").GetArrayLength())}");
        foreach(var roomItem in rooms){var room=(await approved.ReadStagingJsonAsync("room_types/"+RemoteAccommodationService.Text(roomItem,"id"))).GetProperty("data");var photos=RemoteAccommodationService.Photos(RemoteAccommodationService.Attributes(room));Console.WriteLine($"Room {RemoteAccommodationService.Text(room,"id")}: photo count={photos.Length}; distinct URLs={photos.Select(p=>RemoteAccommodationService.Text(p,"url")).Distinct().Count()}; owner IDs match={photos.All(p=>RemoteAccommodationService.Text(p,"room_type_id")==RemoteAccommodationService.Text(room,"id"))}");}}
    catch(Exception ex){Console.WriteLine($"Production read failed: {ex.GetType().Name}: {ex.Message}; inner={ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");Environment.ExitCode=1;}
    return;
}
var fake = new FakeSupabase();
var catalog = new UnifiedTransport();
if(args.Contains("--preview"))catalog.SeedMappingRates();
await using var app = PortalApplication.Build(["--contentRoot", root, "--environment", "Development"], builder =>
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Supabase:Url"] = "https://test.supabase.co",
        ["Supabase:PublicKey"] = "sb_publishable_local_test_only",
        ["Channex:Enabled"]="true",["Channex:Environment"]="production",["Channex:ApiKey"]="catalog-test-key",
        ["Stayline:DataDirectory"]=Path.GetFullPath(Path.Combine(root,".local","checks","runtime",Guid.NewGuid().ToString("N"))),
        ["Catalog:DataDirectory"]=Path.GetFullPath(Path.Combine(root,".local","checks","catalog",Guid.NewGuid().ToString("N")))
    });
    builder.WebHost.UseUrls(args.Contains("--preview") ? "http://localhost:5190" : "http://127.0.0.1:0");
    builder.Services.AddHttpClient<SupabaseAuth>().ConfigurePrimaryHttpMessageHandler(() => fake);
    builder.Services.AddHttpClient<Stayline.ChannexClient>().ConfigurePrimaryHttpMessageHandler(()=>catalog);
    builder.Services.AddHttpClient<ChannexMappingWriter>().ConfigurePrimaryHttpMessageHandler(()=>catalog);
    builder.Services.AddHttpClient<ChannexContentClient>().ConfigurePrimaryHttpMessageHandler(()=>catalog);
    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IHostedService>(builder.Services);
    builder.Services.AddHostedService<InitializeUnifiedDatabase>();
});
await app.StartAsync();
if (args.Contains("--preview"))
{
    Console.WriteLine("LOCAL SIMULATED PREVIEW. Login: demo@example.com / valid-test-password. No production requests.");
    await app.WaitForShutdownAsync();
    return;
}
using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
using var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()) };
int count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
await HostingChecks.Run(client,Check);
async Task<string> FormToken()
{
    using var response = await client.GetAsync("/api/auth/form");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetProperty("csrfToken").GetString()!;
}
async Task<HttpResponseMessage> Post(string path, object data, string? csrf)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(data) };
    if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
    return await client.SendAsync(request);
}

Check((await client.GetAsync("/api/auth/session")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous visitors cannot obtain a session");
Check((await client.GetAsync("/api/owners")).StatusCode==HttpStatusCode.Unauthorized,"Anonymous visitors cannot list owner contacts");
Check((await client.GetAsync("/api/management/users")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous visitors cannot list accounts");
Check((await client.GetAsync("/gestao")).StatusCode == HttpStatusCode.Redirect, "Anonymous management navigation returns to login");
Check((await Post("/api/auth/login", new { email = "test@example.com", password = "password" }, null)).StatusCode == HttpStatusCode.BadRequest && fake.Calls == 0, "Missing CSRF token cannot reach Supabase");
var token = await FormToken();
Check((await Post("/api/auth/login", new { email = "test@example.com", password = "invalid" }, token)).StatusCode == HttpStatusCode.Unauthorized, "Invalid password creates no session");
using var login = await Post("/api/auth/login", new { email = "test@example.com", password = "valid-test-password" }, token);
Check(login.IsSuccessStatusCode, "Valid Supabase credentials establish a session");
var body = await login.Content.ReadAsStringAsync();
Check(!body.Contains("access_token") && !body.Contains(FakeSupabase.AccessToken), "Login response exposes no access or refresh token");
var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("urban-infinity.session="));
Check(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase) && !cookie.Contains(FakeSupabase.AccessToken), "Session cookie is encrypted, HttpOnly and SameSite strict");
Check((await client.GetAsync("/api/auth/session")).IsSuccessStatusCode && fake.UserCalls >= 2, "Identity is revalidated with Supabase for protected requests");
await ManagementChecks.Run(client, handler, fake, Check, FormToken, Post);
await UnifiedChecks.Run(client,fake,catalog,app.Services,Check,FormToken,Post);
await OwnersChecks.Run(client,fake,catalog,Check,FormToken,Post);
await AssignmentsChecks.Run(client,fake,catalog,Check,FormToken,Post);
await ExpensesChecks.Run(client,fake,catalog,Check,FormToken,Post);
await PerformanceChecks.Run(client,fake,Check);
await ChannelMappingChecks.Run(client,fake,catalog,Check,FormToken,Post);
await ReservationsChecks.Run(app.Services,client,fake,catalog,Check,FormToken,Post);
await AnalyticsChecks.Run(app.Services,client,fake,catalog,Check);
await OperationalLinksChecks.Run(app.Services,catalog,Check);
await BuildingEditorChecks.Run(client,fake,catalog,Check,FormToken,Post);
await CardActionsChecks.Run(client,fake,catalog,app.Services,Check,FormToken,Post);
using var logout = await Post("/api/auth/logout", new { }, await FormToken());
Check(logout.IsSuccessStatusCode && fake.Revoked, "Authenticated CSRF token permits logout and revokes remote session");
Check((await client.GetAsync("/api/auth/session")).StatusCode == HttpStatusCode.Unauthorized, "Logout removes access");

token = await FormToken();
await Post("/api/auth/login", new { email = "test@example.com", password = "valid-test-password" }, token);
fake.RejectIdentity = true;
Check((await client.GetAsync("/api/auth/session")).StatusCode == HttpStatusCode.Unauthorized, "Rejected Supabase identity cannot keep access through a local cookie");
fake.RejectIdentity = false;
fake.Unavailable = true;
Check((await client.GetAsync("/api/auth/session")).StatusCode == HttpStatusCode.Unauthorized, "Unavailable identity service fails closed");
fake.Unavailable = false;

foreach (Cookie existing in handler.CookieContainer.GetAllCookies()) existing.Expired = true;
fake.AccountStatus = "pending";
token = await FormToken();
Check((await Post("/api/auth/login", new { email = "test@example.com", password = "valid-test-password" }, token)).StatusCode == HttpStatusCode.Forbidden, "Pending accounts cannot establish a portal session");
fake.AccountStatus = "rejected";
Check((await Post("/api/auth/login", new { email = "test@example.com", password = "valid-test-password" }, token)).StatusCode == HttpStatusCode.Forbidden, "Rejected accounts cannot establish a portal session");
fake.AccountStatus = "approved";

for (var i = 0; i < 8; i++)
{
    using var result = await Post("/api/auth/login", new { email = "test@example.com", password = "invalid" }, await FormToken());
    if (result.StatusCode == HttpStatusCode.TooManyRequests) { Check(true, "Repeated login attempts are rate limited"); break; }
    if (i == 7) throw new Exception("Missing rate limit");
}
Check(!SupabaseAuth.ValidConfiguration("https://test.supabase.co", "sb_secret_not_allowed"), "Privileged Supabase keys are rejected");
var role = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"role\":\"service_role\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
Check(!SupabaseAuth.ValidConfiguration("https://test.supabase.co", "header." + role + ".signature"), "Legacy service_role keys are rejected");
Check(!SupabaseAuth.ValidConfiguration("http://test.supabase.co", "sb_publishable_local_test_only"), "Supabase credentials cannot be sent over unencrypted HTTP");
Check(fake.Paths.All(path => path.StartsWith("/auth/v1/") || path.StartsWith("/rest/v1/profiles") || path.StartsWith("/rest/v1/user_roles") || path.StartsWith("/rest/v1/urban_channex_expenses") || path.StartsWith("/rest/v1/expense_categories") || path.StartsWith("/rest/v1/urban_channex_assignments") || path.StartsWith("/rest/v1/urban_channex_owners") || path.StartsWith("/rest/v1/urban_channex_room_owners") || path == "/functions/v1/admin-delete-user"), "No Guesty or Channex API is requested");
Console.WriteLine($"{count} checks passed; no real Supabase or Channex calls.");
await app.StopAsync();

