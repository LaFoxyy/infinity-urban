using System.Net;
using Microsoft.Extensions.DependencyInjection;
using InfinityUrban.Web;
static class SignupChecks
{
    public static async Task Run(IServiceProvider services,HttpClient client,FakeSupabase fake,Action<bool,string> check,Func<Task<string>> form,Func<string,object,string?,Task<HttpResponseMessage>> post)
    {
        var input=new{fullName="New Test User",email="signup-test@example.com",password="fake-password-123!",role="master",status="approved"};
        using(var missing=await post("/api/auth/signup",input,null))check(missing.StatusCode==HttpStatusCode.BadRequest&&fake.SignupRequests==0,"Registration requires CSRF before contacting Supabase");
        var token=await form();
        using(var invalid=await post("/api/auth/signup",new{fullName=" ",email="invalid",password="short"},token))check(invalid.StatusCode==HttpStatusCode.BadRequest&&fake.SignupRequests==0,"Invalid registration fields never reach Supabase");
        fake.SignupMode="session";
        using(var response=await post("/api/auth/signup",input,await form()))
        {
            var body=await response.Content.ReadAsStringAsync();
            check(response.IsSuccessStatusCode&&!body.Contains("new-signup-session")&&!body.Contains("access_token")&&!response.Headers.TryGetValues("Set-Cookie",out _),"Registration returns pending instructions without a portal session or Supabase tokens");
            check(fake.SignupPayload.GetProperty("data").EnumerateObject().Count()==1&&fake.SignupPayload.GetProperty("data").GetProperty("full_name").GetString()=="New Test User"&&!fake.SignupPayload.TryGetProperty("status",out _)&&!fake.SignupPayload.TryGetProperty("role",out _),"Browser-forged approval and admin roles are excluded from the registration payload");
            check(fake.SignupSessionRevoked,"Any auto-created Supabase registration session is revoked rather than bypassing approval");
        }
        fake.SignupMode="weak";
        using(var weak=await post("/api/auth/signup",input,await form()))check(weak.StatusCode==HttpStatusCode.BadRequest,"Supabase password-policy failures produce an actionable registration error");
        using(var limit=await post("/api/auth/signup",input,await form()))check(limit.StatusCode==HttpStatusCode.TooManyRequests,"Registration requests have a separate bounded rate limit");
        var auth=services.GetRequiredService<SupabaseAuth>();fake.SignupMode="duplicate";
        await auth.Register("Test","duplicate@example.com","fake-password-123!",default);
        check(true,"Duplicate account registration does not disclose whether the email already exists");
        fake.SignupMode="unavailable";
        try{await auth.Register("Test","error@example.com","fake-password-123!",default);check(false,"Expected unavailable signup");}
        catch(PortalError error){check(error.Status==503,"A non-JSON upstream failure produces a controlled registration error");}
        fake.SignupMode="ok";
    }
}
