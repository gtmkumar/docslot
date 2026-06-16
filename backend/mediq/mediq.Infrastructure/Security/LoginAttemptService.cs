using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Appends to <c>platform.login_attempts</c>. Writes on a DEDICATED connection (not the request command
/// transaction) so the attempt record survives even when the surrounding command throws on a failed login —
/// otherwise a command-transaction rollback would erase the failed attempt and break lockout enforcement.
/// IP defaults to a sentinel when absent because the column is NOT NULL.
/// </summary>
public sealed class LoginAttemptService(IDedicatedConnectionFactory connections) : ILoginAttemptService
{
    public async Task RecordAsync(string email, string? ipAddress, string? userAgent, bool success, string? failureReason, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO platform.login_attempts (attempt_id, email, ip_address, user_agent, success, failure_reason, attempted_at)
            VALUES (gen_random_uuid(), @p0, CAST(@p1 AS inet), @p2, @p3, @p4, NOW())
            """, conn);
        cmd.Parameters.AddWithValue("@p0", email);
        cmd.Parameters.AddWithValue("@p1", ipAddress ?? "0.0.0.0");
        cmd.Parameters.AddWithValue("@p2", (object?)userAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p3", success);
        cmd.Parameters.AddWithValue("@p4", (object?)failureReason ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
