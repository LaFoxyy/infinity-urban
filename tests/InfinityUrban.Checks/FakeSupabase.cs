using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfinityUrban.Web;

// This transport lives exclusively in the test executable, never in the portal.
sealed partial class FakeSupabase : HttpMessageHandler
{
    public const string AccessToken = "server-only-fake-token";
    public const string CurrentId = "5ea2156f-d2c6-455b-a20c-941841c811c2";
    public const string TargetId = "2b6bb567-83a5-471b-ac8c-6144904a2d45";
    public const string TargetRoleId = "c0194ae9-e047-41d1-9353-54cc1f0f6800";
    public int Calls, UserCalls, Writes;
    public bool Revoked, RejectIdentity, Unavailable, Deleted, ForceConflict, MissingTargetRole, MultipleTargetRoles;
    public string AccountStatus = "approved", CurrentRole = "master", TargetRole = "proprietario", TargetStatus = "approved";
    public List<string> Paths = [];
    public int SignupRequests;public string SignupMode="ok";public JsonElement SignupPayload;public bool SignupSessionRevoked;
    private readonly Dictionary<Guid, string> statuses = [];
    private readonly Dictionary<Guid, MemberRole> assignedRoles = [];
    private readonly HashSet<Guid> deleted = [];
    private readonly object gate = new();

    private MemberProfile[] Profiles()
    {
        var now = DateTimeOffset.UtcNow;
        MemberProfile P(string id,string name,string email,string status="approved") => new(Guid.Parse(id),email,name,status,now);
        var profiles = new[] {
            P(CurrentId,"Conta de teste","demo@example.com",AccountStatus),
            P(TargetId,"Ana Costa","ana@example.com",TargetStatus),
            P("6a1fe725-67cb-4122-b7f3-8f052eb3ab9d","Bruno Almeida","bruno@example.com"),
            P("5a21869d-c4da-4c6b-b17b-7841ff91f3d1","Carla Lima","carla@example.com"),
            P("084f5fe2-658b-41b5-92f9-f83ab8798332","Diego Santos","diego@example.com"),
            P("b168f8cf-550b-4b73-a2f2-8fd8995989bb","Elisa Martins","elisa@example.com"),
            P("5bd72141-eef1-444c-acba-f3384d4b9b2e","Fernando Rocha","fernando@example.com"),
            P("2fa092ae-5468-45d5-bd93-0d0e46e5ea23","Gabriela Melo","gabriela@example.com"),
            P("b2a3fcd3-6f6d-43e5-9898-d56c09e2b5e8","Henrique Oliveira","henrique@example.com"),
            P("50cffceb-cd4b-4889-a837-11aa17f50b24","Isabela Souza","isabela@example.com","pending"),
            P("9c7113af-cbd7-4918-b1a8-f00ef98059bd","João Ferreira","joao@example.com","rejected")
        };
        return profiles.Where(p=>!deleted.Contains(p.Id)&&!(Deleted&&p.Id==Guid.Parse(TargetId)))
            .Select(p=>statuses.TryGetValue(p.Id,out var status)?p with{Status=status}:p).ToArray();
    }

