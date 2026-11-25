using System.Net;
using System.Text.Json;
using InfinityUrban.Web;
sealed partial class FakeSupabase
{
 public static readonly Guid ExpenseCategoryId=Guid.NewGuid();public int ExpenseWrites;
 private readonly Dictionary<Guid,ExpenseRow> expenseRows=[];private readonly Dictionary<Guid,string> expenseEnvironments=[];
 private HttpResponseMessage? ExpenseRequest(string path,HttpMethod method,Dictionary<string,string> query,JsonElement input)
 {
  if(path=="/rest/v1/expense_categories"){var category=new ExpenseCategory(ExpenseCategoryId,"Limpeza");return Ok(query.TryGetValue("id",out var id)&&id!="eq."+ExpenseCategoryId?Array.Empty<ExpenseCategory>():Offset(query)>0?Array.Empty<ExpenseCategory>():new[]{category});}
  if(path!="/rest/v1/urban_channex_expenses")return null;
  if(method==HttpMethod.Post){ExpenseWrites++;var id=input.GetProperty("id").GetGuid();if(expenseRows.ContainsKey(id))return Ok(Array.Empty<object>());var row=ExpenseFrom(input,id,Guid.NewGuid());expenseRows[id]=row;expenseEnvironments[id]=input.GetProperty("environment").GetString()!;return Ok(new[]{row});}
  var rows=expenseRows.Values.Where(r=>query.GetValueOrDefault("environment")=="eq."+expenseEnvironments[r.Id]).AsEnumerable();if(query.TryGetValue("id",out var selected))rows=rows.Where(r=>"eq."+r.Id==selected);if(query.TryGetValue("revision",out var revision))rows=rows.Where(r=>"eq."+r.Revision==revision);
  if(method==HttpMethod.Patch){ExpenseWrites++;var old=rows.SingleOrDefault();if(old is null)return Ok(Array.Empty<object>());var row=ExpenseFrom(input,old.Id,input.GetProperty("revision").GetGuid());expenseRows[old.Id]=row;return Ok(new[]{row});}
  if(method==HttpMethod.Delete){ExpenseWrites++;var old=rows.SingleOrDefault();if(old is null)return Ok(Array.Empty<object>());expenseRows.Remove(old.Id);expenseEnvironments.Remove(old.Id);return Ok(new[]{old});}
  return Ok(rows.OrderBy(r=>r.Name).Skip(Offset(query)).Take(2));
 }
 private static ExpenseRow ExpenseFrom(JsonElement input,Guid id,Guid revision)
 {
  Guid? Id(string name)=>input.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetGuid():null;
  return new(id,input.GetProperty("name").GetString()!,input.GetProperty("kind").GetString()!,input.GetProperty("amount_cents").GetInt64(),input.GetProperty("currency").GetString()!,Id("category_id"),Id("room_type_id"),Id("property_id"),DateOnly.Parse(input.GetProperty("starts_on").GetString()!),Optional(input,"ends_on") is {} end?DateOnly.Parse(end):null,Optional(input,"description"),revision);
 }
}
