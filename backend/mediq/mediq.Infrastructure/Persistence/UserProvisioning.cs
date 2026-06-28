using System.Security.Cryptography;
using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Admin;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Write-side user provisioning. Inserts the <c>platform.users</c> row only — the initial-role assignment is
/// orchestrated by <c>CreateUserCommandHandler</c> through <see cref="IRoleAssignmentRepository.AssignRoleAsync"/>
/// (the SECURITY DEFINER path that enforces the no-escalation + SoD guards), NOT a raw INSERT here.
/// <para>
/// A password is never accepted from the admin (impersonation hazard): the invite always seeds a
/// server-generated, never-returned temp credential and sets <c>must_change_password=true</c>, which both
/// satisfies <c>chk_user_has_auth</c> (no auth-less row) and forces the user to set their own password on
/// first login. On an email collision (users are a GLOBAL identity) the existing user_id is reused and
/// <c>AlreadyExisted=true</c> is returned — the existing profile is never overwritten.
/// </para>
/// </summary>
public sealed class UserProvisioning(PlatformDbContext db, IPasswordHasher hasher) : IUserProvisioning
{
    public async Task<(Guid UserId, bool AlreadyExisted)> CreateAsync(
        CreateUserRequest request, DateTime nowUtc, CancellationToken ct)
    {
        // Email is a global, case-insensitive (CITEXT) identity. If it already exists, link — don't recreate.
        var existingId = await db.Users.AsNoTracking()
            .Where(u => u.Email == request.Email)
            .Select(u => (Guid?)u.UserId)
            .FirstOrDefaultAsync(ct);
        if (existingId is { } id)
            return (id, true);

        var userId = Guid.CreateVersion7();

        // Server-generated temp credential — random, never returned, never logged. The user can't log in
        // with it; they set their own password via first-login / recovery. must_change_password forces it.
        var tempSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var passwordHash = hasher.Hash(tempSecret);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.users
                (user_id, email, phone, password_hash, full_name, preferred_language,
                 must_change_password, is_active, created_at, updated_at)
            VALUES
                (@p0, @p1, @p2, @p3, @p4, @p5, true, true, @p6, @p6)
            """,
            new[]
            {
                new NpgsqlParameter("@p0", userId),
                new NpgsqlParameter("@p1", request.Email),
                new NpgsqlParameter("@p2", (object?)request.Phone ?? DBNull.Value),
                new NpgsqlParameter("@p3", passwordHash),
                new NpgsqlParameter("@p4", request.FullName),
                new NpgsqlParameter("@p5", request.PreferredLanguage),
                new NpgsqlParameter("@p6", nowUtc),
            }.Cast<object>().ToArray());

        return (userId, false);
    }
}