    private MemberRole[] Roles()
    {
        var profiles=Profiles();
        var roles=new[]{CurrentRole,TargetRole,"admin","admin","master","proprietario","proprietario","funcionario","proprietario"};
        var ids = new[]{CurrentId,TargetId,"6a1fe725-67cb-4122-b7f3-8f052eb3ab9d","5a21869d-c4da-4c6b-b17b-7841ff91f3d1","084f5fe2-658b-41b5-92f9-f83ab8798332","b168f8cf-550b-4b73-a2f2-8fd8995989bb","5bd72141-eef1-444c-acba-f3384d4b9b2e","2fa092ae-5468-45d5-bd93-0d0e46e5ea23","b2a3fcd3-6f6d-43e5-9898-d56c09e2b5e8"};
        List<MemberRole> rows=[];
        for(var index=0;index<ids.Length;index++)
        {
            var id=Guid.Parse(ids[index]);
            if(!profiles.Any(p=>p.Id==id)||(ids[index]==TargetId&&MissingTargetRole))continue;
            // Stable row IDs make compare-and-update tests meaningful across requests.
            var roleId=index==1?Guid.Parse(TargetRoleId):new Guid(index+1,0,0,new byte[8]);
            rows.Add(assignedRoles.TryGetValue(id,out var assigned)?assigned:new(roleId,id,roles[index]));
        }
        foreach(var pair in assignedRoles.Where(pair=>!rows.Any(row=>row.UserId==pair.Key)&&profiles.Any(p=>p.Id==pair.Key))) rows.Add(pair.Value);
        if(MultipleTargetRoles)rows.Add(new(Guid.Parse("ce458034-a46c-4b91-ac49-95ddca39c228"),Guid.Parse(TargetId),"admin"));
        return rows.ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var input=request.Content is null?default:await request.Content.ReadFromJsonAsync<JsonElement>(ct);
        lock(gate)
        {
            Calls++;
            var path=request.RequestUri!.AbsolutePath;Paths.Add(path);
            if(Unavailable)return new(HttpStatusCode.ServiceUnavailable);
            var query=Query(request.RequestUri);
            if(path.EndsWith("/signup"))
            {
                SignupRequests++;SignupPayload=input.Clone();
                if(request.Headers.Authorization is not null)throw new Exception("Public registration must not carry another user's token.");
                if(SignupMode=="weak")return new(HttpStatusCode.UnprocessableEntity){Content=JsonContent.Create(new{code="weak_password"})};
                if(SignupMode=="duplicate")return new(HttpStatusCode.UnprocessableEntity){Content=JsonContent.Create(new{code="user_already_exists"})};
                if(SignupMode=="unavailable")return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("upstream unavailable")};
                return SignupMode=="session"?Ok(new{access_token="new-signup-session",user=new{id=Guid.NewGuid()}}):Ok(new{user=new{id=Guid.NewGuid()}});
            }
            if(path.EndsWith("/logout")&&request.Headers.Authorization?.Parameter=="new-signup-session"){SignupSessionRevoked=true;return new(HttpStatusCode.NoContent);}
            if(path.EndsWith("/token"))return input.GetProperty("password").GetString()=="valid-test-password"
                ?Ok(new{access_token=AccessToken,expires_in=3600,user=new{id=CurrentId,email="demo@example.com"}}):new(HttpStatusCode.BadRequest);
            if(request.Headers.Authorization?.Parameter!=AccessToken)throw new Exception("Every protected Supabase call must carry the end-user JWT.");
            if(path.EndsWith("/user"))
            {
                UserCalls++;
                return RejectIdentity?new(HttpStatusCode.Unauthorized):Ok(new{id=CurrentId,email="demo@example.com"});
            }
            if(path.EndsWith("/logout")){Revoked=true;return new(HttpStatusCode.NoContent);}
            if(OwnerRequest(path,request.Method,query,input) is {} ownerResponse)return ownerResponse;
            if(AssignmentRequest(path,request.Method,query,input) is {} assignmentResponse)return assignmentResponse;
            if(ExpenseRequest(path,request.Method,query,input) is {} expenseResponse)return expenseResponse;
            if(path=="/rest/v1/profiles")
            {
                var rows=Profiles().AsEnumerable();
                if(query.TryGetValue("id",out var id))rows=rows.Where(row=>"eq."+row.Id==id);
                if(request.Method==HttpMethod.Patch)
                {
                    Writes++;
                    rows=rows.Where(row=>query.GetValueOrDefault("status")=="eq."+row.Status);
                    var row=rows.SingleOrDefault();
                    if(row is null||ForceConflict)return Ok(Array.Empty<object>());
                    var status=input.GetProperty("status").GetString()!;
                    if(row.Id==Guid.Parse(TargetId))TargetStatus=status;
                    else statuses[row.Id]=status;
                    return Ok(new[]{row with{Status=status}});
                }
                return Ok(rows.Skip(Offset(query)).Take(3));
            }
            if(path=="/rest/v1/user_roles")
            {
                var rows=Roles().AsEnumerable();
                if(query.TryGetValue("user_id",out var userId))rows=rows.Where(row=>"eq."+row.UserId==userId);
                if(query.TryGetValue("id",out var id))rows=rows.Where(row=>"eq."+row.Id==id);
                if(request.Method==HttpMethod.Post)
                {
                    Writes++;
                    var target=Guid.Parse(input.GetProperty("user_id").GetString()!);
                    var role=input.GetProperty("role").GetString()!;
                    var row=new MemberRole(target==Guid.Parse(TargetId)?Guid.Parse(TargetRoleId):Guid.NewGuid(),target,role);
                    if(target==Guid.Parse(TargetId)){MissingTargetRole=false;TargetRole=role;}else assignedRoles[target]=row;
                    return Ok(new[]{row});
                }
                if(request.Method==HttpMethod.Patch)
                {
                    Writes++;
                    var row=rows.SingleOrDefault(r=>query.GetValueOrDefault("role")=="eq."+r.Role);
                    if(row is null||ForceConflict)return Ok(Array.Empty<object>());
                    var next=row with{Role=input.GetProperty("role").GetString()!};
                    if(row.UserId==Guid.Parse(TargetId))TargetRole=next.Role;else assignedRoles[row.UserId]=next;
                    return Ok(new[]{next});
                }
                return Ok(rows.Skip(Offset(query)).Take(3));
            }
            if(path=="/functions/v1/admin-delete-user")
            {
                var target=Guid.Parse(input.GetProperty("target_user_id").GetString()!);
                if(!Profiles().Any(profile=>profile.Id==target))return new(HttpStatusCode.NotFound);
                Writes++;if(target==Guid.Parse(TargetId))Deleted=true;else deleted.Add(target);
                return Ok(new{success=true});
            }
            throw new Exception("Unexpected endpoint: "+path);
        }
    }

    private static Dictionary<string,string> Query(Uri uri)=>uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries)
        .Select(part=>part.Split('=',2)).ToDictionary(pair=>Uri.UnescapeDataString(pair[0]),pair=>Uri.UnescapeDataString(pair.ElementAtOrDefault(1)??""));
    private static int Offset(Dictionary<string,string> query)=>int.Parse(query.GetValueOrDefault("offset")??"0");
    private static HttpResponseMessage Ok(object content)=>new(HttpStatusCode.OK){Content=JsonContent.Create(content)};
}
