using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Admin;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Write-side user provisioning. Inserts into <c>platform.users</c> (with an argon2id password hash when
/// supplied) and optionally seeds an initial <c>user_tenant_roles</c> assignment. Uses parameterized raw
/// SQL because the schema owns column defaults/triggers and the domain entity is insert-via-schema.
/// </summary>
public sealed class UserProvisioning(PlatformDbContext db, IPasswordHasher hasher) : IUserProvisioning
{
    public async Task<Guid> CreateAsync(Guid tenantId, CreateUserRequest request, DateTime nowUtc, CancellationToken ct)
    {
        var userId = Guid.CreateVersion7();
        var passwordHash = string.IsNullOrEmpty(request.Password) ? null : hasher.Hash(request.Password);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.users
                (user_id, email, phone, password_hash, full_name, preferred_language, is_active, created_at, updated_at)
            VALUES
                (@p0, @p1, @p2, @p3, @p4, @p5, true, @p6, @p6)
            """,
            new[]
            {
                new NpgsqlParameter("@p0", userId),
                new NpgsqlParameter("@p1", request.Email),
                new NpgsqlParameter("@p2", (object?)request.Phone ?? DBNull.Value),
                new NpgsqlParameter("@p3", (object?)passwordHash ?? DBNull.Value),
                new NpgsqlParameter("@p4", request.FullName),
                new NpgsqlParameter("@p5", request.PreferredLanguage),
                new NpgsqlParameter("@p6", nowUtc),
            }.Cast<object>().ToArray());

        if (request.InitialRoleId is { } roleId)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO platform.user_tenant_roles
                    (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                VALUES (@p0, @p1, @p2, @p3, true, @p4)
                ON CONFLICT (user_id, tenant_id, role_id) DO NOTHING
                """,
                new[]
                {
                    new NpgsqlParameter("@p0", Guid.CreateVersion7()),
                    new NpgsqlParameter("@p1", userId),
                    new NpgsqlParameter("@p2", tenantId),
                    new NpgsqlParameter("@p3", roleId),
                    new NpgsqlParameter("@p4", nowUtc),
                }.Cast<object>().ToArray());
        }

        return userId;
    }
}
