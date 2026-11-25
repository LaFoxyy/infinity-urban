using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stayline;
namespace InfinityUrban.Web;
public sealed record AccommodationDeleteInput(string ExpectedTitle,bool Confirm);
public sealed class AccommodationDeletion(RemoteAccommodationService remote,ChannexClient approved,ChannexContentClient writer,IDbContextFactory<AppDb> factory,MutationLock mutation,CatalogFiles files)
{
 public async Task<object> Delete(string resource,Guid id,AccommodationDeleteInput input,CancellationToken ct)
 {
  if(!input.Confirm||string.IsNullOrWhiteSpace(input.ExpectedTitle))throw new PortalError(400,"Confirme a exclusão informando o nome do cadastro.");
  using var gate=files.Lock(id);await mutation.Gate.WaitAsync(ct);
  try{
   var row=await remote.Read(resource,id,ct);if(RemoteAccommodationService.Text(RemoteAccommodationService.Attributes(row),"title")!=input.ExpectedTitle)throw new PortalError(409,"O cadastro mudou. Atualize antes de excluir.");
   var isProperty=resource=="properties";var propertyId=isProperty?id.ToString():RemoteAccommodationService.Parent(row,"property");
   if(isProperty&&(await approved.FetchCollectionAsync($"room_types?filter[property_id]={id:D}",ct)).Any(r=>RemoteAccommodationService.Parent(r,"property")==id.ToString()))throw new PortalError(409,"Exclua ou transfira os apartamentos deste prédio antes de removê-lo.");
   await using var db=await factory.CreateDbContextAsync(ct);
   var properties=await db.Properties.Where(p=>p.ChannexId==propertyId).ToListAsync(ct);var localIds=properties.Select(p=>p.Id).ToArray();
   var rooms=await db.RoomTypes.Where(r=>isProperty?localIds.Contains(r.PropertyId):r.ChannexId==id.ToString()).ToListAsync(ct);var roomIds=rooms.Select(r=>r.Id).ToArray();
   if(await db.ReservationStays.AnyAsync(s=>roomIds.Contains(s.RoomTypeId),ct)||isProperty&&await db.Reservations.AnyAsync(r=>localIds.Contains(r.PropertyId),ct))throw new PortalError(409,"Este cadastro tem histórico de reservas e não pode ser excluído por aqui.");
   if(await db.Outbox.AnyAsync(o=>localIds.Contains(o.PropertyId)&&o.ProcessedAt==null,ct)||await db.BookingReceipts.AnyAsync(b=>localIds.Contains(b.PropertyId??Guid.Empty)&&b.ProcessedAt==null,ct))throw new PortalError(409,"Há reservas ou sincronizações pendentes para este prédio. Aguarde o processamento antes de excluir.");
   // Standard delete deliberately has no force flag: Channex refuses linked channel inventory.
   await writer.DeleteAccommodation(resource,id,ct);
   try{await remote.Read(resource,id,ct);throw new PortalError(409,"A Channex ainda não confirmou a exclusão. Atualize a lista antes de repetir.");}catch(HttpRequestException ex)when(ex.StatusCode==HttpStatusCode.NotFound){}
   // Keep operational history, but detach remote IDs so the approved worker cannot target deleted inventory.
   var rates=await db.RatePlans.Where(r=>roomIds.Contains(r.RoomTypeId)).ToListAsync(ct);foreach(var rate in rates)rate.ChannexId=null;foreach(var room in rooms)room.ChannexId=null;if(isProperty)foreach(var property in properties)property.ChannexId=null;
   await db.SaveChangesAsync(ct);return new{id,confirmed=true};
  }finally{mutation.Gate.Release();}
 }
}
