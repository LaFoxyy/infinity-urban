using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;
public sealed record AssignmentInput(Guid UserId,Guid[] RoomIds);
public sealed record AssignmentView(Guid Id,Guid UserId,Guid RoomTypeId,Guid PropertyId,Guid? AssignedBy,DateTimeOffset CreatedAt);
public sealed record AssignmentRow(Guid Id,[property:JsonPropertyName("user_id")]Guid UserId,[property:JsonPropertyName("room_type_id")]Guid RoomTypeId,[property:JsonPropertyName("property_id")]Guid PropertyId,[property:JsonPropertyName("assigned_by")]Guid? AssignedBy,[property:JsonPropertyName("created_at")]DateTimeOffset CreatedAt)
{public AssignmentView View()=>new(Id,UserId,RoomTypeId,PropertyId,AssignedBy,CreatedAt);}
public sealed class AssignmentsService(SupabaseAuth auth,ManagementService members,RemoteAccommodationService remote,IOptions<ChannexOptions> options)
{
    private const string Fields="id,user_id,room_type_id,property_id,assigned_by,created_at";
    private string Rows=>$"urban_channex_assignments?environment=eq.{options.Value.EnvironmentName}&select={Fields}";
    public async Task<object> List(string token,CancellationToken ct)
    {
        var users=await members.List(token,ct);var rows=await ReadAll(token,Rows+"&order=created_at.desc,id",ct);
        return new{assignments=rows.Select(a=>a.View()),accounts=users.Where(u=>u.IsRestrictedOwner).Select(u=>new{id=u.Id,fullName=u.FullName,email=u.Email}),people=users.Select(u=>new{id=u.Id,fullName=u.FullName,email=u.Email}),fetchedAt=DateTimeOffset.UtcNow};
    }
    public Task<AssignmentRow[]> Mine(string token,Guid userId,CancellationToken ct)=>ReadAll(token,Rows+$"&user_id=eq.{userId:D}&order=id",ct);
    public async Task<object> Add(string token,AssignmentInput input,CancellationToken ct)
    {
        if(input.UserId==Guid.Empty||input.RoomIds is null||input.RoomIds.Length is <1 or >200||input.RoomIds.Any(id=>id==Guid.Empty))throw new PortalError(400,"Selecione uma conta de proprietário e de 1 a 200 apartamentos.");
        var account=await members.CurrentMember(token,input.UserId.ToString(),ct);
        if(account?.IsRestrictedOwner!=true)throw new PortalError(400,"Selecione uma conta aprovada com função Proprietário, sem funções administrativas ou de funcionário.");
        var rows=new List<object>();
        foreach(var id in input.RoomIds.Distinct())
        {
            System.Text.Json.JsonElement room;
            try{room=await remote.Read("room_types",id,ct);}catch(HttpRequestException e)when(e.StatusCode==HttpStatusCode.NotFound){throw new PortalError(404,"Um dos apartamentos não está disponível na Channex.");}
            if(!Guid.TryParse(RemoteAccommodationService.Parent(room,"property"),out var propertyId))throw new PortalError(502,"A Channex não confirmou o prédio de um dos apartamentos.");
            rows.Add(new{environment=options.Value.EnvironmentName,user_id=input.UserId,room_type_id=id,property_id=propertyId});
        }
        // One atomic PostgREST insert. Existing pairs keep their original actor/date.
        using(var response=await auth.Send(HttpMethod.Post,"urban_channex_assignments?on_conflict=environment,user_id,room_type_id",rows,token,ct,"rest/v1/","return=minimal,resolution=ignore-duplicates"))Ensure(response);
        var confirmed=await Mine(token,input.UserId,ct);
        if(input.RoomIds.Any(id=>!confirmed.Any(a=>a.RoomTypeId==id)))throw new PortalError(409,"O Supabase não confirmou todas as designações. Atualize a lista antes de continuar.");
        return new{confirmed=true,assignments=confirmed.Where(a=>input.RoomIds.Contains(a.RoomTypeId)).Select(a=>a.View())};
    }
    public async Task Remove(string token,Guid id,CancellationToken ct)
    {
        using var response=await auth.Send(HttpMethod.Delete,Rows+$"&id=eq.{id:D}",null,token,ct,"rest/v1/","return=minimal");Ensure(response);
    }
    private async Task<AssignmentRow[]> ReadAll(string token,string path,CancellationToken ct)
    {
        var rows=new List<AssignmentRow>();while(rows.Count<=100000){using var response=await auth.Send(HttpMethod.Get,path+$"&offset={rows.Count}&limit=500",null,token,ct,"rest/v1/");Ensure(response);var page=await response.Content.ReadFromJsonAsync<AssignmentRow[]>(ct)??[];if(page.Length==0)return rows.ToArray();rows.AddRange(page);}throw new PortalError(503,"A lista de designações excedeu o limite de consulta.");
    }
    private static void Ensure(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new PortalError(403,"Você não tem permissão para administrar estas designações.");
        if(response.StatusCode==HttpStatusCode.Conflict)throw new PortalError(409,"A conta ou as designações foram alteradas. Atualize a lista.");
        throw new PortalError(503,"Não foi possível confirmar as designações no Supabase. O acesso foi bloqueado até a verificação.");
    }
}
public sealed class PortfolioAccess(AssignmentsService assignments,RemoteAccommodationService remote)
{
    public async Task<object> List(HttpContext context)
    {
        var member=await IntegrationAccess.PortfolioReader(context);
        if(!member.IsRestrictedOwner)return await remote.List(context.RequestAborted);
        var token=await context.GetTokenAsync("access_token")??throw new PortalError(401,"Sua sessão expirou.");
        var allowed=await assignments.Mine(token,member.Id,context.RequestAborted);
        var scope=allowed.ToDictionary(a=>a.RoomTypeId,a=>a.PropertyId);
        var data=await remote.List(context.RequestAborted,scope);
        // Recheck after remote reads: a revoked assignment never releases the already-fetched response.
        var current=await assignments.Mine(token,member.Id,context.RequestAborted);
        if(!current.Select(a=>(a.RoomTypeId,a.PropertyId)).ToHashSet().SetEquals(allowed.Select(a=>(a.RoomTypeId,a.PropertyId))))throw new PortalError(409,"Suas designações mudaram. Atualize a página para consultar os apartamentos disponíveis.");
        return data;
    }
    public async Task<object> Room(HttpContext context,Guid id)
    {
        var member=await IntegrationAccess.PortfolioReader(context);
        if(member.IsRestrictedOwner)
        {
            var token=await context.GetTokenAsync("access_token")??throw new PortalError(401,"Sua sessão expirou.");
            if(!(await assignments.Mine(token,member.Id,context.RequestAborted)).Any(a=>a.RoomTypeId==id))throw new PortalError(404,"Apartamento não disponível para esta conta.");
        }
        var catalog=System.Text.Json.JsonSerializer.SerializeToElement(await List(context),System.Text.Json.JsonSerializerOptions.Web);
        var room=catalog.GetProperty("rooms").EnumerateArray().FirstOrDefault(r=>RemoteAccommodationService.Text(r,"id")==id.ToString());
        return room.ValueKind==System.Text.Json.JsonValueKind.Object?room:throw new PortalError(404,"Apartamento não disponível para esta conta.");
    }
}
