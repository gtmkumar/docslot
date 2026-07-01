using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the user-lifecycle endpoints behave as designed under RLS as <c>docslot_app</c> (the production
/// path through the SECURITY DEFINER functions):
/// <list type="bullet">
///   <item>Invite is wired live and escalation-SAFE — an actor with only <c>tenant.users.create</c> cannot
///   confer a role (the create rolls back atomically), while an admin can; an existing email LINKS without
///   overwriting the profile.</item>
///   <item>Deactivate/reactivate round-trips as a tenant-scoped membership revoke/restore; self-deactivate
///   is blocked; deactivating an admin is allowed while another admin remains.</item>
///   <item>The last-admin guard's BLOCKING branch (issue #79): an actor who can act but is not counted as
///   "another admin" gets 409 on both the deactivate path and the role-assignment revoke path when the
///   target is the tenant's sole admin, the target's membership is left untouched, and the guard yields to
///   a permission-based (not role-name-based) second admin. See the dedicated region below.</item>
///   <item>Edit profile mutates only the whitelisted fields (never the email); an invalid language is 422.</item>
///   <item>Reset access sets the flags and clears the lockout; self-reset is blocked; a member lacking
///   <c>tenant.users.update</c> is 403.</item>
/// </list>
/// </summary>
public sealed class UserLifecycleTests(UserLifecycleWebAppFactory factory) : IClassFixture<UserLifecycleWebAppFactory>
{
    // ---- Invite (create) — wired live + escalation-by-proxy fix --------------------------------------

    [Fact]
    public async Task Admin_InviteUser_WithRole_CreatesAndAssigns()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");
        var email = $"{factory.InvitePrefix}.with-role@docslot.test";

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(email, "New Hire", null, "en", staffRole));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<CreateUserResult>())!;
        Assert.False(result.AlreadyExisted);
        Assert.NotEqual(Guid.Empty, result.UserId);

        var users = await ListUsersAsync(client);
        var u = users.Single(x => x.UserId == result.UserId);
        Assert.Contains(u.Roles, r => r.RoleKey == "tenant_staff");
    }

    [Fact]
    public async Task Admin_InviteExistingEmail_LinksWithoutOverwriting()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(factory.MemberEmail, "Should Not Overwrite", null, "en", staffRole));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<CreateUserResult>())!;
        Assert.True(result.AlreadyExisted);
        Assert.Equal(factory.MemberUserId, result.UserId);

        var fullName = (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "full_name"))!;
        Assert.NotEqual("Should Not Overwrite", fullName);   // existing profile preserved
    }

    [Fact]
    public async Task LimitedInviter_InviteWithRole_Forbidden_AndRollsBack()
    {
        var client = await AuthedClientAsync(factory.InviterEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");
        var email = $"{factory.InvitePrefix}.escalation@docslot.test";

        // Inviter holds tenant.users.create but NOT tenant.roles.assign → conferring ANY role is the
        // escalation-by-proxy the fix closes. The create + assign share one UoW transaction.
        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(email, "Escalation Attempt", null, "en", staffRole));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(await UserLifecycleWebAppFactory.LiveUserExistsAsync(email));   // atomic rollback, no orphan
    }

    [Fact]
    public async Task LimitedInviter_InviteWithoutRole_Succeeds()
    {
        var client = await AuthedClientAsync(factory.InviterEmail);
        var email = $"{factory.InvitePrefix}.norole@docslot.test";

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(email, "No Role Hire", null, "en", null));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True(await UserLifecycleWebAppFactory.LiveUserExistsAsync(email));
    }

    // ---- Deactivate / reactivate --------------------------------------------------------------------

    [Fact]
    public async Task Admin_DeactivateThenReactivate_Member_RoundTrips()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);

        var deact = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}/status",
            new SetUserStatusRequest(false, "left the clinic"));
        Assert.Equal(HttpStatusCode.OK, deact.StatusCode);

        var afterDeact = (await ListUsersAsync(client)).Single(x => x.UserId == factory.MemberUserId);
        Assert.False(afterDeact.IsActive);
        Assert.Empty(afterDeact.Roles);   // memberships revoked → no chips, still listed (reactivatable)

        var react = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}/status",
            new SetUserStatusRequest(true, "rejoined"));
        Assert.Equal(HttpStatusCode.OK, react.StatusCode);

        var afterReact = (await ListUsersAsync(client)).Single(x => x.UserId == factory.MemberUserId);
        Assert.True(afterReact.IsActive);
        Assert.Contains(afterReact.Roles, r => r.RoleKey == "tenant_staff");
    }

    [Fact]
    public async Task Admin_DeactivateSelf_Forbidden()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.AdminUserId}/status",
            new SetUserStatusRequest(false, "self"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_DeactivateOtherAdmin_AllowedWhileAnotherRemains()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);

        var deact = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.Admin2UserId}/status",
            new SetUserStatusRequest(false, "rotate admin2"));
        Assert.Equal(HttpStatusCode.OK, deact.StatusCode);

        // restore so the tenant keeps two admins for any other test
        var react = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.Admin2UserId}/status",
            new SetUserStatusRequest(true, "restore admin2"));
        Assert.Equal(HttpStatusCode.OK, react.StatusCode);
    }

    [Fact]
    public async Task Member_LacksUpdate_Deactivate_Forbidden()
    {
        var client = await AuthedClientAsync(factory.MemberEmail);   // tenant_staff: no tenant.users.update
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.Admin2UserId}/status",
            new SetUserStatusRequest(false, "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- Last-admin guard: the BLOCKING branch (issue #79) -------------------------------------------
    //
    // Admin_DeactivateOtherAdmin_AllowedWhileAnotherRemains (above) only ever exercises the ALLOWED path —
    // tenant_has_other_active_admin() never returns false there, since this fixture's tenant deliberately
    // keeps two admins alive across the whole class. These three tests force the false branch so a
    // regression that dropped or inverted the guard would go red.
    //
    // The actor cannot be the sole admin itself (self-guard → 403, Admin_DeactivateSelf_Forbidden) nor an
    // unprivileged member (permission-guard → 403, Member_LacksUpdate_Deactivate_Forbidden). A genuine
    // platform `super_admin` is ALSO the wrong choice, despite being "platform-scoped with no membership":
    // both set_tenant_user_active (11_rbac_hardening.sql:1522) and revoke_role_assignment (:793) gate their
    // ENTIRE last-admin check behind `NOT platform.is_super_admin(actor)` — a real super_admin bypasses the
    // guard outright and the call would succeed (200), never observing the 409. So the actor here is a
    // narrower kind of platform-scoped principal: a user with ZERO platform.user_tenant_roles rows
    // anywhere, empowered only by a GLOBAL grant-override (user_permission_overrides.tenant_id IS NULL) for
    // tenant.users.update / tenant.roles.assign. That override satisfies user_has_permission() for ANY
    // p_tenant_id (the `OR o.tenant_id IS NULL` branch in both user_has_permission and
    // resolve_user_permissions), so the actor clears the [RequirePermission] gate and the function's own
    // "actor may manage" re-check — but, holding no membership row in the target tenant, is never counted
    // by tenant_has_other_active_admin(), which scans user_tenant_roles rows only. That combination is
    // exactly what is needed to observe the guard fire on the sole admin.
    //
    // Each test seeds (and tears down) its own disposable tenant/users via the owner connection, so none of
    // it touches this class's shared AdminEmail/Admin2Email/MemberEmail fixture state.

    [Fact]
    public async Task NonMemberActor_GlobalOverride_Deactivate_SoleAdmin_Returns409_LastAdminGuard()
    {
        var scenario = await SeedSoleAdminScenarioAsync("deact");
        try
        {
            var client = await AuthedClientAsync(scenario.ActorEmail);   // resolves to NO active tenant

            var resp = await client.PutAsJsonAsync(
                $"/api/v1/tenants/{scenario.TenantId}/users/{scenario.SoleAdminUserId}/status",
                new SetUserStatusRequest(false, "actor attempts to strip the tenant's only admin"));

            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

            // Pin to THIS guard, not a coincidental SoD(23000)/duplicate(23505): UserLifecycle.ExecAsync
            // wraps the SECURITY DEFINER function's raw RAISE EXCEPTION text verbatim in ConflictException,
            // and that text is exactly "cannot deactivate the last administrator of tenant %" — distinct
            // from the SoD trigger's "SoD violation: role % is incompatible with already-held role % ..."
            // and from any unique-violation text. Nothing else in this arrange can raise 23xxx: the actor
            // holds no role assignment of their own to collide, and the request never touches role_permissions.
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("cannot deactivate the last administrator of tenant", body);

            // The target's membership is untouched: still active, still holding the tenant_owner (admin) role.
            var (revokedAt, roleId) = await MembershipStateAsync(scenario.SoleAdminUtrId);
            Assert.Null(revokedAt);
            Assert.Equal(scenario.TenantOwnerRoleId, roleId);
        }
        finally
        {
            await CleanupSoleAdminScenarioAsync(scenario);
        }
    }

    [Fact]
    public async Task NonMemberActor_GlobalOverride_Revoke_SoleAdminAssignment_Returns409_LastAdminGuard()
    {
        var scenario = await SeedSoleAdminScenarioAsync("revoke");
        try
        {
            var client = await AuthedClientAsync(scenario.ActorEmail);

            // The analogous revoke-path: POST /role-assignments/revoke routes through
            // platform.revoke_role_assignment, which carries its OWN copy of the last-admin guard (closing
            // the role-revoke door the deactivate-path guard doesn't cover).
            var resp = await client.PostAsJsonAsync("/api/v1/role-assignments/revoke",
                new RevokeRoleRequest(scenario.SoleAdminUtrId, "actor attempts to revoke the sole admin's role"));

            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

            // revoke_role_assignment's own text: "cannot revoke the last administrator's access in tenant %"
            // — again distinct from the SoD/duplicate wording, pinning this 409 to THIS guard specifically.
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("cannot revoke the last administrator", body);
            Assert.Contains("access in tenant", body);

            var (revokedAt, roleId) = await MembershipStateAsync(scenario.SoleAdminUtrId);
            Assert.Null(revokedAt);
            Assert.Equal(scenario.TenantOwnerRoleId, roleId);
        }
        finally
        {
            await CleanupSoleAdminScenarioAsync(scenario);
        }
    }

    [Fact]
    public async Task NonMemberActor_GlobalOverride_Deactivate_Permitted_WhenCustomRoleHolderCountsAsAdmin()
    {
        // Positive control: proves tenant_has_other_active_admin() is PERMISSION-based, not role-name-based.
        var scenario = await SeedSoleAdminScenarioAsync("posctrl");
        Guid? backupUserId = null;
        Guid? backupRoleId = null;
        try
        {
            // A second member holds a CUSTOM role — deliberately NOT named anything like "admin"/"owner" —
            // carrying ONLY tenant.users.update. That alone must count as "another admin".
            (backupUserId, backupRoleId) = await AddPermissionOnlyBackupAdminAsync(scenario.TenantId);

            var client = await AuthedClientAsync(scenario.ActorEmail);
            var resp = await client.PutAsJsonAsync(
                $"/api/v1/tenants/{scenario.TenantId}/users/{scenario.SoleAdminUserId}/status",
                new SetUserStatusRequest(false, "rotate; the custom-role holder now covers admin duties"));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var (revokedAt, _) = await MembershipStateAsync(scenario.SoleAdminUtrId);
            Assert.NotNull(revokedAt);   // this time it actually happened — the guard did NOT block it
        }
        finally
        {
            await CleanupSoleAdminScenarioAsync(scenario, backupUserId, backupRoleId);
        }
    }

    // ---- Edit profile -------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_EditMemberProfile_UpdatesWhitelistedFields_NotEmail()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var emailBefore = (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "email"))!;

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}",
            new UpdateUserProfileRequest("Renamed Member", "+919812345678", "hi"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal("Renamed Member", (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "full_name"))!);
        Assert.Equal("hi", (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "preferred_language"))!);
        Assert.Equal(emailBefore, (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "email"))!);
    }

    [Fact]
    public async Task EditProfile_InvalidLanguage_422()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}",
            new UpdateUserProfileRequest("Whatever", null, "fr"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    // ---- Reset access -------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_ResetMemberAccess_SetsFlagsAndClearsLock()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        await LockUserAsync(factory.MemberUserId);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}/reset-access",
            new ResetAccessRequest("forgot password"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.True((bool)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "must_change_password"))!);
        var locked = await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "locked_until");
        Assert.True(locked is null or DBNull);
    }

    [Fact]
    public async Task Admin_ResetSelf_Forbidden()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.AdminUserId}/reset-access",
            new ResetAccessRequest("self"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, UserLifecycleWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private async Task<List<UserListItemDto>> ListUsersAsync(HttpClient client)
    {
        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/users");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<UserListItemDto>>())!;
    }

    private static async Task LockUserAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(UserLifecycleWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE platform.users SET locked_until = NOW() + interval '1 hour', failed_login_count = 4 WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("u", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- last-admin-guard scenario helpers (issue #79) -----------------------------------------------

    /// <summary>A disposable tenant with exactly ONE admin (tenant_owner) plus a platform-scoped actor who
    /// holds a GLOBAL grant-override for tenant.users.update/tenant.roles.assign but no membership row
    /// anywhere — see the region comment above for why that specific shape is required.</summary>
    private sealed record SoleAdminScenario(
        Guid TenantId, Guid SoleAdminUserId, Guid SoleAdminUtrId, string SoleAdminEmail,
        Guid ActorUserId, string ActorEmail, Guid TenantOwnerRoleId);

    private static async Task<SoleAdminScenario> SeedSoleAdminScenarioAsync(string label)
    {
        await using var conn = new NpgsqlConnection(UserLifecycleWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();

        var tenantId = Guid.NewGuid();
        var soleAdminId = Guid.NewGuid();
        var soleAdminUtrId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var soleAdminEmail = $"lag.{label}.admin+{Guid.NewGuid():N}@docslot.test";
        var actorEmail = $"lag.{label}.actor+{Guid.NewGuid():N}@docslot.test";

        var tenantOwnerRoleId = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_owner");
        var updatePermId = await IamAdminWebAppFactory.PermissionIdAsync("tenant.users.update");
        var assignPermId = await IamAdminWebAppFactory.PermissionIdAsync("tenant.roles.assign");

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Last-Admin-Guard Hospital', 'LAG', 'hospital', 'lag@docslot.test', '+919744444444', 'active')
            """,
            ("id", tenantId), ("code", $"lag-{label}-{tenantId.ToString()[..8]}"));

        foreach (var (uid, email) in new[] { (soleAdminId, soleAdminEmail), (actorId, actorEmail) })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Last-Admin-Guard Test User', true, true, false, NOW(), NOW())
                """,
                ("id", uid), ("email", email), ("pwd", UserLifecycleWebAppFactory.Password));

        // The tenant's ONLY admin-capability holder.
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (@utr, @uid, @tid, @rid, true, NOW())
            """,
            ("utr", soleAdminUtrId), ("uid", soleAdminId), ("tid", tenantId), ("rid", tenantOwnerRoleId));

        // The actor: NO user_tenant_roles row anywhere. A GLOBAL (tenant_id IS NULL) grant override
        // satisfies user_has_permission()/resolve_user_permissions() for ANY tenant (the `OR o.tenant_id IS
        // NULL` branch) yet is invisible to tenant_has_other_active_admin()'s membership-row scan.
        foreach (var permId in new[] { updatePermId, assignPermId })
            await Exec(conn,
                """
                INSERT INTO platform.user_permission_overrides
                    (override_id, user_id, permission_id, tenant_id, is_allowed, reason, is_active, effective_from, created_at, updated_at)
                VALUES (gen_random_uuid(), @uid, @pid, NULL, true, 'issue #79 last-admin-guard test actor', true, NOW(), NOW(), NOW())
                """,
                ("uid", actorId), ("pid", permId));

        return new SoleAdminScenario(tenantId, soleAdminId, soleAdminUtrId, soleAdminEmail, actorId, actorEmail, tenantOwnerRoleId);
    }

    /// <summary>Adds a second member holding a CUSTOM, non-admin-named role granting ONLY
    /// tenant.users.update — the permission-based (not role-name-based) positive control.</summary>
    private static async Task<(Guid UserId, Guid RoleId)> AddPermissionOnlyBackupAdminAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(UserLifecycleWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();

        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var email = $"lag.backup+{Guid.NewGuid():N}@docslot.test";
        var updatePermId = await IamAdminWebAppFactory.PermissionIdAsync("tenant.users.update");

        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Rota Coordinator (custom role)', true, true, false, NOW(), NOW())
            """,
            ("id", userId), ("email", email), ("pwd", UserLifecycleWebAppFactory.Password));

        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'Rota Coordinator', @tid, 'tenant', false, NOW(), NOW())
            """,
            ("id", roleId), ("key", $"lag_rota_{roleId.ToString("N")[..8]}"), ("tid", tenantId));
        await Exec(conn,
            "INSERT INTO platform.role_permissions (role_id, permission_id, is_grantable, granted_at) VALUES (@rid, @pid, false, NOW())",
            ("rid", roleId), ("pid", updatePermId));
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (gen_random_uuid(), @uid, @tid, @rid, true, NOW())
            """,
            ("uid", userId), ("tid", tenantId), ("rid", roleId));

        return (userId, roleId);
    }

    private static async Task<(DateTime? RevokedAt, Guid RoleId)> MembershipStateAsync(Guid userTenantRoleId)
    {
        await using var conn = new NpgsqlConnection(UserLifecycleWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT revoked_at, role_id FROM platform.user_tenant_roles WHERE user_tenant_role_id = @u", conn);
        cmd.Parameters.AddWithValue("u", userTenantRoleId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var revokedAt = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
        return (revokedAt, reader.GetGuid(1));
    }

    private static async Task CleanupSoleAdminScenarioAsync(
        SoleAdminScenario s, Guid? backupAdminUserId = null, Guid? backupRoleId = null)
    {
        await using var conn = new NpgsqlConnection(UserLifecycleWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();

        var userIds = new List<Guid> { s.SoleAdminUserId, s.ActorUserId };
        if (backupAdminUserId is { } bId) userIds.Add(bId);
        var userIdArray = userIds.ToArray();

        await Exec(conn, "DELETE FROM platform.user_permission_overrides WHERE user_id = ANY(@u)", ("u", userIdArray));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", userIdArray));
        if (backupRoleId is { } rId)
        {
            await Exec(conn, "DELETE FROM platform.role_permissions WHERE role_id = @r", ("r", rId));
            await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @r", ("r", rId));
        }
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", userIdArray));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)",
            ("e", new[] { s.SoleAdminEmail, s.ActorEmail }));
        foreach (var uid in userIdArray)
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@lag.test"), ("u", uid));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t", ("t", s.TenantId));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
