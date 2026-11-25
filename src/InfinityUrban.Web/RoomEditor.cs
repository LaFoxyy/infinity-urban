using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;
public sealed record RoomEditInput(string Title,string? Code,int? Bedrooms,int? Beds,int? Bathrooms,string Description,Guid[] Facilities,string[] PhotoTickets,string ExpectedRevision);
public sealed record RoomAsset(Guid Actor,Guid Room,string PhotoId,string Url,string Environment,DateTimeOffset Expires);
public sealed class RoomEditor(RemoteAccommodationService remote,ChannexContentClient content,ChannexClient approved,IDataProtectionProvider protection,IOptions<ChannexOptions> options,CatalogFiles files)
{
 private readonly IDataProtector assets=protection.CreateProtector("urban.room-editor.assets.v1");
 private static string? T(JsonElement row,string key)=>RemoteAccommodationService.Text(row,key);
 private static JsonElement A(JsonElement row)=>RemoteAccommodationService.Attributes(row);
 private static string Revision(JsonElement row)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.GetRawText())));
 public async Task<object> Read(Guid id,Guid actor,CancellationToken ct)
 {
  var row=await remote.Read("room_types",id,ct);var a=A(row);var description=RemoteAccommodationService.Description(a)??"";var index=description.LastIndexOf(RemotePayloads.DetailsHeading,StringComparison.Ordinal);
  var details=index<0?new Dictionary<string,string>():description[(index+RemotePayloads.DetailsHeading.Length)..].Split('\n').Where(l=>l.Contains(':')).Select(l=>l.Split(':',2)).GroupBy(l=>l[0].Trim()).ToDictionary(g=>g.Key,g=>g.Last()[1].Trim());
  int? N(string field)=>details.TryGetValue(field,out var value)&&int.TryParse(value,out var n)?n:null;
  return new{id,revision=Revision(row),propertyId=RemoteAccommodationService.Parent(row,"property"),title=T(a,"title"),code=details.GetValueOrDefault("Código"),bedrooms=N("Quartos"),beds=N("Camas"),bathrooms=N("Banheiros"),guests=a.TryGetProperty("occ_adults",out var guests)?guests.GetInt32():0,description=index<0?description:description[..index],facilities=RemoteAccommodationService.FacilityIds(row),photos=RemoteAccommodationService.Photos(a).Select(p=>new{url=T(p,"url"),ticket=assets.Protect(JsonSerializer.Serialize(new RoomAsset(actor,id,T(p,"id")!,T(p,"url")!,options.Value.EnvironmentName,DateTimeOffset.UtcNow.AddHours(2))))})};
 }
 public async Task<object> Save(Guid id,RoomEditInput input,Guid actor,CancellationToken ct)
 {
  if(string.IsNullOrWhiteSpace(input.Title)||input.Title.Length>255||input.Code?.Length>80||input.Description is null||input.Description.Length>20000||input.Bedrooms is <0 or >100||input.Beds is <0 or >100||input.Bathrooms is <0 or >100||input.Facilities is null||input.Facilities.Length>200||input.PhotoTickets is null||input.PhotoTickets.Length>10||input.ExpectedRevision?.Length!=64)throw new PortalError(400,"Revise os dados do apartamento. São permitidas até 10 fotos.");
  using var gate=files.Lock(id);var current=await remote.Read("room_types",id,ct);var a=A(current);
  if(Revision(current)!=input.ExpectedRevision)throw new PortalError(409,"O apartamento mudou desde a abertura. Reabra a edição antes de salvar.");
  var allowed=(await approved.FetchCollectionAsync("room_facilities/options",ct)).Select(p=>T(p,"id")).Concat(RemoteAccommodationService.FacilityIds(current)).ToHashSet();
  if(input.Facilities.Any(f=>!allowed.Contains(f.ToString())))throw new PortalError(400,"Uma comodidade não está disponível na Channex.");
  var originals=RemoteAccommodationService.Photos(a);var kept=new HashSet<string>();var changes=new JsonArray();var expected=new List<string?>();
  foreach(var ticket in input.PhotoTickets){
   RoomAsset? asset;try{asset=JsonSerializer.Deserialize<RoomAsset>(assets.Unprotect(ticket));}catch{asset=null;}
   if(asset is not null){
    if(asset.Actor!=actor||asset.Room!=id||asset.Environment!=options.Value.EnvironmentName||asset.Expires<DateTimeOffset.UtcNow||!kept.Add(asset.PhotoId)||!originals.Any(p=>T(p,"id")==asset.PhotoId&&T(p,"url")==asset.Url))throw new PortalError(400,"Uma imagem não pertence a este apartamento ou expirou. Reabra a edição.");
    var entry=new JsonObject{["id"]=asset.PhotoId,["url"]=asset.Url,["position"]=expected.Count};var original=originals.Single(p=>T(p,"id")==asset.PhotoId);foreach(var key in new[]{"description","author","kind"})if(original.TryGetProperty(key,out var value))entry[key]=JsonNode.Parse(value.GetRawText());changes.Add(entry);expected.Add(asset.PhotoId);
   }else{changes.Add(new JsonObject{["url"]=content.ResolvePhoto(ticket,actor),["position"]=expected.Count,["kind"]="photo",["description"]=input.Title,["author"]="Urban Infinity"});expected.Add(null);}
  }
  foreach(var removed in originals.Where(p=>!kept.Contains(T(p,"id")!)))changes.Add(new JsonObject{["id"]=T(removed,"id"),["is_removed"]=true});
  var description=input.Description+RemotePayloads.DetailsHeading+$"Código: {input.Code??""}\nQuartos: {input.Bedrooms}\nCamas: {input.Beds}\nBanheiros: {input.Bathrooms}";
  var bodyContent=a.TryGetProperty("content",out var c)&&c.ValueKind==JsonValueKind.Object?JsonNode.Parse(c.GetRawText())!.AsObject():new JsonObject();bodyContent["description"]=description;bodyContent["photos"]=changes;
  await content.PutRoomContent(id,new{room_type=new{title=input.Title,facilities=input.Facilities,content=bodyContent}},ct);
  var saved=await remote.Read("room_types",id,ct);var photos=RemoteAccommodationService.Photos(A(saved));
  if(T(A(saved),"title")!=input.Title||RemoteAccommodationService.Description(A(saved))!=description||!RemoteAccommodationService.FacilityIds(saved).SequenceEqual(input.Facilities.Select(f=>f.ToString()).Order())||photos.Length!=expected.Count||expected.Where((photo,index)=>photo is not null&&T(photos[index],"id")!=photo).Any())throw new PortalError(409,"O envio foi feito, mas os dados ainda não foram confirmados. Reabra o apartamento antes de repetir.");
  return new{id,confirmed=true};
 }
}
