using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;

public sealed record ChannelMappingInput(string ListingId,Guid RatePlanId,string ExpectedRevision,bool Confirm);
public sealed record MappingDelivery(Guid Actor,Guid RatePlanId,string State);

// A separate API extension. The certified ChannexClient is used unchanged for reads.
public sealed class ChannelMappingService(ChannexClient approved,ChannexMappingWriter writer,CatalogFiles files)
{
    private static JsonElement A(JsonElement value)=>RemoteAccommodationService.Attributes(value);
    private static string? T(JsonElement value,string field)=>RemoteAccommodationService.Text(value,field);
    private static JsonElement[] Items(JsonElement value,string field)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(field,out var list)&&list.ValueKind==JsonValueKind.Array?list.EnumerateArray().ToArray():[];
    private static string? Value(JsonElement value,string field)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(field,out var v)?v.ValueKind==JsonValueKind.String?v.GetString():v.ValueKind==JsonValueKind.Number?v.GetRawText():null:null;
    private static bool Airbnb(JsonElement value)=>string.Equals(T(A(value),"channel"),"AirBNB",StringComparison.OrdinalIgnoreCase);
    private static JsonElement[] Mappings(JsonElement channel)=>Items(A(channel),"rate_plans");
    private static JsonElement Find(JsonElement channel,string listing)=>Mappings(channel).FirstOrDefault(m=>m.TryGetProperty("settings",out var settings)&&Value(settings,"listing_id")==listing);
    private static string Revision(JsonElement channel)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{updated=T(A(channel),"updated_at"),properties=Items(A(channel),"properties"),mappings=Mappings(channel)}))));
    private static Guid Operation(Guid channel,string listing)=>new(SHA256.HashData(Encoding.UTF8.GetBytes($"airbnb-mapping:{channel:D}:{listing}"))[..16]);
    private static object Channel(JsonElement value)=>new{id=T(value,"id"),title=T(A(value),"title"),active=A(value).TryGetProperty("is_active",out var active)&&active.ValueKind==JsonValueKind.True};
    public async Task<object> List(CancellationToken ct)=>new{channels=(await approved.FetchCollectionAsync("channels",ct)).Where(Airbnb).Select(Channel).ToArray(),fetchedAt=DateTimeOffset.UtcNow};
    private async Task<JsonElement> ReadChannel(Guid id,CancellationToken ct)
    {
        var channel=(await approved.ReadStagingJsonAsync($"channels/{id:D}",ct)).GetProperty("data");
        if(T(channel,"id")!=id.ToString()||!Airbnb(channel))throw new PortalError(400,"Selecione uma conexão Airbnb disponível na Channex.");
        return channel;
    }
    private async Task<JsonElement[]> Listings(Guid id,CancellationToken ct)
    {
        var root=await approved.ReadStagingJsonAsync($"channels/{id:D}/action/listings",ct);
        if(!root.TryGetProperty("data",out var data)||!data.TryGetProperty("listing_id_dictionary",out var dictionary)||!dictionary.TryGetProperty("values",out var values)||values.ValueKind!=JsonValueKind.Array)throw new PortalError(502,"A Channex não confirmou a lista de anúncios Airbnb. Atualize antes de continuar.");
        return values.EnumerateArray().ToArray();
    }
    public async Task<object> View(Guid id,CancellationToken ct)
    {
        var channel=await ReadChannel(id,ct);
        var listingsTask=Listings(id,ct);var propertiesTask=approved.FetchCollectionAsync("properties",ct);var roomsTask=approved.FetchCollectionAsync("room_types",ct);var ratesTask=approved.FetchCollectionAsync("rate_plans",ct);
        await Task.WhenAll(listingsTask,propertiesTask,roomsTask,ratesTask);
        var allowed=Items(A(channel),"properties").Select(v=>v.GetString()).ToHashSet();
        var properties=(await propertiesTask).Where(p=>allowed.Contains(T(p,"id"))).ToArray();
        var rooms=(await roomsTask).Where(r=>allowed.Contains(RemoteAccommodationService.Parent(r,"property"))).ToArray();var roomIds=rooms.Select(r=>T(r,"id")).ToHashSet();
        var rates=(await ratesTask).Where(r=>allowed.Contains(RemoteAccommodationService.Parent(r,"property"))&&roomIds.Contains(RemoteAccommodationService.Parent(r,"room_type"))).ToArray();
        var listings=new List<object>();
        foreach(var listing in await listingsTask)
        {
            var listingId=Value(listing,"id")!;var mapped=Find(channel,listingId);var delivery=await files.Load<MappingDelivery>("airbnb-map-"+Operation(id,listingId));
            listings.Add(new{id=listingId,title=T(listing,"title"),city=T(listing,"city"),mappingId=T(mapped,"id"),ratePlanId=Value(mapped,"rate_plan_id"),pending=mapped.ValueKind!=JsonValueKind.Object&&delivery?.State is "sending" or "pending" or "uncertain"});
        }
        return new{channel=Channel(channel),revision=Revision(channel),listings,properties=properties.Select(p=>new{id=T(p,"id"),title=T(A(p),"title"),currency=T(A(p),"currency")}),rooms=rooms.Select(r=>new{id=T(r,"id"),title=T(A(r),"title"),propertyId=RemoteAccommodationService.Parent(r,"property")}),rates=rates.Select(r=>new{id=T(r,"id"),title=T(A(r),"title"),propertyId=RemoteAccommodationService.Parent(r,"property"),roomTypeId=RemoteAccommodationService.Parent(r,"room_type")}),fetchedAt=DateTimeOffset.UtcNow};
    }
    public async Task<object> Map(Guid channelId,ChannelMappingInput input,Guid actor,CancellationToken ct)
    {
        if(!input.Confirm||input.RatePlanId==Guid.Empty||string.IsNullOrWhiteSpace(input.ExpectedRevision)||string.IsNullOrWhiteSpace(input.ListingId)||input.ListingId.Length>30||input.ListingId.Any(c=>c<'0'||c>'9'))throw new PortalError(400,"Selecione anúncio, apartamento e tarifa e confirme o envio.");
        var operation=Operation(channelId,input.ListingId);using var operationLock=files.Lock(operation);var key="airbnb-map-"+operation;
        var channel=await ReadChannel(channelId,ct);var existing=Find(channel,input.ListingId);
        if(existing.ValueKind==JsonValueKind.Object)
        {
            if(Value(existing,"rate_plan_id")==input.RatePlanId.ToString())return new{saved=true,pending=false};
            throw new PortalError(409,"Este anúncio já está vinculado a outra tarifa. Atualize a lista para conferir o vínculo existente.");
        }
        if(Revision(channel)!=input.ExpectedRevision)throw new PortalError(409,"A conexão foi alterada. Atualize e confira os vínculos antes de salvar.");
        var delivery=await files.Load<MappingDelivery>(key);
        if(delivery?.State is "sending" or "pending" or "uncertain")throw new PortalError(409,"Já houve um envio deste anúncio. Atualize e confira o resultado na Channex antes de repetir.");
        if(!(await Listings(channelId,ct)).Any(l=>Value(l,"id")==input.ListingId))throw new PortalError(400,"O anúncio não pertence a esta conexão Airbnb.");
        var rate=(await approved.ReadStagingJsonAsync($"rate_plans/{input.RatePlanId:D}",ct)).GetProperty("data");
        var propertyId=RemoteAccommodationService.Parent(rate,"property");var roomId=RemoteAccommodationService.Parent(rate,"room_type");
        if(T(rate,"id")!=input.RatePlanId.ToString()||!Guid.TryParse(roomId,out var roomGuid)||!Items(A(channel),"properties").Any(p=>p.GetString()==propertyId))throw new PortalError(400,"A tarifa precisa pertencer a um prédio desta conexão.");
        var room=(await approved.ReadStagingJsonAsync($"room_types/{roomGuid:D}",ct)).GetProperty("data");
        if(T(room,"id")!=roomId||RemoteAccommodationService.Parent(room,"property")!=propertyId)throw new PortalError(400,"O apartamento não corresponde ao prédio da tarifa.");
        await files.Save(key,new MappingDelivery(actor,input.RatePlanId,"sending"));
        try{await writer.Create(channelId,input.ListingId,input.RatePlanId,ct);}
        catch(PortalError e)when(e.Status is 400 or 403 or 404 or 422 or 429){await files.Save(key,new MappingDelivery(actor,input.RatePlanId,"rejected"));throw;}
        catch{await files.Save(key,new MappingDelivery(actor,input.RatePlanId,"uncertain"));throw new PortalError(503,"O envio pode ter sido recebido. Atualize e confira na Channex antes de tentar novamente.");}
        await files.Save(key,new MappingDelivery(actor,input.RatePlanId,"pending"));
        try
        {
            var current=Find(await ReadChannel(channelId,ct),input.ListingId);
            if(Value(current,"rate_plan_id")==input.RatePlanId.ToString()){await files.Save(key,new MappingDelivery(actor,input.RatePlanId,"confirmed"));return new{saved=true,pending=false};}
        }
        catch(Exception e)when(e is HttpRequestException or TaskCanceledException or PortalError){}
        return new{saved=false,pending=true};
    }
}
public sealed class ChannexMappingWriter(HttpClient http,IOptions<ChannexOptions> options)
{
    public async Task UpdateProperties(Guid id,string adapter,string title,Guid[] properties,CancellationToken ct)
    {
        if(!options.Value.Enabled||string.IsNullOrWhiteSpace(options.Value.ApiKey))throw new PortalError(503,"A conexão Channex está indisponível.");
        using var request=new HttpRequestMessage(HttpMethod.Put,new Uri(options.Value.ApiBaseUri,$"channels/{id:D}"));request.Headers.Add("user-api-key",options.Value.ApiKey);
        request.Content=JsonContent.Create(new{channel=new{channel=adapter,title,properties}});using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)throw new PortalError(response.StatusCode==System.Net.HttpStatusCode.TooManyRequests?429:502,"A Channex não confirmou as propriedades do canal. Confira os vínculos existentes e atualize antes de repetir.");
    }
    public async Task Create(Guid channel,string listing,Guid rate,CancellationToken ct)
    {
        if(!options.Value.Enabled||string.IsNullOrWhiteSpace(options.Value.ApiKey))throw new PortalError(503,"A conexão Channex está indisponível.");
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(options.Value.ApiBaseUri,$"channels/{channel:D}/mappings"));
        request.Headers.Add("user-api-key",options.Value.ApiKey);
        request.Content=JsonContent.Create(new{mapping=new{rate_plan_id=rate,settings=new{listing_id=listing}}});
        using var response=await http.SendAsync(request,ct);
        if(response.IsSuccessStatusCode)return;
        var status=(int)response.StatusCode;
        throw new PortalError(status is 400 or 403 or 404 or 422 or 429?status:502,"A Channex não confirmou o mapeamento. Confira as permissões da conexão e se o anúncio já está vinculado antes de repetir.");
    }
}
