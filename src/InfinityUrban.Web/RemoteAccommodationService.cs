using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;

// Business data is stored only by Channex. The journal contains IDs, hashes and delivery state only.
public sealed class RemoteCreateJob
{
    public Guid Actor{get;set;} public string Hash{get;set;}="";public string Stage{get;set;}="new";
    public Guid? RemoteId{get;set;}public Guid? RateId{get;set;}
}
public static class RemotePayloads
{
    public const string DetailsHeading="\n\nInformações do apartamento:\n";
    public static string RoomDescription(RoomInput input)=>input.Description+DetailsHeading+$"Código: {input.Code??""}\nQuartos: {input.Bedrooms}\nCamas: {input.Beds}\nBanheiros: {input.Bathrooms}";
    public static object Building(BuildingInput input,string[] photos,string? logo)=>new{property=new{title=input.Title,currency=input.Currency,timezone=input.Timezone,property_type="hotel",address=input.Address,city=input.City,state=input.State,country=input.Country,zip_code=input.ZipCode,email=input.Email,phone=input.Phone,logo_url=logo,facilities=input.Facilities??[],content=new{description=input.Description,important_information=input.ImportantInformation??"",photos=photos.Select((url,index)=>new{url,position=index,kind="photo",description=input.Title,author="Urban Infinity"})},settings=new{min_stay_type="both",state_length=500,allow_availability_autoupdate_on_modification=false,allow_availability_autoupdate_on_cancellation=false}}};
    public static object Room(Guid propertyId,RoomInput input,string[] photos)=>new{room_type=new{property_id=propertyId,title=input.Title,count_of_rooms=1,occ_adults=input.Guests,occ_children=0,occ_infants=0,default_occupancy=input.Guests,room_kind="room",facilities=input.Facilities,content=new{description=RoomDescription(input),photos=photos.Select((url,index)=>new{url,position=index,kind="photo",description=input.Title,author="Urban Infinity"})}}};
    public static object Rate(Guid propertyId,Guid roomId,string currency,RoomInput input)=>new{rate_plan=new{property_id=propertyId,room_type_id=roomId,title="Tarifa padrão",currency,sell_mode="per_room",rate_mode="manual",options=new[]{new{occupancy=input.Guests,is_primary=true,rate=input.BasePrice.ToString("0.00",CultureInfo.InvariantCulture)}}}};
}
public sealed class RemoteAccommodationService(ChannexClient approved,ChannexContentClient media,CatalogFiles files,IOptions<ChannexOptions> options)
{
    private readonly SemaphoreSlim readSlots=new(4);
    private async Task<IReadOnlyList<JsonElement>> Collection(string path,CancellationToken ct)
    {await readSlots.WaitAsync(ct);try{return await approved.FetchCollectionAsync(path,ct);}finally{readSlots.Release();}}
    private async Task<JsonElement[]> HydratedCollection(string path,CancellationToken ct)=>await Details(path,await Collection(path,ct),ct);
    public static string? Text(JsonElement item,string name)=>item.ValueKind==JsonValueKind.Object&&item.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    public static JsonElement Attributes(JsonElement item)=>item.ValueKind==JsonValueKind.Object&&item.TryGetProperty("attributes",out var a)?a:default;
    public static string? Parent(JsonElement item,string name)
    {
        if(Text(Attributes(item),name+"_id") is {} direct)return direct;
        return item.TryGetProperty("relationships",out var rel)&&rel.TryGetProperty(name,out var r)&&r.TryGetProperty("data",out var d)?Text(d,"id"):null;
    }
    private static int? Number(JsonElement a,string field)=>a.ValueKind==JsonValueKind.Object&&a.TryGetProperty(field,out var n)&&n.ValueKind==JsonValueKind.Number&&n.TryGetInt32(out var value)?value:null;
    public static string? Description(JsonElement a)=>a.ValueKind==JsonValueKind.Object&&a.TryGetProperty("content",out var c)?Text(c,"description"):null;
    public static JsonElement[] Photos(JsonElement a)=>a.ValueKind==JsonValueKind.Object&&a.TryGetProperty("content",out var c)&&c.TryGetProperty("photos",out var p)&&p.ValueKind==JsonValueKind.Array?p.EnumerateArray().OrderBy(p=>Number(p,"position")??0).ToArray():[];
    public static string[] FacilityIds(JsonElement resource)
    {
        JsonElement list=default;
        if(resource.TryGetProperty("relationships",out var rel)&&rel.TryGetProperty("facilities",out var facility)&&facility.TryGetProperty("data",out var data))list=data;
        else if(Attributes(resource).TryGetProperty("facilities",out var attributes))list=attributes;
        return list.ValueKind==JsonValueKind.Array?list.EnumerateArray().Select(f=>f.ValueKind==JsonValueKind.String?f.GetString():Text(f,"id")).Where(id=>id is not null).Cast<string>().Order().ToArray():[];
    }
    public static decimal? Price(JsonElement rate)
    {
        var a=Attributes(rate);if(!a.TryGetProperty("options",out var options)||options.ValueKind!=JsonValueKind.Array)return null;
        foreach(var option in options.EnumerateArray())if(option.TryGetProperty("is_primary",out var primary)&&primary.ValueKind==JsonValueKind.True&&option.TryGetProperty("rate",out var value))
        {if(value.ValueKind==JsonValueKind.Number&&value.TryGetDecimal(out var numeric))return numeric;if(value.ValueKind==JsonValueKind.String&&decimal.TryParse(value.GetString(),NumberStyles.Number,CultureInfo.InvariantCulture,out var text))return text;}
        return null;
    }
    public async Task<JsonElement> Read(string resource,Guid id,CancellationToken ct)
    {
        await readSlots.WaitAsync(ct);
        try{var root=await approved.ReadStagingJsonAsync($"{resource}/{id:D}",ct);var data=root.GetProperty("data");
        if(Text(data,"id")!=id.ToString())throw new PortalError(502,"A Channex retornou um cadastro diferente do solicitado.");return data;}finally{readSlots.Release();}
    }
    private async Task<JsonElement[]> Details(string resource,IReadOnlyList<JsonElement> collection,CancellationToken ct)
    {
        // Collection endpoints omit content on production. Hydrate authoritative details with bounded concurrency.
        return await Task.WhenAll(collection.Select(item=>Read(resource,Guid.Parse(Text(item,"id")!),ct)));
    }
    public async Task<object> ExpenseCatalog(CancellationToken ct)
    {
        var propertiesTask=Collection("properties",ct);var roomsTask=Collection("room_types",ct);
        await Task.WhenAll(propertiesTask,roomsTask);var properties=await propertiesTask;var rooms=await roomsTask;
        return new{properties=properties.Select(p=>new{id=Text(p,"id"),title=Text(Attributes(p),"title"),currency=Text(Attributes(p),"currency")}).ToArray(),rooms=rooms.Select(r=>new{id=Text(r,"id"),title=Text(Attributes(r),"title"),propertyId=Parent(r,"property")}).ToArray()};
    }
    public async Task<object> List(CancellationToken ct,IReadOnlyDictionary<Guid,Guid>? scope=null)
    {
        // No SQLite/catalog fallback: every displayed field comes from the remote API.
        JsonElement[] properties,rooms;IReadOnlyList<JsonElement> rates;
        if(scope?.Count==0)return new{properties=Array.Empty<object>(),rooms=Array.Empty<object>(),environment=options.Value.EnvironmentName,fetchedAt=DateTimeOffset.UtcNow,remoteError=(string?)null};
        var facilitiesTask=Collection("room_facilities/options",ct);
        var propertyFacilitiesTask=scope is null?Collection("property_facilities/options",ct):Task.FromResult<IReadOnlyList<JsonElement>>(Array.Empty<JsonElement>());
        if(scope is not null)
        {
            if(scope.Count==0)return new{properties=Array.Empty<object>(),rooms=Array.Empty<object>(),environment=options.Value.EnvironmentName,fetchedAt=DateTimeOffset.UtcNow,remoteError=(string?)null};
            var selected=await Task.WhenAll(scope.Select(async pair=>
            {
                try{var room=await Read("room_types",pair.Key,ct);return Parent(room,"property")==pair.Value.ToString()?(JsonElement?)room:null;}
                catch(HttpRequestException error)when(error.StatusCode==System.Net.HttpStatusCode.NotFound){return null;}
            }));
            rooms=selected.Where(r=>r.HasValue).Select(r=>r!.Value).ToArray();var parents=rooms.Select(r=>Parent(r,"property")!).Distinct().ToArray();
            var propertyTask=Details("properties",parents.Select(id=>JsonSerializer.SerializeToElement(new{id})).ToArray(),ct);
            var allowedRooms=rooms.Select(r=>Text(r,"id")).ToHashSet();
            var rateTasks=parents.Select(async propertyId=>(await Collection($"rate_plans?filter[property_id]={propertyId}",ct)).Where(rate=>Parent(rate,"property")==propertyId&&allowedRooms.Contains(Parent(rate,"room_type"))).ToArray()).ToArray();
            var rateTask=Task.WhenAll(rateTasks);await Task.WhenAll(propertyTask,rateTask);
            properties=await propertyTask;rates=(await rateTask).SelectMany(r=>r).ToArray();
        }
        else
        {
            var propertyTask=HydratedCollection("properties",ct);var roomTask=HydratedCollection("room_types",ct);var rateTask=Collection("rate_plans",ct);
            await Task.WhenAll(propertyTask,roomTask,rateTask);properties=await propertyTask;rooms=await roomTask;rates=await rateTask;
        }
        await Task.WhenAll(facilitiesTask,propertyFacilitiesTask);var facilities=await facilitiesTask;var propertyFacilities=await propertyFacilitiesTask;
        var buildingViews=properties.Select(p=>{var a=Attributes(p);var id=Text(p,"id");return new{id,localId=(Guid?)null,channexId=id,title=Text(a,"title"),currency=Text(a,"currency"),timezone=Text(a,"timezone"),address=Text(a,"address"),city=Text(a,"city"),state=Text(a,"state"),country=Text(a,"country"),zipCode=Text(a,"zip_code"),email=scope is null?Text(a,"email"):null,phone=scope is null?Text(a,"phone"):null,description=scope is null?Description(a):null,importantInformation=scope is null&&a.TryGetProperty("content",out var content)?Text(content,"important_information"):null,logoUrl=scope is null?Text(a,"logo_url"):null,facilities=(scope is null?FacilityIds(p):[]).Select(id=>new{id,title=propertyFacilities.Where(f=>Text(f,"id")==id).Select(f=>Text(Attributes(f),"title")).FirstOrDefault()??id}).ToArray(),photos=(scope is null?Photos(a):[]).Where(photo=>Uri.TryCreate(Text(photo,"url"),UriKind.Absolute,out var uri)&&uri.Scheme=="https").Select(photo=>new{id=Text(photo,"id"),url=Text(photo,"url"),description=Text(photo,"description")}).ToArray()};}).ToArray();
        var roomViews=rooms.Select(r=>{var a=Attributes(r);var id=Text(r,"id");var description=Description(a)??"";var split=description.LastIndexOf(RemotePayloads.DetailsHeading,StringComparison.Ordinal);var details=split<0?new Dictionary<string,string>():description[(split+RemotePayloads.DetailsHeading.Length)..].Split('\n').Where(l=>l.Contains(':')).Select(l=>l.Split(':',2)).GroupBy(l=>l[0].Trim()).ToDictionary(g=>g.Key,g=>g.Last()[1].Trim());int? Field(string name)=>details.TryGetValue(name,out var value)&&int.TryParse(value,out var n)?n:null;var rate=rates.FirstOrDefault(p=>Parent(p,"room_type")==id);return new{id,localId=(Guid?)null,channexId=id,propertyId=Parent(r,"property"),title=Text(a,"title"),guests=Number(a,"occ_adults"),quantity=Number(a,"count_of_rooms"),description=split<0?description:description[..split],facilities=FacilityIds(r).Select(id=>new{id,title=facilities.Where(f=>Text(f,"id")==id).Select(f=>Text(Attributes(f),"title")).FirstOrDefault()??id}).ToArray(),photos=Photos(a).Where(p=>Uri.TryCreate(Text(p,"url"),UriKind.Absolute,out var uri)&&uri.Scheme=="https").Select(p=>new{id=Text(p,"id"),url=Text(p,"url"),description=Text(p,"description")}).ToArray(),bedrooms=Field("Quartos"),beds=Field("Camas"),bathrooms=Field("Banheiros"),code=details.GetValueOrDefault("Código"),basePrice=rate.ValueKind==JsonValueKind.Object?Price(rate):null};}).ToArray();
        return new{properties=buildingViews,rooms=roomViews,environment=options.Value.EnvironmentName,fetchedAt=DateTimeOffset.UtcNow,remoteError=(string?)null};
    }
    private async Task<RemoteCreateJob> Job(string key,Guid actor,object input)
    {
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));var job=await files.Load<RemoteCreateJob>(key);
        if(job is not null&&(job.Actor!=actor||job.Hash!=hash))throw new PortalError(409,"Retome esta operação com os mesmos dados para evitar duplicações.");
        if(job?.Stage.EndsWith("-sending")==true)throw new PortalError(409,"O resultado do envio anterior é incerto. Confira o cadastro na Channex antes de repetir.");
        return job??new(){Actor=actor,Hash=hash};
    }
    private async Task<Guid> Create(string endpoint,object body,RemoteCreateJob job,string key,bool rate,CancellationToken ct)
    {
        job.Stage=endpoint+"-sending";await files.Save(key,job);ChannexResponse response;
        try{response=await approved.CreateStagingEntityAsync(endpoint,JsonSerializer.Serialize(body),ct);}
        catch{throw new PortalError(503,"A resposta da Channex se perdeu. Confira o cadastro antes de repetir para evitar duplicação.");}
        if(!response.IsSuccess)
        {
            if(response.StatusCode is >=400 and <500 && response.StatusCode!=408){job.Stage="rejected";await files.Save(key,job);}
            throw new PortalError(response.StatusCode==429?429:502,$"A Channex recusou o cadastro (HTTP {response.StatusCode}). Nenhuma confirmação de salvamento foi emitida.");
        }
        Guid id;
        try{using var doc=JsonDocument.Parse(response.Body);id=Guid.Parse(Text(doc.RootElement.GetProperty("data"),"id")!);if(id==Guid.Empty)throw new Exception();}
        catch{throw new PortalError(502,"A Channex não retornou o identificador criado. Confira o cadastro antes de repetir.");}
        if(rate)job.RateId=id;else job.RemoteId=id;job.Stage=endpoint+"-created";await files.Save(key,job);return id;
    }
    public async Task VerifyBuilding(Guid id,BuildingInput input,CancellationToken ct)
    {
        var resource=await Read("properties",id,ct);var a=Attributes(resource);
        foreach(var (name,expected) in new[]{("title",input.Title),("currency",input.Currency),("timezone",input.Timezone),("address",input.Address),("city",input.City),("state",input.State),("country",input.Country),("zip_code",input.ZipCode),("email",input.Email),("phone",input.Phone)})if(!string.Equals(Text(a,name),expected,StringComparison.OrdinalIgnoreCase))throw new PortalError(409,$"O prédio existe na Channex, mas o campo {name} ainda não foi confirmado. Não crie outro cadastro.");
        if(Description(a)!=input.Description)throw new PortalError(409,"A descrição do prédio ainda não foi confirmada na Channex.");
        var important=a.TryGetProperty("content",out var c)?Text(c,"important_information"):null;
        if((important??"")!=(input.ImportantInformation??""))throw new PortalError(409,"As informações importantes ainda não foram confirmadas na Channex.");
        if(!FacilityIds(resource).SequenceEqual((input.Facilities??[]).Select(id=>id.ToString()).Order()))throw new PortalError(409,"As comodidades do prédio ainda não foram confirmadas na Channex.");
        var photos=Photos(a);if(photos.Length!=(input.PhotoTickets?.Length??0)||photos.Any(p=>Text(p,"room_type_id") is not null||Text(p,"property_id") is {} owner&&owner!=id.ToString()))throw new PortalError(409,"A Channex ainda não confirmou todas as fotos da propriedade. Não crie outro cadastro.");
        if(!string.IsNullOrWhiteSpace(input.LogoTicket)&&(!Uri.TryCreate(Text(a,"logo_url"),UriKind.Absolute,out var logo)||logo.Scheme!="https"))throw new PortalError(409,"O logo do prédio ainda não foi confirmado na Channex.");
    }
    public async Task VerifyRoom(Guid id,Guid propertyId,RoomInput input,int photoCount,CancellationToken ct)
    {
        var data=await Read("room_types",id,ct);var a=Attributes(data);
        if(Parent(data,"property")!=propertyId.ToString()||Text(a,"title")!=input.Title||Number(a,"count_of_rooms")!=1||Number(a,"occ_adults")!=input.Guests||Description(a)!=RemotePayloads.RoomDescription(input))throw new PortalError(409,"O apartamento foi criado, mas seus dados ainda não foram confirmados na Channex. Não crie outro cadastro.");
        if(!FacilityIds(data).SequenceEqual(input.Facilities.Select(id=>id.ToString()).Order()))throw new PortalError(409,"As comodidades do apartamento ainda não foram confirmadas na Channex.");
        var photos=Photos(a);if(photos.Length!=photoCount||photos.Any(p=>Text(p,"room_type_id") is {} owner&&owner!=id.ToString()))throw new PortalError(409,"O apartamento foi criado; a Channex ainda não confirmou todas as fotos. Não crie outro cadastro.");
    }
    public async Task<object> CreateBuilding(BuildingInput input,Guid actor,CancellationToken ct)
    {
        UnifiedAccommodationService.ValidateBuilding(input);using var gate=files.Lock(input.OperationId);var key="remote-create-"+input.OperationId;var job=await Job(key,actor,input);
        if(job.RemoteId is null)
        {
            var photos=(input.PhotoTickets??[]).Select(p=>media.ResolvePhoto(p,actor)).ToArray();
            var logo=string.IsNullOrWhiteSpace(input.LogoTicket)?null:media.ResolvePhoto(input.LogoTicket,actor);
            await Create("properties",RemotePayloads.Building(input,photos,logo),job,key,false,ct);
        }
        var id=job.RemoteId!.Value;
        await VerifyBuilding(id,input,ct);job.Stage="complete";await files.Save(key,job);return new{id,channexId=id,stage="complete",confirmed=true};
    }
    public async Task<object> CreateRoom(RoomInput input,Guid actor,CancellationToken ct)
    {
        UnifiedAccommodationService.ValidateRoom(input);if(input.Code?.IndexOfAny(['\r','\n'])>=0)throw new PortalError(400,"O código do apartamento deve ocupar uma única linha.");
        var property=await Read("properties",input.PropertyId,ct);var currency=Text(Attributes(property),"currency")??throw new PortalError(502,"A Channex não informou a moeda do prédio.");
        using var gate=files.Lock(input.OperationId);var key="remote-create-"+input.OperationId;var job=await Job(key,actor,input);
        if(job.RemoteId is null){var photos=input.PhotoTickets.Select(p=>media.ResolvePhoto(p,actor)).ToArray();await Create("room_types",RemotePayloads.Room(input.PropertyId,input,photos),job,key,false,ct);}
        var roomId=job.RemoteId!.Value;
        var rateId=job.RateId??await Create("rate_plans",RemotePayloads.Rate(input.PropertyId,roomId,currency,input),job,key,true,ct);
        await VerifyRoom(roomId,input.PropertyId,input,input.PhotoTickets.Length,ct);var rate=await Read("rate_plans",rateId,ct);
        if(Parent(rate,"room_type")!=roomId.ToString()||Parent(rate,"property")!=input.PropertyId.ToString()||Price(rate)!=input.BasePrice)throw new PortalError(409,"O apartamento existe; a tarifa ainda não foi confirmada na Channex.");
        job.Stage="complete";await files.Save(key,job);return new{id=roomId,channexId=roomId,rateId,propertyId=input.PropertyId,stage="complete",confirmed=true};
    }
}
