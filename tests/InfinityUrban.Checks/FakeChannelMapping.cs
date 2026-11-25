using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
sealed partial class UnifiedTransport
{
 public static readonly Guid MappingChannel=Guid.NewGuid(),MappingRate=Guid.NewGuid(),ExistingMapping=Guid.NewGuid();
 public int MappingWrites;public bool LoseMappingResponse;
 private readonly List<object> channelMappings=[new{id=ExistingMapping,rate_plan_id=Guid.NewGuid(),settings=new{listing_id="222222",pricing_setting=new{preserve="existing pricing"}}}];
 private string channelVersion="initial";private string channelTitle="Airbnb demonstração";private string[] channelProperties=[ExistingProperty];public int GeneralWrites;public JsonElement GeneralPayload;
 public void SeedMappingRates()=>Store("rate_plans",MappingRate.ToString(),new{title="Tarifa padrão",property_id=ExistingProperty,room_type_id=ExistingRoom});
 private object ChannelFixture()=>new{id=MappingChannel,type="channel",attributes=new{id=MappingChannel,title=channelTitle,channel="AirBNB",is_active=true,updated_at=channelVersion,properties=channelProperties,rate_plans=channelMappings.ToArray(),settings=new{tokens="NEVER EXPOSE OAUTH TOKENS",email="private-host@example.com"}}};
 private async Task<HttpResponseMessage?> MappingRequest(HttpRequestMessage request,string path,CancellationToken ct)
 {
  if(path=="channels")return Ok(new{data=new[]{ChannelFixture()},meta=new{total=1,page=1}});
  if(!path.StartsWith("channels/"))return null;
  if(path==$"channels/{MappingChannel}"&&request.Method==HttpMethod.Put){var body=await request.Content!.ReadFromJsonAsync<JsonElement>(ct);var input=body.GetProperty("channel");GeneralPayload=body;GeneralWrites++;channelTitle=input.GetProperty("title").GetString()!;channelProperties=input.GetProperty("properties").EnumerateArray().Select(p=>p.GetString()!).ToArray();channelVersion=Guid.NewGuid().ToString();return Ok(new{data=ChannelFixture()});}
  if(path==$"channels/{MappingChannel}")return Ok(new{data=ChannelFixture()});
  if(path==$"channels/{MappingChannel}/action/listings")return Ok(new{data=new{listing_id_dictionary=new{values=new[]{new{id="111111",title="Apartamento piloto",city="São Paulo"},new{id="222222",title="Anúncio já mapeado",city="São Paulo"},new{id="333333",title="Teste de resposta incerta",city="São Paulo"}}}}});
  if(path==$"channels/{MappingChannel}/mappings"&&request.Method==HttpMethod.Post)
  {
   var input=await request.Content!.ReadFromJsonAsync<JsonElement>(ct);var mapping=input.GetProperty("mapping");
   if(mapping.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"rate_plan_id","settings"})==false)throw new Exception("Unexpected mapping payload");
   var listing=mapping.GetProperty("settings").GetProperty("listing_id").GetString();
   if(listing is not("111111" or "333333")||mapping.GetProperty("rate_plan_id").GetGuid()!=MappingRate)throw new Exception("Unexpected mapping target");
   MappingWrites++;
   if(LoseMappingResponse)throw new HttpRequestException("Simulated lost response");
   var id=Guid.NewGuid();channelMappings.Add(new{id,rate_plan_id=MappingRate,settings=new{listing_id=listing}});channelVersion=Guid.NewGuid().ToString();
   return Ok(new{data=new{id,type="channel_rate_plan",attributes=new{id,settings=new{listing_id=listing}}}});
  }
  return new HttpResponseMessage(HttpStatusCode.NotFound){Content=JsonContent.Create(new{errors=new{title="Not found"}})};
 }
}
