using System.Net;
using System.Text.Json;
sealed partial class UnifiedTransport
{
 public bool NoAvailability,FailAvailability,IncompleteOperationalSnapshot;public decimal OperationalCalendarPrice=245m;public List<JsonElement> ReservationFeed=[];public List<string> ReservationAcks=[];
 public Guid OperationalProperty=Guid.NewGuid(),OperationalRoom=Guid.NewGuid(),OperationalRate=Guid.NewGuid();
 public void SeedOperationalProperty()
 {
  Store("properties",OperationalProperty.ToString(),new{title="Edifício automático",currency="BRL",timezone="America/Sao_Paulo",property_type="hotel"});
  Store("room_types",OperationalRoom.ToString(),new{title="Apartamento automático",property_id=OperationalProperty,count_of_rooms=1,occ_adults=3,room_kind="room"});
  Store("rate_plans",OperationalRate.ToString(),new{title="Tarifa automática",property_id=OperationalProperty,room_type_id=OperationalRoom,sell_mode="per_room",rate_mode="manual",currency="BRL",options=new[]{new{is_primary=true,rate="348.00"}}});
 }
 private HttpResponseMessage? ReservationRequest(HttpRequestMessage request,string path)
 {
  if(path=="availability")
  {
   if(FailAvailability)throw new HttpRequestException("Simulated availability outage");
   var query=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);var property=query["filter[property_id]"].ToString();var from=DateOnly.Parse(query["filter[date][gte]"].ToString());var to=DateOnly.Parse(query["filter[date][lte]"].ToString());
   var data=new Dictionary<string,Dictionary<string,int>>();foreach(var room in entities.Values.Where(r=>r.GetProperty("type").GetString()=="room_type"&&InfinityUrban.Web.RemoteAccommodationService.Parent(r,"property")==property)){var days=new Dictionary<string,int>();for(var d=from;d<=to;d=d.AddDays(1)){if(IncompleteOperationalSnapshot&&d>from.AddDays(2))break;days[d.ToString("yyyy-MM-dd")]=NoAvailability?0:1;}data[room.GetProperty("id").GetString()!]=days;}return Ok(new{data});
  }
  if(path=="restrictions")
  {
   var query=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);var property=query["filter[property_id]"].ToString();var from=DateOnly.Parse(query["filter[date][gte]"].ToString());var to=DateOnly.Parse(query["filter[date][lte]"].ToString());
   var data=new Dictionary<string,Dictionary<string,object>>();foreach(var rate in entities.Values.Where(r=>r.GetProperty("type").GetString()=="rate_plan"&&InfinityUrban.Web.RemoteAccommodationService.Parent(r,"property")==property)){var days=new Dictionary<string,object>();for(var d=from;d<=to;d=d.AddDays(1))days[d.ToString("yyyy-MM-dd")]=new{rate=(rate.GetProperty("id").GetString()==OperationalRate.ToString()?OperationalCalendarPrice:InfinityUrban.Web.RemoteAccommodationService.Price(rate)??200m).ToString("0.00",System.Globalization.CultureInfo.InvariantCulture),min_stay_arrival=2,min_stay_through=2,max_stay=30,stop_sell=false,closed_to_arrival=false,closed_to_departure=false};data[rate.GetProperty("id").GetString()!]=days;}return Ok(new{data});
  }
  if(path=="booking_revisions/feed")return Ok(new{data=ReservationFeed.Where(r=>!ReservationAcks.Contains(r.GetProperty("id").GetString()!)).ToArray(),meta=new{total=ReservationFeed.Count(r=>!ReservationAcks.Contains(r.GetProperty("id").GetString()!)),page=1}});
  if(path.StartsWith("booking_revisions/")&&path.EndsWith("/ack")){ReservationAcks.Add(path.Split('/')[1]);return Ok(new{meta=new{message="Success"}});}
  return null;
 }
}
