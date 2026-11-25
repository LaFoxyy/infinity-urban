using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Stayline;

public class Property
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "hotel";
    public string Currency { get; set; } = "USD";
    public string TimeZoneId { get; set; } = "America/Sao_Paulo";
    public string? ChannexId { get; set; }
}
public class RoomType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public int Occupancy { get; set; } = 2;
    public string? ChannexId { get; set; }
}
public class RatePlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RoomTypeId { get; set; }
    public string Name { get; set; } = "";
    public decimal BasePrice { get; set; }
    public string? ChannexId { get; set; }
}
public class DailyRate
{
    public long Id { get; set; }
    public Guid RatePlanId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Rate { get; set; }
    public int MinStayArrival { get; set; } = 1;
    public int MinStayThrough { get; set; } = 1;
    public int MaxStay { get; set; }
    public bool StopSell { get; set; }
    public bool ClosedToArrival { get; set; }
    public bool ClosedToDeparture { get; set; }
}
public class DailyInventory
{
    public long Id { get; set; }
    public Guid RoomTypeId { get; set; }
    public DateOnly Date { get; set; }
    public int Capacity { get; set; }
}
public class Reservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string GuestName { get; set; } = "";
    public string Source { get; set; } = "direct";
    public string? ExternalBookingId { get; set; }
    public DateTimeOffset? ExternalUpdatedAt { get; set; }
    public string Status { get; set; } = "confirmed";
    public DateOnly Arrival { get; set; }
    public DateOnly Departure { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReservationStay> Stays { get; set; } = [];
}
public class ReservationStay
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReservationId { get; set; }
    public Guid RoomTypeId { get; set; }
    public Guid RatePlanId { get; set; }
    public int Quantity { get; set; } = 1;
    public DateOnly? Arrival { get; set; }
    public DateOnly? Departure { get; set; }
}
public class OutboxChange
{
    public long Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public DateOnly Date { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    // Zero keeps legacy queued changes/full synchronization as complete state.
    public int ChangedFields { get; set; }
}
public class SyncAttempt
{
    public long Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Endpoint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "";
    public int? HttpStatus { get; set; }
    public string TaskIds { get; set; } = "[]";
    public int ChangeCount { get; set; }
    public string Detail { get; set; } = "";
    public string RequestJson { get; set; } = "";
}
public class SyncGate
{
    public long Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Failures { get; set; }
}
public class BookingReceipt
{
    public string Id { get; set; } = "";
    public Guid? PropertyId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? AckedAt { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ExternalBookingId { get; set; }
    public string? RevisionStatus { get; set; }
    public string Source { get; set; } = "legacy";
    [JsonIgnore]
    public string? PayloadJson { get; set; }
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<RoomType> RoomTypes => Set<RoomType>();
    public DbSet<RatePlan> RatePlans => Set<RatePlan>();
    public DbSet<DailyRate> DailyRates => Set<DailyRate>();
    public DbSet<DailyInventory> DailyInventories => Set<DailyInventory>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationStay> ReservationStays => Set<ReservationStay>();
    public DbSet<OutboxChange> Outbox => Set<OutboxChange>();
    public DbSet<SyncAttempt> SyncAttempts => Set<SyncAttempt>();
    public DbSet<SyncGate> SyncGates => Set<SyncGate>();
    public DbSet<BookingReceipt> BookingReceipts => Set<BookingReceipt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Property>().HasIndex(x => x.ChannexId).IsUnique();
        b.Entity<RoomType>().HasIndex(x => x.ChannexId).IsUnique();
        b.Entity<RatePlan>().HasIndex(x => x.ChannexId).IsUnique();
        b.Entity<RoomType>().HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId);
        b.Entity<RatePlan>().HasOne<RoomType>().WithMany().HasForeignKey(x => x.RoomTypeId);
        b.Entity<DailyRate>().HasIndex(x => new { x.RatePlanId, x.Date }).IsUnique();
        b.Entity<DailyInventory>().HasIndex(x => new { x.RoomTypeId, x.Date }).IsUnique();
        b.Entity<DailyRate>().HasOne<RatePlan>().WithMany().HasForeignKey(x => x.RatePlanId);
        b.Entity<DailyInventory>().HasOne<RoomType>().WithMany().HasForeignKey(x => x.RoomTypeId);
        b.Entity<Reservation>().HasIndex(x => x.ExternalBookingId).IsUnique();
        b.Entity<Reservation>().HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId);
        b.Entity<ReservationStay>().HasOne<Reservation>().WithMany(x => x.Stays).HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ReservationStay>().HasOne<RoomType>().WithMany().HasForeignKey(x => x.RoomTypeId);
        b.Entity<ReservationStay>().HasOne<RatePlan>().WithMany().HasForeignKey(x => x.RatePlanId);
        b.Entity<SyncGate>().HasIndex(x => new { x.PropertyId, x.Kind }).IsUnique();
        b.Entity<OutboxChange>().HasIndex(x => new { x.PropertyId, x.Kind, x.ProcessedAt });
        b.Entity<OutboxChange>().Property(x => x.ChangedFields).HasDefaultValue(0);
        b.Entity<BookingReceipt>().Property(x => x.Source).HasDefaultValue("legacy");
        // SQLite cannot compare DateTimeOffset natively. UTC ticks preserve ordering.
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var p in entity.GetProperties())
        {
            if (p.ClrType == typeof(DateTimeOffset))
                p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero)));
            if (p.ClrType == typeof(DateTimeOffset?))
                p.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(v => v.HasValue ? v.Value.UtcTicks : null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null));
        }
    }
}

public class MutationLock { public SemaphoreSlim Gate { get; } = new(1, 1); }
public class ChannexOptions
{
    public bool Enabled { get; set; }
    public string Environment { get; set; } = "staging";
    public string ApiKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public int BatchSeconds { get; set; } = 15;
    public const string BaseUrl = "https://staging.channex.io/api/v1/";
    public const string ProductionBaseUrl = "https://app.channex.io/api/v1/";

    public string EnvironmentName => Environment?.Trim().ToLowerInvariant() switch
    {
        "staging" => "staging",
        "production" => "production",
        _ => throw new InvalidOperationException("Channex:Environment must be staging or production.")
    };

    public Uri ApiBaseUri => new(EnvironmentName == "production" ? ProductionBaseUrl : BaseUrl);
}
