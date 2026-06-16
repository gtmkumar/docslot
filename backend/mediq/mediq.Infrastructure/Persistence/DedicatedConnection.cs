using Microsoft.Extensions.Configuration;
using Npgsql;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Opens a SHORT-LIVED connection independent of the request's <see cref="PlatformDbContext"/> transaction.
/// Used by writers whose records MUST survive a rollback of the surrounding command transaction — i.e. the
/// audit trail (tamper-evidence must not depend on business success) and login-attempt/lockout bookkeeping
/// (a failed login records the attempt and then throws; that record must persist for lockout to work).
/// </summary>
public interface IDedicatedConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct);
}

public sealed class DedicatedConnectionFactory(IConfiguration config) : IDedicatedConnectionFactory
{
    private readonly string _connectionString =
        config.GetConnectionString("platform-db")
        ?? config.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("No 'platform-db' connection string configured.");

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
