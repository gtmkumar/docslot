using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>
/// Write-side access to <c>platform.users</c>. Earns the Repository pattern because login state has
/// non-trivial update logic (lockout stamping) that must persist even on auth failure.
/// <para>
/// <see cref="UpdateLoginStateAsync"/> writes the lockout columns on a DEDICATED connection (not the request
/// command transaction) so the failed-login bookkeeping survives the thrown <c>InvalidCredentialsException</c>
/// — otherwise the command-transaction rollback would erase the incremented failed-login count and break
/// lockout enforcement.
/// </para>
/// </summary>
public sealed class UserRepository(PlatformDbContext db, IDedicatedConnectionFactory connections) : IUserRepository
{
    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == email && u.DeletedAt == null, ct);

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.UserId == userId && u.DeletedAt == null, ct);

    public async Task AddAsync(User user, CancellationToken ct) =>
        await db.Users.AddAsync(user, ct);

    public async Task UpdateLoginStateAsync(User user, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE platform.users
            SET failed_login_count = @p1, locked_until = @p2,
                last_login_at = @p3, last_login_ip = CAST(@p4 AS inet)
            WHERE user_id = @p0
            """, conn);
        cmd.Parameters.AddWithValue("@p0", user.UserId);
        cmd.Parameters.AddWithValue("@p1", user.FailedLoginCount);
        cmd.Parameters.AddWithValue("@p2", (object?)user.LockedUntil ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p3", (object?)user.LastLoginAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p4", (object?)user.LastLoginIp ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdatePasswordHashAsync(Guid userId, string newPasswordHash, CancellationToken ct) =>
        // On the request DbContext connection → enlisted in the command UnitOfWork transaction (commits with the audit row).
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.users
            SET password_hash = @p1, must_change_password = false, updated_at = NOW()
            WHERE user_id = @p0 AND deleted_at IS NULL
            """,
            [new NpgsqlParameter("@p0", userId), new NpgsqlParameter("@p1", newPasswordHash)], ct);
}
