using System.Net;
using System.Text.Json;

static class ManagementChecks
{
    public static async Task Run(HttpClient client,HttpClientHandler handler,FakeSupabase fake,Action<bool,string> check,Func<Task<string>> token,Func<string,object,string?,Task<HttpResponseMessage>> post)
    {
        using var list=await client.GetAsync("/api/management/users");
        using var data=JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        check(list.IsSuccessStatusCode&&data.RootElement.GetProperty("users").GetArrayLength()==11,"Management list loads every page despite a 3-row server cap");
        check((await client.GetAsync("/gestao")).IsSuccessStatusCode,"Approved Master can open the management page");
        check((await client.GetAsync("/Pages/gestao.html")).StatusCode==HttpStatusCode.NotFound,"Management HTML has no public static-file bypass");
        var path="/api/management/users/"+FakeSupabase.TargetId;
        var before=fake.Writes;
        check((await post(path+"/status",new{status="rejected",expectedStatus="approved"},null)).StatusCode==HttpStatusCode.BadRequest&&fake.Writes==before,"Management writes require CSRF protection");
        var csrf=await token();
        check((await post("/api/management/users/"+FakeSupabase.CurrentId+"/status",new{status="rejected",expectedStatus="approved"},csrf)).StatusCode==HttpStatusCode.Forbidden&&fake.Writes==before,"Master cannot reject their own account");
        check((await post("/api/management/users/"+FakeSupabase.CurrentId+"/role",new{role="admin",expectedRoleId=(string?)null,expectedRole="master"},csrf)).StatusCode==HttpStatusCode.Forbidden,"Master cannot demote their own account");
        check((await post(path+"/status",new{status="rejected",expectedStatus="pending"},csrf)).StatusCode==HttpStatusCode.Conflict&&fake.TargetStatus=="approved","Stale status edits cannot overwrite a newer status");
        check((await post(path+"/status",new{status="rejected",expectedStatus="approved"},csrf)).IsSuccessStatusCode&&fake.TargetStatus=="rejected","Master can reject another account using a conditional update");
        check((await post(path+"/status",new{status="approved",expectedStatus="rejected"},csrf)).IsSuccessStatusCode&&fake.TargetStatus=="approved","Master can approve a rejected account");
        check((await post(path+"/role",new{role="superadmin",expectedRoleId=FakeSupabase.TargetRoleId,expectedRole="proprietario"},csrf)).StatusCode==HttpStatusCode.BadRequest,"Unknown roles cannot be written");
        check((await post(path+"/role",new{role="admin",expectedRoleId=FakeSupabase.TargetRoleId,expectedRole="funcionario"},csrf)).StatusCode==HttpStatusCode.Conflict&&fake.TargetRole=="proprietario","Stale role selection cannot overwrite current permissions");
        fake.ForceConflict=true;
        check((await post(path+"/role",new{role="admin",expectedRoleId=FakeSupabase.TargetRoleId,expectedRole="proprietario"},csrf)).StatusCode==HttpStatusCode.Conflict&&fake.TargetRole=="proprietario","A role changed between read and write cannot be overwritten");
        fake.ForceConflict=false;
        check((await post(path+"/role",new{role="funcionario",expectedRoleId=FakeSupabase.TargetRoleId,expectedRole="proprietario"},csrf)).IsSuccessStatusCode&&fake.TargetRole=="funcionario","Single role changes use one atomic conditional PATCH");
        fake.MultipleTargetRoles=true;before=fake.Writes;
        check((await post(path+"/role",new{role="proprietario",expectedRoleId=FakeSupabase.TargetRoleId,expectedRole="funcionario"},csrf)).StatusCode==HttpStatusCode.Conflict&&fake.Writes==before,"Multiple existing roles are preserved without destructive replacement");
        fake.MultipleTargetRoles=false;fake.MissingTargetRole=true;
        check((await post(path+"/role",new{role="funcionario",expectedRoleId=(string?)null,expectedRole=(string?)null},csrf)).IsSuccessStatusCode&&!fake.MissingTargetRole,"Master can assign an initial role to an account without permissions");
        check((await post(path+"/delete",new{confirmEmail="wrong@example.com"},csrf)).StatusCode==HttpStatusCode.BadRequest&&!fake.Deleted,"Account deletion requires exact target email confirmation");
        check((await post("/api/management/users/"+FakeSupabase.CurrentId+"/delete",new{confirmEmail="demo@example.com"},csrf)).StatusCode==HttpStatusCode.Forbidden,"Master cannot delete their own account");

        fake.CurrentRole="admin";
        check((await client.GetAsync("/api/management/users")).IsSuccessStatusCode,"Admin retains management read access");
        before=fake.Writes;
        check((await post(path+"/status",new{status="rejected",expectedStatus="approved"},csrf)).StatusCode==HttpStatusCode.Forbidden&&fake.Writes==before,"Admin cannot bypass the Master-only status rule");
        check((await post(path+"/role",new{role="master",expectedRoleId=FakeSupabase.TargetRoleId,expectedRole="funcionario"},csrf)).StatusCode==HttpStatusCode.Forbidden&&fake.Writes==before,"Admin cannot promote an account through a direct HTTP request");
        check((await post(path+"/delete",new{confirmEmail="ana@example.com"},csrf)).StatusCode==HttpStatusCode.Forbidden&&fake.Writes==before,"Admin cannot bypass the portal deletion restriction");
        fake.CurrentRole="funcionario";
        check((await client.GetAsync("/api/management/users")).StatusCode==HttpStatusCode.Forbidden,"Staff cannot enumerate management accounts");
        check((await client.GetAsync("/gestao")).StatusCode==HttpStatusCode.Forbidden,"Staff cannot open the management page");
        fake.CurrentRole="proprietario";
        check((await client.GetAsync("/api/management/users")).StatusCode==HttpStatusCode.Forbidden,"Owners cannot enumerate management accounts");
        fake.CurrentRole="master";
        check((await post(path+"/delete",new{confirmEmail="ana@example.com"},csrf)).IsSuccessStatusCode&&fake.Deleted,"Confirmed deletion delegates only to the existing Auth administration function");
        fake.Deleted=false;fake.TargetRole="proprietario";
    }
}
