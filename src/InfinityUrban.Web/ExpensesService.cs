using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Stayline;
namespace InfinityUrban.Web;
public sealed record ExpenseInput(Guid Id,string Name,string Kind,long AmountCents,string Currency,Guid? CategoryId,Guid? RoomTypeId,string StartDate,string? EndDate,string? Description,Guid? ExpectedRevision=null,Guid? PropertyId=null);
public sealed record ExpenseDelete(Guid ExpectedRevision);
public sealed record ExpenseCategory(Guid Id,string Label);
public sealed record ExpenseView(Guid Id,string Name,string Kind,long AmountCents,string Currency,Guid? CategoryId,Guid? RoomTypeId,Guid? PropertyId,DateOnly StartDate,DateOnly? EndDate,string? Description,Guid Revision);
public sealed record ExpenseRow(Guid Id,string Name,string Kind,[property:JsonPropertyName("amount_cents")]long AmountCents,string Currency,[property:JsonPropertyName("category_id")]Guid? CategoryId,[property:JsonPropertyName("room_type_id")]Guid? RoomTypeId,[property:JsonPropertyName("property_id")]Guid? PropertyId,[property:JsonPropertyName("starts_on")]DateOnly StartDate,[property:JsonPropertyName("ends_on")]DateOnly? EndDate,string? Description,Guid Revision)
{public ExpenseView View()=>new(Id,Name,Kind,AmountCents,Currency,CategoryId,RoomTypeId,PropertyId,StartDate,EndDate,Description,Revision);}
public sealed class ExpensesService(SupabaseAuth auth,RemoteAccommodationService remote,IOptions<ChannexOptions> options)
{
    private const string Fields="id,name,kind,amount_cents,currency,category_id,room_type_id,property_id,starts_on,ends_on,description,revision";
    private string Rows=>$"urban_channex_expenses?environment=eq.{options.Value.EnvironmentName}&select={Fields}";
    public async Task<object> List(string token,CancellationToken ct)
    {
        var rowsTask=All<ExpenseRow>(token,Rows+"&order=name,id",ct);
        var categoriesTask=All<ExpenseCategory>(token,"expense_categories?select=id,label&order=label,id",ct);
        await Task.WhenAll(rowsTask,categoriesTask);
        return new{expenses=(await rowsTask).Select(r=>r.View()),categories=await categoriesTask,fetchedAt=DateTimeOffset.UtcNow};
    }
    public static (DateOnly Start,DateOnly? End) Validate(ExpenseInput input)
    {
        if(input.Id==Guid.Empty||string.IsNullOrWhiteSpace(input.Name)||input.Name.Trim().Length>180||input.Kind is not("monthly" or "per_stay" or "one_off")||input.AmountCents is <1 or >99999999999||!PmsService.Currencies.Contains(input.Currency)||input.CategoryId==Guid.Empty||input.RoomTypeId==Guid.Empty||input.PropertyId==Guid.Empty||input.RoomTypeId is not null&&input.PropertyId is not null||input.Description?.Length>10000)throw new PortalError(400,"Revise nome, tipo, valor, moeda e apartamento da despesa.");
        if(!DateOnly.TryParseExact(input.StartDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start)||start.Year is <2000 or >2100)throw new PortalError(400,"Informe uma data inicial válida.");
        DateOnly? end=null;
        if(!string.IsNullOrWhiteSpace(input.EndDate)){if(!DateOnly.TryParseExact(input.EndDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)||parsed.Year is <2000 or >2100)throw new PortalError(400,"Informe uma data final válida.");end=parsed;}
        if(end<start||input.Kind=="one_off"&&end is not null||input.Kind!="one_off"&&(start.Day!=1||end is {} finish&&finish.Day!=1))throw new PortalError(400,"Revise a vigência. Despesas recorrentes usam meses; a avulsa utiliza uma única data.");
        return(start,end);
    }
    public async Task<ExpenseView> Save(string token,ExpenseInput input,CancellationToken ct)
    {
        var(start,end)=Validate(input);var name=input.Name.Trim();var description=string.IsNullOrWhiteSpace(input.Description)?null:input.Description.Trim();
        if(input.CategoryId is {} category&&(await Read<ExpenseCategory>(token,$"expense_categories?select=id,label&id=eq.{category:D}&limit=1",ct)).Length!=1)throw new PortalError(400,"A categoria selecionada não está disponível.");
        Guid? propertyId=input.PropertyId;
        if(input.RoomTypeId is {} roomId)
        {
            System.Text.Json.JsonElement room;
            try{room=await remote.Read("room_types",roomId,ct);}catch(HttpRequestException e)when(e.StatusCode==HttpStatusCode.NotFound){throw new PortalError(404,"Apartamento não disponível na Channex. Selecione outro ou deixe sem apartamento.");}
            if(!Guid.TryParse(RemoteAccommodationService.Parent(room,"property"),out var parent))throw new PortalError(502,"A Channex não confirmou o prédio deste apartamento.");
            propertyId=parent;
        }
        if(propertyId is {} buildingId)
        {
            System.Text.Json.JsonElement property;
            try{property=await remote.Read("properties",buildingId,ct);}catch(HttpRequestException e)when(e.StatusCode==HttpStatusCode.NotFound){throw new PortalError(404,"Prédio não disponível na Channex.");}
            var currency=RemoteAccommodationService.Text(RemoteAccommodationService.Attributes(property),"currency");
            if(currency!=input.Currency)throw new PortalError(400,$"Use a moeda da acomodação ({currency}) para esta despesa.");
        }
        var body=new Dictionary<string,object?>{{"name",name},{"kind",input.Kind},{"amount_cents",input.AmountCents},{"currency",input.Currency},{"category_id",input.CategoryId},{"room_type_id",input.RoomTypeId},{"property_id",propertyId},{"starts_on",start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"ends_on",end?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},{"description",description}};
        ExpenseRow[] rows;
        if(input.ExpectedRevision is {} revision)
        {
            body["revision"]=Guid.NewGuid();body["updated_at"]=DateTimeOffset.UtcNow;
            rows=await Write<ExpenseRow>(token,HttpMethod.Patch,Rows+$"&id=eq.{input.Id:D}&revision=eq.{revision:D}",body,ct);if(rows.Length!=1)throw Changed();
        }
        else
        {
            body["id"]=input.Id;body["environment"]=options.Value.EnvironmentName;
            rows=await Write<ExpenseRow>(token,HttpMethod.Post,"urban_channex_expenses?on_conflict=id&select="+Fields,body,ct,"return=representation,resolution=ignore-duplicates");
            if(rows.Length==0)
            {
                rows=await Read<ExpenseRow>(token,Rows+$"&id=eq.{input.Id:D}&limit=1",ct);
                if(rows.Length!=1)throw Changed();var row=rows[0];if(row.Name!=name||row.Kind!=input.Kind||row.AmountCents!=input.AmountCents||row.Currency!=input.Currency||row.CategoryId!=input.CategoryId||row.RoomTypeId!=input.RoomTypeId||row.PropertyId!=propertyId||row.StartDate!=start||row.EndDate!=end||row.Description!=description)throw Changed();
            }
        }
        return rows.Single().View();
    }
    public async Task Delete(string token,Guid id,Guid revision,CancellationToken ct)
    {
        if(id==Guid.Empty||revision==Guid.Empty)throw new PortalError(400,"Selecione uma despesa válida.");
        var rows=await Write<ExpenseRow>(token,HttpMethod.Delete,Rows+$"&id=eq.{id:D}&revision=eq.{revision:D}",null,ct);
        if(rows.Length==0&&(await Read<ExpenseRow>(token,Rows+$"&id=eq.{id:D}&limit=1",ct)).Length>0)throw Changed();
    }
    private async Task<T[]> All<T>(string token,string path,CancellationToken ct)
    {
        var rows=new List<T>();while(rows.Count<=100000){var page=await Read<T>(token,path+$"&offset={rows.Count}&limit=500",ct);if(page.Length==0)return rows.ToArray();rows.AddRange(page);}throw new PortalError(503,"A lista de despesas excedeu o limite de consulta.");
    }
    private async Task<T[]> Read<T>(string token,string path,CancellationToken ct){using var response=await auth.Send(HttpMethod.Get,path,null,token,ct,"rest/v1/");Ensure(response);return await response.Content.ReadFromJsonAsync<T[]>(ct)??[];}
    private async Task<T[]> Write<T>(string token,HttpMethod method,string path,object? body,CancellationToken ct,string prefer="return=representation"){using var response=await auth.Send(method,path,body,token,ct,"rest/v1/",prefer);Ensure(response);return await response.Content.ReadFromJsonAsync<T[]>(ct)??[];}
    private static void Ensure(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new PortalError(403,"Você não tem permissão para acessar ou alterar estas despesas.");
        if(response.StatusCode==HttpStatusCode.Conflict)throw Changed();
        throw new PortalError(502,"O Supabase não confirmou a operação. Atualize as despesas antes de tentar novamente.");
    }
    private static PortalError Changed()=>new(409,"A despesa ou a categoria foi alterada por outra pessoa. Atualize a lista antes de continuar.");
}
