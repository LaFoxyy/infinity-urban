using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;

public sealed record BuildingEditInput(BuildingInput Input,string ExpectedRevision);
public sealed record BuildingAsset(Guid Actor,Guid Property,string? PhotoId,string Url,string Kind,string Environment,DateTimeOffset Expires);
public sealed class BuildingEditor(RemoteAccommodationService remote,ChannexContentClient content,ChannexClient approved,IDataProtectionProvider protection,IOptions<ChannexOptions> options,CatalogFiles files)
{
    private readonly IDataProtector assets=protection.CreateProtector("urban.property-editor.assets.v1");
    private static string? T(JsonElement row,string name)=>RemoteAccommodationService.Text(row,name);
    private static JsonElement A(JsonElement row)=>RemoteAccommodationService.Attributes(row);
    private static string Revision(JsonElement property)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(property.GetRawText())));
    private string Reference(Guid actor,Guid id,string? photoId,string url,string kind)=>assets.Protect(JsonSerializer.Serialize(new BuildingAsset(actor,id,photoId,url,kind,options.Value.EnvironmentName,DateTimeOffset.UtcNow.AddHours(2))));
    private BuildingAsset? Existing(string ticket,Guid actor,Guid property,string kind)
    {
        BuildingAsset? asset;try{asset=JsonSerializer.Deserialize<BuildingAsset>(assets.Unprotect(ticket));}catch{return null;}
        if(asset is null||asset.Actor!=actor||asset.Property!=property||asset.Kind!=kind||asset.Environment!=options.Value.EnvironmentName||asset.Expires<DateTimeOffset.UtcNow)throw new PortalError(400,"Uma imagem não pertence a este prédio ou expirou. Reabra a edição.");
        return asset;
    }
    public async Task<object> Read(Guid id,Guid actor,CancellationToken ct)
    {
        var property=await remote.Read("properties",id,ct);var a=A(property);var logo=T(a,"logo_url");var photos=RemoteAccommodationService.Photos(a);
        var information=a.TryGetProperty("content",out var c)?T(c,"important_information"):null;
        return new{id,revision=Revision(property),input=new BuildingInput(Guid.NewGuid(),T(a,"title")??"",T(a,"currency")??"",T(a,"timezone")??"",T(a,"address")??"",T(a,"city")??"",T(a,"state")??"",T(a,"country")??"",T(a,"zip_code")??"",T(a,"email")??"",T(a,"phone")??"",RemoteAccommodationService.Description(a)??"",information,RemoteAccommodationService.FacilityIds(property).Select(Guid.Parse).ToArray()),photos=photos.Select(p=>new{url=T(p,"url"),ticket=Reference(actor,id,T(p,"id"),T(p,"url")!,"photo")}),logo=string.IsNullOrWhiteSpace(logo)?null:new{url=logo,ticket=Reference(actor,id,null,logo,"logo")}};
    }
    private static void Validate(BuildingInput input)
    {
        bool Length(string? value,int max)=>value?.Length>max;
        if(string.IsNullOrWhiteSpace(input.Title)||Length(input.Title,255)||Length(input.Address,255)||Length(input.City,255)||Length(input.State,255)||(!string.IsNullOrWhiteSpace(input.Country)&&input.Country.Length!=2)||Length(input.ZipCode,32)||Length(input.Phone,32)||Length(input.Description,20000)||Length(input.ImportantInformation,20000)||(!string.IsNullOrWhiteSpace(input.Email)&&!MailAddress.TryCreate(input.Email,out _))||input.Facilities?.Length>200||input.Facilities?.Any(id=>id==Guid.Empty)==true||input.PhotoTickets?.Length>30)throw new PortalError(400,"Revise as informações do prédio. A galeria aceita até 30 fotos.");
    }
    public async Task<object> Save(Guid id,BuildingEditInput edit,Guid actor,CancellationToken ct)
    {
        if(edit.Input is null)throw new PortalError(400,"Informe os dados do prédio.");
        Validate(edit.Input);if(string.IsNullOrWhiteSpace(edit.ExpectedRevision)||edit.ExpectedRevision.Length!=64)throw new PortalError(400,"Reabra a edição para carregar os dados atuais.");
        // The file lock is delivery coordination only. No building content is persisted locally.
        using var gate=files.Lock(id);var current=await remote.Read("properties",id,ct);var a=A(current);var input=edit.Input;
        if(Revision(current)!=edit.ExpectedRevision)throw new PortalError(409,"Este prédio mudou desde que você abriu a edição. Reabra para conferir os dados antes de salvar.");
        if(input.Currency!=T(a,"currency")||input.Timezone!=T(a,"timezone"))throw new PortalError(400,"Moeda e fuso horário não podem ser alterados nesta edição.");
        var allowed=(await approved.FetchCollectionAsync("property_facilities/options",ct)).Select(f=>T(f,"id")).Concat(RemoteAccommodationService.FacilityIds(current)).ToHashSet();
        if((input.Facilities??[]).Any(f=>!allowed.Contains(f.ToString())))throw new PortalError(400,"Uma comodidade não está disponível na Channex.");
        var existingPhotos=RemoteAccommodationService.Photos(a);var kept=new HashSet<string>();var photoChanges=new JsonArray();var expectedPhotos=new List<(string? Id,string Url)>();
        foreach(var ticket in input.PhotoTickets??[])
        {
            var asset=Existing(ticket,actor,id,"photo");
            if(asset is not null)
            {
                if(asset.PhotoId is null||!kept.Add(asset.PhotoId)||!existingPhotos.Any(p=>T(p,"id")==asset.PhotoId&&T(p,"url")==asset.Url))throw new PortalError(400,"Uma foto foi alterada ou repetida. Reabra a edição.");
                var original=existingPhotos.Single(p=>T(p,"id")==asset.PhotoId);var entry=new JsonObject{["id"]=asset.PhotoId,["url"]=asset.Url,["position"]=expectedPhotos.Count};
                foreach(var field in new[]{"kind","author","description"})if(original.TryGetProperty(field,out var value))entry[field]=JsonNode.Parse(value.GetRawText());
                photoChanges.Add(entry);expectedPhotos.Add((asset.PhotoId,asset.Url));
            }
            else
            {
                var url=content.ResolvePhoto(ticket,actor);photoChanges.Add(new JsonObject{["url"]=url,["position"]=expectedPhotos.Count,["kind"]="photo",["description"]=input.Title,["author"]="Urban Infinity"});expectedPhotos.Add((null,url));
            }
        }
        foreach(var removed in existingPhotos.Where(p=>!kept.Contains(T(p,"id")!)))photoChanges.Add(new JsonObject{["id"]=T(removed,"id"),["is_removed"]=true});
        var oldLogo=T(a,"logo_url");string? logo=null;
        if(!string.IsNullOrWhiteSpace(input.LogoTicket))
        {
            var asset=Existing(input.LogoTicket,actor,id,"logo");logo=asset?.Url??content.ResolvePhoto(input.LogoTicket,actor);
            if(asset is not null&&logo!=oldLogo)throw new PortalError(409,"O logo foi alterado. Reabra a edição.");
        }
        var bodyContent=a.TryGetProperty("content",out var currentContent)&&currentContent.ValueKind==JsonValueKind.Object?JsonNode.Parse(currentContent.GetRawText())!.AsObject():new JsonObject();
        bodyContent["description"]=input.Description??"";bodyContent["important_information"]=input.ImportantInformation??"";bodyContent["photos"]=photoChanges;
        var property=new JsonObject{["title"]=input.Title,["address"]=input.Address,["city"]=input.City,["state"]=input.State,["country"]=string.IsNullOrWhiteSpace(input.Country)?null:input.Country.ToUpperInvariant(),["zip_code"]=input.ZipCode,["email"]=string.IsNullOrWhiteSpace(input.Email)?null:input.Email,["phone"]=input.Phone,["facilities"]=JsonSerializer.SerializeToNode(input.Facilities??[]),["content"]=bodyContent};
        if(logo!=oldLogo)property["logo_url"]=logo;
        await content.PutBuildingContent(id,new JsonObject{["property"]=property},ct);
        var saved=await remote.Read("properties",id,ct);var savedAttributes=A(saved);var savedPhotos=RemoteAccommodationService.Photos(savedAttributes);
        foreach(var (field,value) in new[]{("title",input.Title),("address",input.Address),("city",input.City),("state",input.State),("country",input.Country?.ToUpperInvariant()),("zip_code",input.ZipCode),("email",input.Email),("phone",input.Phone)})if((T(savedAttributes,field)??"")!=(value??""))throw Unconfirmed();
        if(RemoteAccommodationService.Description(savedAttributes)!=(input.Description??"")||!savedAttributes.TryGetProperty("content",out var savedContent)||(T(savedContent,"important_information")??"")!=(input.ImportantInformation??""))throw Unconfirmed();
        if(!RemoteAccommodationService.FacilityIds(saved).SequenceEqual((input.Facilities??[]).Select(f=>f.ToString()).Order()))throw Unconfirmed();
        if(savedPhotos.Length!=expectedPhotos.Count)throw Unconfirmed();
        for(var index=0;index<expectedPhotos.Count;index++)if(expectedPhotos[index].Id is {} photoId&&T(savedPhotos[index],"id")!=photoId)throw Unconfirmed();
        if(logo is null&&!string.IsNullOrWhiteSpace(T(savedAttributes,"logo_url"))||logo is not null&&!Uri.TryCreate(T(savedAttributes,"logo_url"),UriKind.Absolute,out _))throw Unconfirmed();
        return new{id,confirmed=true,stage="updated"};
    }
    private static PortalError Unconfirmed()=>new(409,"A atualização foi enviada, mas a Channex ainda não confirmou todos os dados. Reabra o prédio e confira antes de repetir.");
}
