using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stayline;
namespace InfinityUrban.Web;
public sealed record ChannelGeneralInput(string Title,Guid[] PropertyIds,string ExpectedRevision,bool Confirm);
public sealed class ChannelGeneralSettings(ChannexClient client,ChannexMappingWriter writer,CatalogFiles files)
{
 private static JsonElement A(JsonElement row)=>RemoteAccommodationService.Attributes(row);
 private static string? T(JsonElement row,string key)=>RemoteAccommodationService.Text(row,key);
 private static string Revision(JsonElement row)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.GetRawText())));
 private static string[] Properties(JsonElement channel)=>A(channel).GetProperty("properties").EnumerateArray().Select(p=>p.GetString()!).Order().ToArray();
 private async Task<JsonElement> Read(Guid id,CancellationToken ct){var row=(await client.ReadStagingJsonAsync($"channels/{id:D}",ct)).GetProperty("data");if(T(row,"id")!=id.ToString()||!string.Equals(T(A(row),"channel"),"AirBNB",StringComparison.OrdinalIgnoreCase))throw new PortalError(400,"Selecione uma conexão Airbnb válida.");return row;}
 public async Task<object> View(Guid id,CancellationToken ct){var channel=await Read(id,ct);var properties=await client.FetchCollectionAsync("properties",ct);return new{id,title=T(A(channel),"title"),revision=Revision(channel),selectedPropertyIds=Properties(channel),properties=properties.Select(p=>new{id=T(p,"id"),title=T(A(p),"title")})};}
 public async Task<object> Save(Guid id,ChannelGeneralInput input,CancellationToken ct)
 {
  if(!input.Confirm||string.IsNullOrWhiteSpace(input.Title)||input.Title.Length>255||input.PropertyIds is null||input.PropertyIds.Length==0||input.PropertyIds.Length>1000||input.PropertyIds.Any(p=>p==Guid.Empty)||input.PropertyIds.Distinct().Count()!=input.PropertyIds.Length||input.ExpectedRevision?.Length!=64)throw new PortalError(400,"Selecione pelo menos um prédio e confirme a atualização do canal.");
  using var gate=files.Lock(id);var channel=await Read(id,ct);if(Revision(channel)!=input.ExpectedRevision)throw new PortalError(409,"A conexão mudou. Reabra as configurações antes de salvar.");
  var available=(await client.FetchCollectionAsync("properties",ct)).Select(p=>T(p,"id")).ToHashSet();if(input.PropertyIds.Any(p=>!available.Contains(p.ToString())))throw new PortalError(400,"Um prédio selecionado não está disponível na Channex.");
  var selected=input.PropertyIds.Select(p=>p.ToString()).Order().ToArray();var removed=Properties(channel).Except(selected).ToHashSet();
  if(removed.Count>0&&A(channel).TryGetProperty("rate_plans",out var mappings)&&mappings.ValueKind==JsonValueKind.Array&&mappings.GetArrayLength()>0){
   var rates=await client.FetchCollectionAsync("rate_plans",ct);
   foreach(var mapping in mappings.EnumerateArray()){var rateId=T(mapping,"rate_plan_id");var rate=rates.FirstOrDefault(r=>T(r,"id")==rateId);if(rate.ValueKind!=JsonValueKind.Object||removed.Contains(RemoteAccommodationService.Parent(rate,"property")??""))throw new PortalError(409,"Um prédio removido pode ter anúncios mapeados. Resolva os vínculos antes de desmarcá-lo.");}
  }
  if(T(A(channel),"title")==input.Title&&Properties(channel).SequenceEqual(selected))return new{confirmed=true};
  // Omit settings, currency, group and rate_plans: their omission preserves the connection and its mappings.
  await writer.UpdateProperties(id,T(A(channel),"channel")!,input.Title,input.PropertyIds,ct);
  var saved=await Read(id,ct);
  if(T(A(saved),"title")!=input.Title||!Properties(saved).SequenceEqual(selected)||A(channel).GetProperty("rate_plans").GetRawText()!=A(saved).GetProperty("rate_plans").GetRawText())throw new PortalError(409,"O envio foi feito. Atualize a conexão para conferir a confirmação antes de repetir.");
  return new{confirmed=true};
 }
}
