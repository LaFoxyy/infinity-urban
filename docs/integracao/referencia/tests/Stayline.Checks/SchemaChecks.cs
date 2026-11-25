using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stayline;

namespace Stayline.Checks;

internal static class SchemaChecks
{
    public static async Task AdditiveUpgradeKeepsLegacyPendingWorkAndBookingHistory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "stayline-upgrade-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "existing.db");
        var options = new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var property = new Property { Name = "Existing property", TimeZoneId = "UTC" };
        var receiptId = Guid.NewGuid().ToString("D");
        try
        {
            await using (var db = new AppDb(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                db.Properties.Add(property);
                db.Outbox.Add(new OutboxChange { PropertyId = property.Id, Kind = "restrictions", EntityId = Guid.NewGuid(),
                    Date = PmsService.Today(property).AddDays(5) });
                db.BookingReceipts.Add(new BookingReceipt { Id = receiptId, PropertyId = property.Id, Error = "Existing mapping needs attention" });
                db.Reservations.Add(new Reservation { PropertyId = property.Id, GuestName = "Existing operational guest",
                    ExternalBookingId = Guid.NewGuid().ToString("D"), Arrival = PmsService.Today(property).AddDays(2),
                    Departure = PmsService.Today(property).AddDays(4) });
                db.SyncAttempts.Add(new SyncAttempt { PropertyId = property.Id, Endpoint = "restrictions", Status = "success", TaskIds = "[\"existing-task\"]" });
                await db.SaveChangesAsync();
                foreach (var sql in new[]
                {
                    "ALTER TABLE Outbox DROP COLUMN ChangedFields",
                    "ALTER TABLE BookingReceipts DROP COLUMN ExternalBookingId",
                    "ALTER TABLE BookingReceipts DROP COLUMN RevisionStatus",
                    "ALTER TABLE BookingReceipts DROP COLUMN Source",
                    "ALTER TABLE BookingReceipts DROP COLUMN PayloadJson"
                }) await db.Database.ExecuteSqlRawAsync(sql);
            }
            await using (var upgraded = new AppDb(options))
            {
                await AppSchema.EnsureAsync(upgraded);
                Check.Equal(1, await upgraded.Properties.CountAsync(), "Schema upgrade must preserve existing properties");
                Check.Equal(1, await upgraded.Reservations.CountAsync(), "Schema upgrade must preserve existing reservations");
                Check.Equal(1, await upgraded.SyncAttempts.CountAsync(), "Schema upgrade must preserve historical task evidence");
                var change = await upgraded.Outbox.SingleAsync();
                Check.That(change.ProcessedAt == null && change.ChangedFields == 0,
                    "Legacy pending work must remain pending and retain safe full-snapshot semantics");
                var receipt = await upgraded.BookingReceipts.SingleAsync();
                Check.Equal(receiptId, receipt.Id, "Schema upgrade must preserve the original revision ID");
                Check.That(receipt.Source == "legacy" && receipt.PayloadJson == null && receipt.RevisionStatus == null && receipt.AckedAt == null,
                    "Upgrade must not invent feed provenance or acknowledge legacy pending receipts");
                await AppSchema.EnsureAsync(upgraded);
            }
            var backups = Directory.GetFiles(Path.Combine(folder, ".schema-backups"), "*.db");
            Check.Equal(1, backups.Length, "An idempotent upgrade should create exactly one pre-upgrade backup");
            await using var backup = new SqliteConnection($"Data Source={backups.Single()};Mode=ReadOnly;Pooling=False");
            await backup.OpenAsync();
            using var count = backup.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Outbox";
            Check.Equal(1L, (long)(await count.ExecuteScalarAsync())!, "Backup must include committed legacy pending work");
            using var columns = backup.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Outbox') WHERE name='ChangedFields'";
            Check.Equal(0L, (long)(await columns.ExecuteScalarAsync())!, "Backup must precede the schema change");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
