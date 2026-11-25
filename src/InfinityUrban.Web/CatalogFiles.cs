using System.Text.Json;
using Microsoft.Extensions.Options;
using Stayline;

namespace InfinityUrban.Web;

// Separate store: never opens or changes the certified Stayline SQLite schema.
public sealed class CatalogFiles(IWebHostEnvironment host,IOptions<ChannexOptions> options,IConfiguration config)
{
    public string DirectoryPath=>Path.Combine(config["Catalog:DataDirectory"]??Path.Combine(host.ContentRootPath,".local","catalog"),options.Value.EnvironmentName);
    public async Task<T?> Load<T>(string name)
    {
        var path=Path.Combine(DirectoryPath,name+".json");
        if(!File.Exists(path))return default;
        await using var file=File.OpenRead(path);return await JsonSerializer.DeserializeAsync<T>(file,JsonSerializerOptions.Web);
    }
    public async Task Save<T>(string name,T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var target=Path.Combine(DirectoryPath,name+".json");var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        await using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {await JsonSerializer.SerializeAsync(file,value,JsonSerializerOptions.Web);await file.FlushAsync();file.Flush(true);}
        File.Move(temp,target,true);
    }
    public FileStream Lock(Guid operation)
    {
        Directory.CreateDirectory(DirectoryPath);
        try{return new FileStream(Path.Combine(DirectoryPath,"operation-"+operation.ToString("D")+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new PortalError(409,"Este cadastro já está sendo processado. Aguarde e consulte o resultado.");}
    }
    public async Task<OnboardingJob[]> Jobs(Guid actor)
    {
        if(!Directory.Exists(DirectoryPath))return [];
        List<OnboardingJob> result=[];
        foreach(var path in Directory.EnumerateFiles(DirectoryPath,"operation-*.json"))
        {
            var job=await Load<OnboardingJob>(Path.GetFileNameWithoutExtension(path));
            if(job is not null&&job.Actor==actor)result.Add(job);
        }
        return result.OrderByDescending(job=>job.CreatedAt).Take(30).ToArray();
    }
}

public sealed record ApartmentInput(Guid OperationId,string Title,string? Code,string Currency,string Timezone,string Address,string City,string State,string Country,string ZipCode,
    string Email,string Phone,string? Latitude,string? Longitude,int Guests,int Bedrooms,int Beds,int Bathrooms,decimal BasePrice,string Description,Guid[] Facilities,string[] PhotoTickets,string Environment);
public sealed class OnboardingJob
{
    public Guid Id{get;set;} public Guid Actor{get;set;} public string Fingerprint{get;set;}="";public string Stage{get;set;}="new";
    public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow;
    public ApartmentInput Input{get;set;}=null!;public string[] Photos{get;set;}=[];
    public Guid? LocalPropertyId{get;set;}public Guid? LocalRoomId{get;set;}public Guid? LocalRateId{get;set;}public Guid? ChannexId{get;set;}
}
public sealed record ApartmentExtras(string? Code,int Bedrooms,int Beds,int Bathrooms,decimal BasePrice,Guid LocalPropertyId);
