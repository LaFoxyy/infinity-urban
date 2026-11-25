using System.Net.Mail;
using Stayline;
namespace InfinityUrban.Web;
public sealed record BuildingInput(Guid OperationId,string Title,string Currency,string Timezone,string Address,string City,string State,string Country,string ZipCode,string Email,string Phone,string Description,string? ImportantInformation=null,Guid[]? Facilities=null,string[]? PhotoTickets=null,string? LogoTicket=null);
public sealed record RoomInput(Guid OperationId,Guid PropertyId,string Title,int Guests,int Bedrooms,int Beds,int Bathrooms,decimal BasePrice,string Description,Guid[] Facilities,string[] PhotoTickets,string? Code);
public sealed class UnifiedCreateJob
{
    public Guid Id{get;set;} public Guid Actor{get;set;} public string Fingerprint{get;set;}="";public string Stage{get;set;}="new";
    public Guid? LocalId{get;set;} public Guid? RateId{get;set;}
}
public sealed class BuildingContent {public BuildingInput Input{get;set;}=null!;public string State{get;set;}="pending";}
public sealed class RoomContent {public RoomInput Input{get;set;}=null!;public string[] Photos{get;set;}=[];public string State{get;set;}="pending";}

public static class UnifiedAccommodationService
{
    public static void ValidateBuilding(BuildingInput input)
    {
        if(input.OperationId==Guid.Empty||string.IsNullOrWhiteSpace(input.Title)||input.Title.Length>255||string.IsNullOrWhiteSpace(input.Address)||input.Address.Length>255||string.IsNullOrWhiteSpace(input.City)||input.City.Length>255||input.State is null||input.State.Length>255||input.Country?.Length!=2||input.ZipCode is null||input.ZipCode.Length>32||!MailAddress.TryCreate(input.Email,out _)||input.Phone is null||input.Phone.Length>32||input.Description is null||input.Description.Length>20000)throw new PortalError(400,"Revise nome, endereço, contato e descrição do prédio.");
        if(input.ImportantInformation?.Length>20000||input.Facilities?.Length>200||input.Facilities?.Any(id=>id==Guid.Empty)==true||input.PhotoTickets?.Length>30)throw new PortalError(400,"Revise o conteúdo do prédio. A galeria da propriedade aceita até 30 fotos.");
        if(!PmsService.Currencies.Contains(input.Currency))throw new PortalError(400,"Moeda inválida.");
        try{TimeZoneInfo.FindSystemTimeZoneById(input.Timezone);}catch{throw new PortalError(400,"Fuso horário inválido.");}
    }
    public static void ValidateRoom(RoomInput input)
    {
        if(input.OperationId==Guid.Empty||input.PropertyId==Guid.Empty||string.IsNullOrWhiteSpace(input.Title)||input.Title.Length>255||input.Guests is <1 or >30||input.Bedrooms is <0 or >100||input.Beds is <0 or >100||input.Bathrooms is <0 or >100||input.BasePrice<=0||decimal.Round(input.BasePrice,2)!=input.BasePrice||input.Description is null||input.Description.Length>20000||input.Code?.Length>80||input.PhotoTickets is null||input.PhotoTickets.Length>10||input.Facilities is null||input.Facilities.Length>200||input.Facilities.Any(id=>id==Guid.Empty))throw new PortalError(400,"Revise os dados do apartamento. O limite é de 10 fotos.");
    }
}
