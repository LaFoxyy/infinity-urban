using System.Net;
using System.Net.Mail;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;
public sealed record OwnerInput(Guid Id,string FullName,string? Email,string? Phone,Guid? ExpectedRevision=null);
public sealed record OwnerDelete(Guid ExpectedRevision);
public sealed record RoomOwnerInput(Guid? OwnerId,Guid? ExpectedOwnerId,Guid? ExpectedRevision);
public sealed record OwnerView(Guid Id,string FullName,string? Email,string? Phone,Guid Revision);
public sealed record RoomOwnerView(Guid RoomTypeId,Guid PropertyId,Guid OwnerId,Guid Revision);
public sealed record OwnerRow(Guid Id,[property:JsonPropertyName("full_name")]string FullName,string? Email,string? Phone,Guid Revision)
{public OwnerView View()=>new(Id,FullName,Email,Phone,Revision);}
public sealed record RoomOwnerRow([property:JsonPropertyName("room_type_id")]Guid RoomTypeId,[property:JsonPropertyName("property_id")]Guid PropertyId,[property:JsonPropertyName("owner_id")]Guid OwnerId,Guid Revision)
{public RoomOwnerView View()=>new(RoomTypeId,PropertyId,OwnerId,Revision);}
public sealed class OwnersService(SupabaseAuth auth,RemoteAccommodationService remote,IOptions<ChannexOptions> options)
{
    private string Environment=>options.Value.EnvironmentName;
    private const string OwnerFields="id,full_name,email,phone,revision";
    private const string LinkFields="room_type_id,property_id,owner_id,revision";
    private string Owners=>$"urban_channex_owners?environment=eq.{Environment}";
    private string Links=>$"urban_channex_room_owners?environment=eq.{Environment}";
    public async Task<object> List(string token,CancellationToken ct)
    {
        var ownersTask=All<OwnerRow>(token,Owners+"&select="+OwnerFields+"&order=full_name,id",ct);
        var linksTask=All<RoomOwnerRow>(token,Links+"&select="+LinkFields+"&order=room_type_id",ct);
        await Task.WhenAll(ownersTask,linksTask);
        return new{owners=(await ownersTask).Select(o=>o.View()),assignments=(await linksTask).Select(a=>a.View()),fetchedAt=DateTimeOffset.UtcNow};
    }
    public async Task<OwnerView> Save(string token,OwnerInput input,CancellationToken ct)
    {
        var name=input.FullName?.Trim();var email=string.IsNullOrWhiteSpace(input.Email)?null:input.Email.Trim();var phone=string.IsNullOrWhiteSpace(input.Phone)?null:input.Phone.Trim();
        if(input.Id==Guid.Empty||string.IsNullOrWhiteSpace(name)||name.Length>180||email?.Length>320||phone?.Length>50||email is not null&&(!MailAddress.TryCreate(email,out var address)||address.Address!=email))throw new PortalError(400,"Preencha um nome válido e revise email e telefone.");
        OwnerRow[] rows;
        if(input.ExpectedRevision is {} revision)
        {
            rows=await Write<OwnerRow>(token,HttpMethod.Patch,Owners+$"&id=eq.{input.Id:D}&revision=eq.{revision:D}&select={OwnerFields}",new{full_name=name,email,phone,revision=Guid.NewGuid(),updated_at=DateTimeOffset.UtcNow},ct);
            if(rows.Length!=1)throw Changed();
        }
        else
        {
            rows=await Write<OwnerRow>(token,HttpMethod.Post,"urban_channex_owners?on_conflict=id&select="+OwnerFields,new{id=input.Id,environment=Environment,full_name=name,email,phone},ct,"return=representation,resolution=ignore-duplicates");
            if(rows.Length==0){var existing=await Read<OwnerRow>(token,Owners+$"&id=eq.{input.Id:D}&select={OwnerFields}&limit=1",ct);if(existing.Length!=1||existing[0].FullName!=name||existing[0].Email!=email||existing[0].Phone!=phone)throw Changed();rows=existing;}
        }
        return rows.Single().View();
    }
    public async Task Delete(string token,Guid id,Guid revision,CancellationToken ct)
    {
        if((await Read<RoomOwnerRow>(token,Links+$"&owner_id=eq.{id:D}&select={LinkFields}&limit=1",ct)).Length>0)throw new PortalError(409,"Desvincule os apartamentos antes de excluir o proprietário.");
        var rows=await Write<OwnerRow>(token,HttpMethod.Delete,Owners+$"&id=eq.{id:D}&revision=eq.{revision:D}&select={OwnerFields}",null,ct);
        if(rows.Length==0&&(await Read<OwnerRow>(token,Owners+$"&id=eq.{id:D}&select={OwnerFields}&limit=1",ct)).Length>0)throw Changed();
    }
    public async Task<RoomOwnerView?> Link(string token,Guid roomId,RoomOwnerInput input,CancellationToken ct)
    {
        if(roomId==Guid.Empty||input.OwnerId==Guid.Empty)throw new PortalError(400,"Apartamento ou proprietário inválido.");
        var current=(await Read<RoomOwnerRow>(token,Links+$"&room_type_id=eq.{roomId:D}&select={LinkFields}&limit=1",ct)).SingleOrDefault();
        if(current?.OwnerId==input.OwnerId)return current?.View();
        if(current?.OwnerId!=input.ExpectedOwnerId||current?.Revision!=input.ExpectedRevision)throw Changed();
        if(input.OwnerId is {} ownerId&&(await Read<OwnerRow>(token,Owners+$"&id=eq.{ownerId:D}&select={OwnerFields}&limit=1",ct)).Length!=1)throw new PortalError(404,"Proprietário não encontrado neste ambiente.");
        if(input.OwnerId is null)
        {
            var removed=await Write<RoomOwnerRow>(token,HttpMethod.Delete,Links+$"&room_type_id=eq.{roomId:D}&revision=eq.{current!.Revision:D}&select={LinkFields}",null,ct);if(removed.Length!=1)throw Changed();return null;
        }
        // Verify the real Room and derive its Property from Channex; the browser cannot invent the parent.
        var room=await remote.Read("room_types",roomId,ct);if(!Guid.TryParse(RemoteAccommodationService.Parent(room,"property"),out var propertyId))throw new PortalError(502,"A Channex não confirmou o prédio deste apartamento.");
        RoomOwnerRow[] rows;
        if(current is null)
        {
            rows=await Write<RoomOwnerRow>(token,HttpMethod.Post,"urban_channex_room_owners?on_conflict=environment,room_type_id&select="+LinkFields,new{environment=Environment,room_type_id=roomId,property_id=propertyId,owner_id=input.OwnerId},ct,"return=representation,resolution=ignore-duplicates");
            if(rows.Length==0){var existing=(await Read<RoomOwnerRow>(token,Links+$"&room_type_id=eq.{roomId:D}&select={LinkFields}&limit=1",ct)).SingleOrDefault();if(existing?.OwnerId!=input.OwnerId)throw Changed();return existing.View();}
        }
        else
        {
            rows=await Write<RoomOwnerRow>(token,HttpMethod.Patch,Links+$"&room_type_id=eq.{roomId:D}&revision=eq.{current.Revision:D}&select={LinkFields}",new{owner_id=input.OwnerId,property_id=propertyId,revision=Guid.NewGuid(),updated_at=DateTimeOffset.UtcNow},ct);if(rows.Length!=1)throw Changed();
        }
        return rows.Single().View();
    }
    private async Task<T[]> All<T>(string token,string path,CancellationToken ct)
    {
        var rows=new List<T>();while(rows.Count<=100000){var page=await Read<T>(token,path+$"&offset={rows.Count}&limit=500",ct);if(page.Length==0)return rows.ToArray();rows.AddRange(page);}throw new PortalError(503,"A lista de proprietários excedeu o limite de consulta.");
    }
    private async Task<T[]> Read<T>(string token,string path,CancellationToken ct)
    {
        using var response=await auth.Send(HttpMethod.Get,path,null,token,ct,"rest/v1/");Ensure(response);return await response.Content.ReadFromJsonAsync<T[]>(ct)??[];
    }
    private async Task<T[]> Write<T>(string token,HttpMethod method,string path,object? body,CancellationToken ct,string prefer="return=representation")
    {
        using var response=await auth.Send(method,path,body,token,ct,"rest/v1/",prefer);Ensure(response);return await response.Content.ReadFromJsonAsync<T[]>(ct)??[];
    }
    private static void Ensure(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new PortalError(403,"Você não tem permissão para alterar estes proprietários.");
        if(response.StatusCode==HttpStatusCode.Conflict)throw new PortalError(409,"O cadastro possui vínculos ou foi alterado. Atualize a lista antes de continuar.");
        throw new PortalError(502,"O Supabase não confirmou a operação de proprietários. Atualize a lista antes de repetir.");
    }
    private static PortalError Changed()=>new(409,"O proprietário ou o vínculo foi alterado por outra pessoa. Atualize a lista antes de continuar.");
}
