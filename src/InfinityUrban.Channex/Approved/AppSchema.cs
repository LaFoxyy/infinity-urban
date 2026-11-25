using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Stayline;

/// <summary>Additive upgrades for existing local databases; never recreates saved PMS data.</summary>
public static class AppSchema
{
    private static readonly (string Table, string Column, string Definition)[] Additions =
    [
        ("Outbox", "ChangedFields", "INTEGER NOT NULL DEFAULT 0"),
        ("BookingReceipts", "ExternalBookingId", "TEXT NULL"),
        ("BookingReceipts", "RevisionStatus", "TEXT NULL"),
        ("BookingReceipts", "Source", "TEXT NOT NULL DEFAULT 'legacy'"),
        ("BookingReceipts", "PayloadJson", "TEXT NULL")
    ];

    public static async Task EnsureAsync(AppDb db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            var columns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var table in Additions.Select(x => x.Table).Distinct())
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = await command.ExecuteReaderAsync(ct);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync(ct)) names.Add(reader.GetString(1));
                columns.Add(table, names);
            }
            var missing = Additions.Where(x => !columns[x.Table].Contains(x.Column)).ToArray();
            if (missing.Length == 0) return;
            // SQLite's backup API includes committed WAL pages, unlike copying the .db file.
            var settings = new SqliteConnectionStringBuilder(connection.ConnectionString);
            if (settings.Mode != SqliteOpenMode.Memory && settings.DataSource != ":memory:" && !string.IsNullOrWhiteSpace(settings.DataSource))
            {
                var databasePath = Path.GetFullPath(settings.DataSource);
                var backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, ".schema-backups");
                Directory.CreateDirectory(backupDirectory);
                var backupPath = Path.Combine(backupDirectory, $"stayline-before-deltas-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");
                await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
                await backup.OpenAsync(ct);
                connection.BackupDatabase(backup);
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var column in missing)
            {
                using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
                command.CommandText = $"ALTER TABLE \"{column.Table}\" ADD COLUMN \"{column.Column}\" {column.Definition}";
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
