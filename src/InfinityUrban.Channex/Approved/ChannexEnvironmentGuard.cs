using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Stayline;

/// <summary>Bind a PMS database to one Channex environment before synchronization workers start.</summary>
public static class ChannexEnvironmentGuard
{
    public static async Task EnsureAsync(AppDb db, ChannexOptions options, CancellationToken ct = default)
    {
        var requested = options.EnvironmentName;
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
            command.CommandText = "CREATE TABLE IF NOT EXISTS \"ChannexEnvironment\" (\"Id\" INTEGER NOT NULL PRIMARY KEY CHECK (\"Id\" = 1), \"Environment\" TEXT NOT NULL CHECK (\"Environment\" IN ('staging', 'production')))";
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = "SELECT \"Environment\" FROM \"ChannexEnvironment\" WHERE \"Id\" = 1";
            var bound = await command.ExecuteScalarAsync(ct) as string;
            if (bound is not null)
            {
                if (!string.Equals(bound, requested, StringComparison.Ordinal))
                    throw new InvalidOperationException($"This PMS database is bound to Channex {bound}, but {requested} was requested. Use a separate data directory; mappings and queued requests cannot be moved between environments.");
            }
            else
            {
                // Unmarked databases from the certified pre-alpha belong to staging. Never infer
                // that their UUIDs, receipts or pending tasks are safe to send to production.
                if (requested == "production")
                {
                    command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"Properties\" UNION ALL SELECT 1 FROM \"RoomTypes\" UNION ALL SELECT 1 FROM \"RatePlans\" UNION ALL SELECT 1 FROM \"DailyRates\" UNION ALL SELECT 1 FROM \"DailyInventories\" UNION ALL SELECT 1 FROM \"Reservations\" UNION ALL SELECT 1 FROM \"ReservationStays\" UNION ALL SELECT 1 FROM \"Outbox\" UNION ALL SELECT 1 FROM \"SyncAttempts\" UNION ALL SELECT 1 FROM \"SyncGates\" UNION ALL SELECT 1 FROM \"BookingReceipts\")";
                    if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0)
                        throw new InvalidOperationException("A populated legacy PMS database cannot be connected to Channex production. Start with a separate production data directory and map the production properties explicitly.");
                }
                command.CommandText = "INSERT INTO \"ChannexEnvironment\" (\"Id\", \"Environment\") VALUES (1, $environment)";
                command.Parameters.AddWithValue("$environment", requested);
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
