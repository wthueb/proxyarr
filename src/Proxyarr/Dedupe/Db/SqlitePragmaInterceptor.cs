using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Proxyarr.Dedupe.Db;

/// <summary>
/// Applies the per-connection SQLite pragmas dedup relies on: WAL journaling (concurrent readers
/// alongside one writer) and a busy timeout so a second writer waits instead of failing with
/// SQLITE_BUSY. Because the store opens a fresh connection per operation via
/// <c>IDbContextFactory</c>, these must be set on every connection open, not just once at startup.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
