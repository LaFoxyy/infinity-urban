using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using InfinityUrban.Web;
using Stayline;
static class ProductionRepair
{
 public static async Task Run(string root,ChannexClient approved,ChannexOptions options)
 {
  if(options.EnvironmentName!="production")throw new Exception("Production-only repair.");
  using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};
  var settings=Options.Create(options);var content=new ChannexContentClient(http,settings,new EphemeralDataProtectionProvider());var remote=new RemoteAccommodationService(approved,content,null!,settings);
  var database=Path.Combine(root,".local","runtime","production","stayline.db");
  using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly}.ToString());await db.OpenAsync();
  var buildings=new Dictionary<Guid,Guid>();var rooms=new Dictionary<Guid,(Guid Parent,Guid Remote)>();
  using(var cmd=db.CreateCommand()){cmd.CommandText="SELECT Id,ChannexId FROM Properties WHERE ChannexId IS NOT NULL";using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())buildings[Guid.Parse(reader.GetString(0))]=Guid.Parse(reader.GetString(1));}
  using(var cmd=db.CreateCommand()){cmd.CommandText="SELECT Id,PropertyId,ChannexId FROM RoomTypes WHERE ChannexId IS NOT NULL";using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())rooms[Guid.Parse(reader.GetString(0))]=(Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)));}
  var catalog=Path.Combine(root,".local","catalog","production");var jsonOptions=JsonSerializerOptions.Web;var failed=0;
  foreach(var file in Directory.EnumerateFiles(catalog,"building-*.json"))
  {
   var localId=Guid.Parse(Path.GetFileNameWithoutExtension(file)[9..]);if(!buildings.TryGetValue(localId,out var id))continue;
   var saved=JsonSerializer.Deserialize<BuildingContent>(await File.ReadAllTextAsync(file),jsonOptions)!;
   try
   {
    var a=RemoteAccommodationService.Attributes(await remote.Read("properties",id,default));
    foreach(var (field,expected) in new[]{("address",saved.Input.Address),("city",saved.Input.City),("state",saved.Input.State),("country",saved.Input.Country),("zip_code",saved.Input.ZipCode),("email",saved.Input.Email),("phone",saved.Input.Phone)})
    {var current=RemoteAccommodationService.Text(a,field);if(!string.IsNullOrWhiteSpace(current)&&!string.Equals(current,expected,StringComparison.OrdinalIgnoreCase))throw new Exception("Existing remote field differs: "+field);}
    var description=RemoteAccommodationService.Description(a);if(!string.IsNullOrWhiteSpace(description)&&description!=saved.Input.Description)throw new Exception("Existing remote description differs");
    bool confirmed;try{await remote.VerifyBuilding(id,saved.Input,default);confirmed=true;}catch(PortalError){confirmed=false;}
    if(!confirmed){await content.PutBuilding(id,saved.Input,default);await remote.VerifyBuilding(id,saved.Input,default);}
    File.Delete(file);Console.WriteLine($"Property {id}: address/contact/description verified in Channex; legacy supplemental file removed.");
   }
   catch(Exception e){failed++;Console.WriteLine($"Property {id}: repair incomplete: {e.Message}");}
  }
  foreach(var file in Directory.EnumerateFiles(catalog,"room-*.json"))
  {
   var localId=Guid.Parse(Path.GetFileNameWithoutExtension(file)[5..]);if(!rooms.TryGetValue(localId,out var mapping)||!buildings.TryGetValue(mapping.Parent,out var parent))continue;
   var saved=JsonSerializer.Deserialize<RoomContent>(await File.ReadAllTextAsync(file),jsonOptions)!;var id=mapping.Remote;
   try
   {
    var current=await remote.Read("room_types",id,default);if(RemoteAccommodationService.Parent(current,"property")!=parent.ToString())throw new Exception("Room parent does not match");
    var a=RemoteAccommodationService.Attributes(current);var photos=RemoteAccommodationService.Photos(a);var description=RemoteAccommodationService.Description(a);
    if(!string.IsNullOrWhiteSpace(description)&&description!=saved.Input.Description&&description!=RemotePayloads.RoomDescription(saved.Input))throw new Exception("Existing remote description differs");
    var expectedDescription=RemotePayloads.RoomDescription(saved.Input);
    if(photos.Length!=0)
    {
     // Never append a second gallery to a nonempty room. Verify the already saved result instead.
     await remote.VerifyRoom(id,parent,saved.Input,saved.Photos.Length,default);
    }
    else
    {
     await content.PutRoom(id,saved.Input,saved.Photos,default);
     await remote.VerifyRoom(id,parent,saved.Input,saved.Photos.Length,default);
    }
    var rates=await approved.FetchCollectionAsync($"rate_plans?filter[property_id]={parent:D}");var rate=rates.FirstOrDefault(r=>RemoteAccommodationService.Parent(r,"room_type")==id.ToString());
    if(rate.ValueKind!=JsonValueKind.Object||RemoteAccommodationService.Price(rate)!=saved.Input.BasePrice)throw new Exception("Remote base price has not been confirmed; source file preserved");
    File.Delete(file);Console.WriteLine($"Room {id}: description, details, price and {saved.Photos.Length} photos verified in Channex; legacy supplemental file removed.");
   }
   catch(Exception e){failed++;Console.WriteLine($"Room {id}: repair incomplete: {e.Message}");}
  }
  Console.WriteLine($"Repair finished; incomplete={failed}; no properties, rooms, prices or bookings created. No ARI sent.");if(failed>0)Environment.ExitCode=1;
 }
}
