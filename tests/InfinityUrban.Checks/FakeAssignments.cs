using System.Net;
using System.Text.Json;
using InfinityUrban.Web;
sealed partial class FakeSupabase
{
 public int AssignmentWrites;public bool FailAssignments,RevokeAssignmentsAfterRead;
 private readonly List<AssignmentRow> accessAssignments=[];
 public AssignmentRow SeedAssignment(Guid user,Guid room,Guid property){var row=new AssignmentRow(Guid.NewGuid(),user,room,property,Guid.Parse(CurrentId),DateTimeOffset.UtcNow);accessAssignments.Add(row);return row;}
 public void ClearAssignments(Guid user)=>accessAssignments.RemoveAll(a=>a.UserId==user);
 private HttpResponseMessage? AssignmentRequest(string path,HttpMethod method,Dictionary<string,string> query,JsonElement input)
 {
  if(path!="/rest/v1/urban_channex_assignments")return null;
  if(FailAssignments)return new(HttpStatusCode.ServiceUnavailable);
  if(method==HttpMethod.Post)
  {
   AssignmentWrites++;
   foreach(var item in input.EnumerateArray()){if(item.GetProperty("environment").GetString()!="production")throw new Exception("Wrong environment");var user=item.GetProperty("user_id").GetGuid();var room=item.GetProperty("room_type_id").GetGuid();if(!accessAssignments.Any(a=>a.UserId==user&&a.RoomTypeId==room))SeedAssignment(user,room,item.GetProperty("property_id").GetGuid());}
   return new(HttpStatusCode.NoContent);
  }
  var rows=accessAssignments.AsEnumerable();if(query.TryGetValue("environment",out var environment)&&environment!="eq.production")return Ok(Array.Empty<object>());
  if(CurrentRole=="proprietario")rows=rows.Where(a=>a.UserId==Guid.Parse(CurrentId));
  if(query.TryGetValue("user_id",out var userId))rows=rows.Where(a=>"eq."+a.UserId==userId);if(query.TryGetValue("id",out var id))rows=rows.Where(a=>"eq."+a.Id==id);
  if(method==HttpMethod.Delete){AssignmentWrites++;var ids=rows.Select(a=>a.Id).ToHashSet();accessAssignments.RemoveAll(a=>ids.Contains(a.Id));return new(HttpStatusCode.NoContent);}
  var page=rows.OrderBy(a=>a.Id).Skip(Offset(query)).Take(2).ToArray();if(RevokeAssignmentsAfterRead&&CurrentRole=="proprietario"&&page.Length>0){ClearAssignments(Guid.Parse(CurrentId));RevokeAssignmentsAfterRead=false;}return Ok(page);
 }
}
