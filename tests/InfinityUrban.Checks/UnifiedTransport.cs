using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
sealed partial class UnifiedTransport:HttpMessageHandler
{
 public static readonly string PropertyFacility=Guid.NewGuid().ToString();
 public static readonly string ExistingProperty=Guid.NewGuid().ToString(),ExistingRoom=Guid.NewGuid().ToString(),EmptyProperty=Guid.NewGuid().ToString(),SiblingRoom=Guid.NewGuid().ToString();
 public List<string> Hosts=[];public List<(string Resource,JsonElement Body)> Writes=[];public int Uploads;public bool LoseRoomResponse;public bool OmitContactOnCreate;public string? RemoveRoomOnRead;
 private readonly Dictionary<string,JsonElement> entities=[];
 public UnifiedTransport(){Store("room_types",SiblingRoom,new{title="PRIVATE SIBLING ROOM",property_id=ExistingProperty,occ_adults=4,count_of_rooms=1,content=new{description="Must never reach another apartment owner",photos=new[]{new{url="https://img.channex.io/private-sibling-photo/",position=0}}}});Store("rate_plans",Guid.NewGuid().ToString(),new{title="PRIVATE SIBLING TARIFF",property_id=ExistingProperty,room_type_id=SiblingRoom,options=new[]{new{is_primary=true,rate="99999.00"}}});Store("properties",EmptyProperty,new{title="Prédio vazio",currency="BRL",timezone="America/Sao_Paulo",property_type="hotel"});Store("properties",ExistingProperty,new{title="PrÃ©dio existente",currency="BRL",timezone="America/Sao_Paulo",address="Rua do prÃ©dio",email="private-building@example.com",phone="PRIVATE PHONE",property_type="hotel",content=new{description="PrÃ©dio",photos=new[]{new{url="https://img.channex.io/property-photo-must-not-show/",position=0}}}});Store("room_types",ExistingRoom,new{title="Apartamento existente",property_id=ExistingProperty,occ_adults=2,count_of_rooms=1,content=new{description="Apartamento",photos=new[]{new{url="https://img.channex.io/room-photo/",position=0}}}});}
 private void Store(string kind,string id,object attributes)
 {
  var a=System.Text.Json.JsonSerializer.SerializeToNode(attributes)!.AsObject();
  if(kind is "room_types" or "properties"&&a["content"]?["photos"] is System.Text.Json.Nodes.JsonArray photos)foreach(var photo in photos){photo!["id"]??=Guid.NewGuid().ToString();photo["room_type_id"]=kind=="room_types"?id:null;photo["property_id"]=kind=="properties"?id:a["property_id"]?.GetValue<string>();}
  entities[kind+"/"+id]=JsonSerializer.SerializeToElement(new{id,type=kind switch{"properties"=>"property","room_types"=>"room_type",_=>"rate_plan"},attributes=a});
 }
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  var uri=request.RequestUri!;Hosts.Add(uri.Host);if(uri.Host!="app.channex.io")throw new Exception("Unexpected host "+uri.Host);
  if(request.Headers.GetValues("user-api-key").Single()!="catalog-test-key")throw new Exception("Unexpected key");
  var path=uri.AbsolutePath.Replace("/api/v1/","");
  if(ReservationRequest(request,path) is {} reservationResponse)return reservationResponse;
  if(await MappingRequest(request,path,ct) is {} mappingResponse)return mappingResponse;
  if(path=="photos/upload"){Uploads++;return Ok(new{url="https://img.channex.io/uploaded-room-photo/"});}
  if(request.Method==HttpMethod.Get)
  {
   if(path=="room_types/"+RemoveRoomOnRead){entities.Remove(path);RemoveRoomOnRead=null;}
   if(path=="property_facilities/options")return Ok(new{data=new[]{new{id=PropertyFacility,attributes=new{title="Elevador"}}}});
   if(path=="room_facilities/options")return Ok(new{data=Array.Empty<object>()});
   if(entities.TryGetValue(path,out var found))return Ok(new{data=found});
   if(path.Split('/').Length==2&&Guid.TryParse(path.Split('/')[1],out _))return new HttpResponseMessage(HttpStatusCode.NotFound){Content=JsonContent.Create(new{errors=new{title="Not found"}})};
   var rows=entities.Where(x=>x.Key.StartsWith(path+"/")).Select(x=>x.Value).ToArray();
   var query=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);if(query.TryGetValue("filter[property_id]",out var filter))rows=rows.Where(r=>r.GetProperty("attributes").TryGetProperty("property_id",out var parent)&&parent.GetString()==filter.ToString()).ToArray();
   var listRows=rows.Select(row=>{var reduced=System.Text.Json.JsonSerializer.SerializeToNode(row)!.AsObject();if(path is "properties" or "room_types"){var attributes=reduced["attributes"]!.AsObject();foreach(var field in new[]{"content","address","email","phone","city","state","country","zip_code"})attributes.Remove(field);}return reduced;}).ToArray();
   return Ok(new{data=listRows,meta=new{total=rows.Length,page=1}});
  }
  if(request.Method==HttpMethod.Delete){Writes.Add((path,JsonSerializer.SerializeToElement(new{deleted=true})));if(RejectDelete)return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity){Content=JsonContent.Create(new{errors=new{title="linked"}})};entities.Remove(path);return Ok(new{meta=new{message="Success"}});}
  var body=await request.Content!.ReadFromJsonAsync<JsonElement>(ct);Writes.Add((path,body));
  if(request.Method==HttpMethod.Post)
  {
   var id=Guid.NewGuid().ToString();var name=path switch{"properties"=>"property","room_types"=>"room_type",_=>"rate_plan"};var attributes=System.Text.Json.JsonSerializer.SerializeToNode(body.GetProperty(name))!.AsObject();if(path=="properties"&&OmitContactOnCreate)attributes.Remove("email");Store(path,id,attributes);
   if(path=="room_types"&&LoseRoomResponse)throw new HttpRequestException("Simulated unknown result");return Ok(new{data=entities[path+"/"+id]});
  }
  var existing=System.Text.Json.JsonSerializer.SerializeToNode(entities[path].GetProperty("attributes"))!.AsObject();var key=path.StartsWith("properties/")?"property":"room_type";var patch=body.GetProperty(key);foreach(var field in patch.EnumerateObject()){
   var value=System.Text.Json.JsonSerializer.SerializeToNode(field.Value);
   if(field.Name=="content"&&value?["photos"] is System.Text.Json.Nodes.JsonArray changes){
    var merged=(existing["content"]?["photos"]?.DeepClone() as System.Text.Json.Nodes.JsonArray)??new();
    foreach(var photo in changes){var photoId=photo?["id"]?.GetValue<string>();var previous=photoId is null?null:merged.FirstOrDefault(p=>p?["id"]?.GetValue<string>()==photoId);if(previous is not null)merged.Remove(previous);if(photo?["is_removed"]?.GetValue<bool>()!=true)merged.Add(photo!.DeepClone());}
    value["photos"]=merged;
   }
   existing[field.Name]=value;
  }Store(path.Split('/')[0],path.Split('/')[1],existing);return Ok(new{data=entities[path]});
 }
 private static HttpResponseMessage Ok(object data)=>new(HttpStatusCode.OK){Content=JsonContent.Create(data)};
}
