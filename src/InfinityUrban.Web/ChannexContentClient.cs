using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Stayline;

namespace InfinityUrban.Web;

// Additive content API. It has no ARI, booking, mapping, force, or settings methods.
public sealed class ChannexContentClient(HttpClient http,IOptions<ChannexOptions> options,IDataProtectionProvider protection)
{
    private readonly IDataProtector tickets=protection.CreateProtector("urban.channex.upload.v1");
    private HttpRequestMessage Request(HttpMethod method,string relative)
    {
        var opt=options.Value;
        if(!opt.Enabled||string.IsNullOrWhiteSpace(opt.ApiKey))throw new PortalError(503,"A Channex não está configurada no servidor original.");
        var request=new HttpRequestMessage(method,new Uri(opt.ApiBaseUri,relative));
        request.Headers.Add("user-api-key",opt.ApiKey);return request;
    }
    public async Task<UploadReference> Upload(IFormFile file,Guid actor,CancellationToken ct)
    {
        if(file.Length is <=0 or >10*1024*1024)throw new PortalError(400,"Cada foto deve ter até 10 MB.");
        await using var stream=file.OpenReadStream();var header=new byte[12];var count=await stream.ReadAsync(header,ct);stream.Position=0;
        var type=count>=3&&header[0]==255&&header[1]==216&&header[2]==255?"image/jpeg":
            count>=8&&header.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})?"image/png":
            count>=12&&Encoding.ASCII.GetString(header,0,4)=="RIFF"&&Encoding.ASCII.GetString(header,8,4)=="WEBP"?"image/webp":null;
        if(type is null)throw new PortalError(400,"Envie uma imagem JPEG, PNG ou WebP válida.");
        using var request=Request(HttpMethod.Post,"photos/upload");
        using var multipart=new MultipartFormDataContent();var part=new StreamContent(stream);part.Headers.ContentType=new MediaTypeHeaderValue(type);
        multipart.Add(part,"photo",type=="image/png"?"photo.png":type=="image/webp"?"photo.webp":"photo.jpg");request.Content=multipart;
        using var response=await http.SendAsync(request,ct);await Check(response);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var url=json.RootElement.GetProperty("url").GetString();
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new PortalError(502,"A Channex não retornou uma URL de imagem válida.");
        var ticket=tickets.Protect(JsonSerializer.Serialize(new PhotoTicket(actor,url!,DateTimeOffset.UtcNow.AddHours(24),options.Value.EnvironmentName)));
        return new(url!,ticket);
    }
    public string ResolvePhoto(string ticket,Guid actor)
    {
        try
        {
            var data=JsonSerializer.Deserialize<PhotoTicket>(tickets.Unprotect(ticket));
            if(data is null||data.Actor!=actor||data.Expires<DateTimeOffset.UtcNow||data.Environment!=options.Value.EnvironmentName)throw new Exception();
            return data.Url;
        }
        catch{throw new PortalError(400,"Uma foto expirou ou pertence a outro cadastro. Envie-a novamente.");}
    }
    public Task PutBuildingContent(Guid id,object body,CancellationToken ct)=>Put("properties",id,body,ct);
    public Task PutRoomContent(Guid id,object body,CancellationToken ct)=>Put("room_types",id,body,ct);
    public async Task DeleteAccommodation(string resource,Guid id,CancellationToken ct)
    {
        if(resource is not("properties" or "room_types"))throw new PortalError(400,"Cadastro inválido.");
        using var request=Request(HttpMethod.Delete,$"{resource}/{id:D}");using var response=await http.SendAsync(request,ct);
        if(response.StatusCode is System.Net.HttpStatusCode.UnprocessableEntity or System.Net.HttpStatusCode.Conflict)throw new PortalError(409,"A Channex bloqueou a exclusão. Confira os canais, mapeamentos e reservas vinculados ao cadastro.");
        await Check(response);
    }
    public Task PutBuilding(Guid id,BuildingInput input,CancellationToken ct)=>Put("properties",id,new{property=new{address=input.Address,city=input.City,state=input.State,country=input.Country,zip_code=input.ZipCode,email=input.Email,phone=input.Phone,content=new{description=input.Description}}},ct);
    public Task PutRoom(Guid id,RoomInput input,string[] photos,CancellationToken ct)
    {
        if(photos.Length>10)throw new PortalError(400,"Cada apartamento aceita até 10 fotos.");
        return Put("room_types",id,new{room_type=new{facilities=input.Facilities,content=new{description=RemotePayloads.RoomDescription(input),photos=photos.Select((url,index)=>new{url,position=index,kind="photo",description=input.Title,author="Urban Infinity"})}}},ct);
    }
    private async Task Put(string resource,Guid id,object body,CancellationToken ct)
    {
        using var request=Request(HttpMethod.Put,$"{resource}/{id:D}");
        request.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");
        using var response=await http.SendAsync(request,ct);await Check(response);
    }
    private static Task Check(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return Task.CompletedTask;
        throw new PortalError(response.StatusCode==System.Net.HttpStatusCode.TooManyRequests?429:502,
            "A Channex não concluiu o envio de conteúdo. Confira as permissões da chave e o cadastro antes de repetir.");
    }
    private sealed record PhotoTicket(Guid Actor,string Url,DateTimeOffset Expires,string Environment);
}
public sealed record UploadReference(string Url,string Ticket);
