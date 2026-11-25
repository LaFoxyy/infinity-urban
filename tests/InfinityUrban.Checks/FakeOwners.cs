using System.Net;
using System.Text.Json;
using InfinityUrban.Web;
sealed partial class FakeSupabase
{
 public int OwnerWrites;private readonly Dictionary<Guid,OwnerRow> owners=[];private readonly Dictionary<Guid,string> ownerEnvironments=[];private readonly Dictionary<Guid,RoomOwnerRow> ownerLinks=[];
 public void SeedOwners(){for(var i=0;i<5;i++){var id=Guid.NewGuid();owners[id]=new(id,"Proprietário "+i,$"owner{i}@example.com",null,Guid.NewGuid());ownerEnvironments[id]="production";}var stage=Guid.NewGuid();owners[stage]=new(stage,"STAGING OWNER MUST NOT SHOW",null,null,Guid.NewGuid());ownerEnvironments[stage]="staging";}
 private HttpResponseMessage? OwnerRequest(string path,HttpMethod method,Dictionary<string,string> query,JsonElement input)
 {
  if(path=="/rest/v1/urban_channex_owners")
  {
   var rows=owners.Values.Where(o=>query.GetValueOrDefault("environment")=="eq."+ownerEnvironments[o.Id]).AsEnumerable();if(query.TryGetValue("id",out var id))rows=rows.Where(o=>"eq."+o.Id==id);if(query.TryGetValue("revision",out var revision))rows=rows.Where(o=>"eq."+o.Revision==revision);
   if(method==HttpMethod.Post){OwnerWrites++;var ownerId=input.GetProperty("id").GetGuid();if(owners.ContainsKey(ownerId))return Ok(Array.Empty<object>());var created=new OwnerRow(ownerId,input.GetProperty("full_name").GetString()!,Optional(input,"email"),Optional(input,"phone"),Guid.NewGuid());owners[ownerId]=created;ownerEnvironments[ownerId]=input.GetProperty("environment").GetString()!;return Ok(new[]{created});}
   if(method==HttpMethod.Patch){OwnerWrites++;var old=rows.SingleOrDefault();if(old is null)return Ok(Array.Empty<object>());var changed=old with{FullName=input.GetProperty("full_name").GetString()!,Email=Optional(input,"email"),Phone=Optional(input,"phone"),Revision=input.GetProperty("revision").GetGuid()};owners[old.Id]=changed;return Ok(new[]{changed});}
   if(method==HttpMethod.Delete){OwnerWrites++;var old=rows.SingleOrDefault();if(old is null)return Ok(Array.Empty<object>());if(ownerLinks.Values.Any(l=>l.OwnerId==old.Id))return new(HttpStatusCode.Conflict);owners.Remove(old.Id);ownerEnvironments.Remove(old.Id);return Ok(new[]{old});}
   return Ok(rows.OrderBy(o=>o.FullName).Skip(Offset(query)).Take(2));
  }
  if(path=="/rest/v1/urban_channex_room_owners")
  {
   if(query.TryGetValue("environment",out var environment)&&environment!="eq.production")throw new Exception("Unexpected owner environment");
   var rows=ownerLinks.Values.AsEnumerable();if(query.TryGetValue("room_type_id",out var room))rows=rows.Where(l=>"eq."+l.RoomTypeId==room);if(query.TryGetValue("owner_id",out var owner))rows=rows.Where(l=>"eq."+l.OwnerId==owner);if(query.TryGetValue("revision",out var revision))rows=rows.Where(l=>"eq."+l.Revision==revision);
   if(method==HttpMethod.Post){OwnerWrites++;var roomId=input.GetProperty("room_type_id").GetGuid();if(ownerLinks.ContainsKey(roomId))return Ok(Array.Empty<object>());var created=new RoomOwnerRow(roomId,input.GetProperty("property_id").GetGuid(),input.GetProperty("owner_id").GetGuid(),Guid.NewGuid());ownerLinks[roomId]=created;return Ok(new[]{created});}
   if(method==HttpMethod.Patch){OwnerWrites++;var old=rows.SingleOrDefault();if(old is null)return Ok(Array.Empty<object>());var changed=old with{OwnerId=input.GetProperty("owner_id").GetGuid(),PropertyId=input.GetProperty("property_id").GetGuid(),Revision=input.GetProperty("revision").GetGuid()};ownerLinks[old.RoomTypeId]=changed;return Ok(new[]{changed});}
   if(method==HttpMethod.Delete){OwnerWrites++;var old=rows.SingleOrDefault();if(old is null)return Ok(Array.Empty<object>());ownerLinks.Remove(old.RoomTypeId);return Ok(new[]{old});}
   return Ok(rows.OrderBy(l=>l.RoomTypeId).Skip(Offset(query)).Take(2));
  }
  return null;
 }
 private static string? Optional(JsonElement input,string key)=>input.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
}
