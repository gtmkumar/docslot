using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// End-to-end tenant edit + suspend split (a PLATFORM-ONLY super_admin). Proves:
/// (1) PUT /tenants/{id} edits mutable attributes (incl. geo-tag under settings.geo; a contact-only edit does NOT
///     wipe an existing geo tag), a follow-up GET reflects them, tenant_code/tenant_type stay immutable, and the
///     edit path NEVER changes status; an unknown id is 404.
/// (2) Suspend/reactivate are SEPARATE dangerous endpoints (PUT /tenants/{id}/suspend + /reactivate, both gated on
///     platform.tenants.suspend, broker pattern): a suspend requires a reason (persisted to suspended_reason;
///     missing reason → 422), both return the fresh TenantDetailDto, reactivate clears the reason, unknown id → 404.
/// </summary>
public sealed class TenantUpdateTests(RbacSuperAdminGucWebAppFactory factory)
    : IClassFixture<RbacSuperAdminGucWebAppFactory>
{
    [Fact]
    public async Task SuperAdmin_Updates_Tenant_And_Get_Reflects_It_Without_Touching_Status()
    {
        var saUserId = Guid.NewGuid();
        var saEmail = $"edit.sa+{Guid.NewGuid():N}@docslot.test";
        var code = $"edit-{Guid.NewGuid():N}"[..18];
        var ownerEmail = $"edit.owner+{Guid.NewGuid():N}@docslot.test";
        Guid tenantId = default;

        await SeedPlatformOnlySuperAdminAsync(saUserId, saEmail);
        try
        {
            var sa = await AuthedClientAsync(saEmail, RbacSuperAdminGucWebAppFactory.Password);

            // ---- Onboard a clinic to edit (lands 'active') ---------------------------------------
            var create = await sa.PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest(
                code, "Edit Test Clinic Pvt Ltd", "Edit Test Clinic", "hospital",
                $"ops+{code}@docslot.test", "+919800000101", "Pune", "Maharashtra",
                "411001", null, null, ownerEmail));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = (await create.Content.ReadFromJsonAsync<CreateTenantResult>())!;
            tenantId = created.TenantId;

            // ---- Edit: new display name, new city, AND geo-tag it — NO status field on this path --
            const decimal lat = 18.938771m, lng = 72.835335m;
            var update = await sa.PutAsJsonAsync($"/api/v1/tenants/{tenantId}", new UpdateTenantRequest(
                "Edit Test Clinic (Renamed)", "Edit Test Clinic Pvt Ltd", $"ops+{code}@docslot.test",
                "+919800000102", "Mumbai", "Maharashtra", "400001", lat, lng));
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var updated = (await update.Content.ReadFromJsonAsync<TenantDetailDto>())!;

            Assert.Equal("Edit Test Clinic (Renamed)", updated.DisplayName);
            Assert.Equal("Mumbai", updated.City);
            // The edit path never touches status — the clinic is still active.
            Assert.Equal("active", updated.Status);
            // The PUT response carries the full editable shape so the form re-syncs.
            Assert.Equal("Edit Test Clinic Pvt Ltd", updated.LegalName);
            Assert.Equal("+919800000102", updated.PrimaryPhone);
            Assert.Equal("Maharashtra", updated.State);
            Assert.Equal("400001", updated.PinCode);
            // Geo persisted under settings.geo and echoed back.
            Assert.Equal(lat, updated.Latitude);
            Assert.Equal(lng, updated.Longitude);
            // Immutable identity/structure survived the edit.
            Assert.Equal(code, updated.TenantCode);
            Assert.Equal("hospital", updated.TenantType);

            // settings.geo was written with the same shape as onboarding.
            Assert.Equal("18.938771|72.835335", await ScalarAsync(
                "SELECT (settings#>>'{geo,latitude}') || '|' || (settings#>>'{geo,longitude}') " +
                "FROM platform.tenants WHERE tenant_id = @t", ("t", tenantId)));

            // ---- A follow-up GET reflects the change AND pre-fills every editable field + geo ------
            var fetched = (await sa.GetFromJsonAsync<TenantDetailDto>($"/api/v1/tenants/{tenantId}"))!;
            Assert.Equal("Edit Test Clinic (Renamed)", fetched.DisplayName);
            Assert.Equal("Mumbai", fetched.City);
            Assert.Equal("active", fetched.Status);
            Assert.Equal("Edit Test Clinic Pvt Ltd", fetched.LegalName);
            Assert.Equal("+919800000102", fetched.PrimaryPhone);
            Assert.Equal("Maharashtra", fetched.State);
            Assert.Equal("400001", fetched.PinCode);
            Assert.Equal(lat, fetched.Latitude);
            Assert.Equal(lng, fetched.Longitude);
            Assert.Equal(code, fetched.TenantCode);
            Assert.Equal("hospital", fetched.TenantType);

            // ---- A contact-only edit (NO geo supplied) must NOT wipe the existing geo tag ----------
            var contactOnly = await sa.PutAsJsonAsync($"/api/v1/tenants/{tenantId}", new UpdateTenantRequest(
                "Edit Test Clinic (Renamed)", "Edit Test Clinic Pvt Ltd", $"ops+{code}@docslot.test",
                "+919800000102", "Delhi", "Delhi", "110001"));
            Assert.Equal(HttpStatusCode.OK, contactOnly.StatusCode);
            var afterContact = (await contactOnly.Content.ReadFromJsonAsync<TenantDetailDto>())!;
            Assert.Equal("Delhi", afterContact.City);
            // Geo preserved (not wiped) — the response echoes the retained tag...
            Assert.Equal(lat, afterContact.Latitude);
            Assert.Equal(lng, afterContact.Longitude);
            // ...and it is still in the DB.
            Assert.Equal("18.938771|72.835335", await ScalarAsync(
                "SELECT (settings#>>'{geo,latitude}') || '|' || (settings#>>'{geo,longitude}') " +
                "FROM platform.tenants WHERE tenant_id = @t", ("t", tenantId)));
        }
        finally
        {
            await CleanupAsync(saUserId, saEmail, tenantId);
        }
    }

    [Fact]
    public async Task Suspend_Requires_Reason_Persists_It_And_Reactivate_Clears_It()
    {
        var saUserId = Guid.NewGuid();
        var saEmail = $"susp.sa+{Guid.NewGuid():N}@docslot.test";
        var code = $"susp-{Guid.NewGuid():N}"[..18];
        var ownerEmail = $"susp.owner+{Guid.NewGuid():N}@docslot.test";
        Guid tenantId = default;

        await SeedPlatformOnlySuperAdminAsync(saUserId, saEmail);
        try
        {
            var sa = await AuthedClientAsync(saEmail, RbacSuperAdminGucWebAppFactory.Password);

            var create = await sa.PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest(
                code, "Suspend Test Clinic Pvt Ltd", "Suspend Test Clinic", "hospital",
                $"ops+{code}@docslot.test", "+919800000201", "Pune", "Maharashtra",
                "411001", null, null, ownerEmail));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            tenantId = (await create.Content.ReadFromJsonAsync<CreateTenantResult>())!.TenantId;

            // ---- Suspend WITHOUT a reason is rejected (422) --------------------------------------
            var noReason = await sa.PutAsJsonAsync($"/api/v1/tenants/{tenantId}/suspend",
                new SetTenantStatusReasonRequest(Reason: null));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);

            // ---- Suspend WITH a reason: status flips + suspended_reason persists + returns detail --
            const string reason = "Non-payment of Q3 invoice";
            var suspend = await sa.PutAsJsonAsync($"/api/v1/tenants/{tenantId}/suspend",
                new SetTenantStatusReasonRequest(Reason: reason));
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            var suspended = (await suspend.Content.ReadFromJsonAsync<TenantDetailDto>())!;
            Assert.Equal("suspended", suspended.Status);
            Assert.Equal(reason, suspended.SuspendedReason);   // returned detail re-syncs the reason

            var afterSuspend = (await sa.GetFromJsonAsync<TenantDetailDto>($"/api/v1/tenants/{tenantId}"))!;
            Assert.Equal("suspended", afterSuspend.Status);
            Assert.Equal(reason, afterSuspend.SuspendedReason);
            Assert.Equal(reason, await ScalarAsync(
                "SELECT suspended_reason FROM platform.tenants WHERE tenant_id = @t", ("t", tenantId)));

            // ---- Reactivate: status returns to active + suspended_reason cleared (no reason needed) --
            var reactivate = await sa.PutAsJsonAsync($"/api/v1/tenants/{tenantId}/reactivate",
                new SetTenantStatusReasonRequest(Reason: null));
            Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
            var reactivated = (await reactivate.Content.ReadFromJsonAsync<TenantDetailDto>())!;
            Assert.Equal("active", reactivated.Status);
            Assert.Null(reactivated.SuspendedReason);

            var afterReactivate = (await sa.GetFromJsonAsync<TenantDetailDto>($"/api/v1/tenants/{tenantId}"))!;
            Assert.Equal("active", afterReactivate.Status);
            Assert.Null(afterReactivate.SuspendedReason);
            Assert.Null(await ScalarAsync(
                "SELECT suspended_reason FROM platform.tenants WHERE tenant_id = @t", ("t", tenantId)));
        }
        finally
        {
            await CleanupAsync(saUserId, saEmail, tenantId);
        }
    }

    [Fact]
    public async Task UpdateTenant_And_Suspend_UnknownId_Are404()
    {
        var saUserId = Guid.NewGuid();
        var saEmail = $"edit.sa2+{Guid.NewGuid():N}@docslot.test";
        await SeedPlatformOnlySuperAdminAsync(saUserId, saEmail);
        try
        {
            var sa = await AuthedClientAsync(saEmail, RbacSuperAdminGucWebAppFactory.Password);

            var editGhost = await sa.PutAsJsonAsync($"/api/v1/tenants/{Guid.NewGuid()}", new UpdateTenantRequest(
                "Ghost Clinic", "Ghost Clinic Pvt Ltd", "ghost@docslot.test",
                "+919800000103", "Nowhere", "Nostate", null));
            Assert.Equal(HttpStatusCode.NotFound, editGhost.StatusCode);

            var suspendGhost = await sa.PutAsJsonAsync($"/api/v1/tenants/{Guid.NewGuid()}/suspend",
                new SetTenantStatusReasonRequest(Reason: "gone"));
            Assert.Equal(HttpStatusCode.NotFound, suspendGhost.StatusCode);
        }
        finally
        {
            await CleanupAsync(saUserId, saEmail, default);
        }
    }

    [Fact]
    public async Task Update_Permission_Alone_Cannot_Suspend_Or_Reactivate_403()
    {
        // Negative-authorization boundary (auditor recommendation): a principal holding platform.tenants.update but
        // NOT the DANGEROUS platform.tenants.suspend must be rejected 403 on BOTH suspend and reactivate — locks the
        // split against regression (a future collapse back onto .update would turn these 403s into 404s and fail).
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var email = $"upd.only+{Guid.NewGuid():N}@docslot.test";
        var roleKey = ("tenants_upd_only_" + Guid.NewGuid().ToString("N"))[..28];

        await SeedUpdateOnlyPlatformAdminAsync(userId, email, roleId, roleKey);
        try
        {
            var caller = await AuthedClientAsync(email, RbacSuperAdminGucWebAppFactory.Password);
            var someTenant = Guid.NewGuid();

            // Suspend / reactivate are gated on platform.tenants.suspend — this caller lacks it → 403.
            var suspend = await caller.PutAsJsonAsync($"/api/v1/tenants/{someTenant}/suspend",
                new SetTenantStatusReasonRequest("attempt without the dangerous perm"));
            Assert.Equal(HttpStatusCode.Forbidden, suspend.StatusCode);

            var reactivate = await caller.PutAsJsonAsync($"/api/v1/tenants/{someTenant}/reactivate",
                new SetTenantStatusReasonRequest(null));
            Assert.Equal(HttpStatusCode.Forbidden, reactivate.StatusCode);

            // Positive control: the SAME caller DOES hold platform.tenants.update, so the edit gate passes and the
            // handler 404s on the unknown tenant (NOT 403). This proves the 403s above are specifically the missing
            // DANGEROUS permission — not a blanket-denied principal.
            var update = await caller.PutAsJsonAsync($"/api/v1/tenants/{someTenant}", new UpdateTenantRequest(
                "X", "X Pvt Ltd", "x@docslot.test", "+919800000104", null, null, null));
            Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", userId));
            await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", userId));
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email = @e", ("e", email));
            await ExecAsync("DELETE FROM platform.roles WHERE role_id = @r", ("r", roleId));
            await ExecAsync(
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{userId}@edit.test"), ("u", userId));
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    /// <summary>Seeds a platform user whose ONLY grant is platform.tenants.update (a platform-scoped custom role) —
    /// deliberately WITHOUT platform.tenants.suspend. Mirrors the SeedZeroPermissionUser idiom in
    /// ReadListEndpointsTests (custom platform role, tenant NULL).</summary>
    private static async Task SeedUpdateOnlyPlatformAdminAsync(Guid userId, string email, Guid roleId, string roleKey)
    {
        await ExecAsync(
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Update-Only Admin', true, true, NOW(), NOW())
            """,
            ("id", userId), ("email", email), ("pwd", RbacSuperAdminGucWebAppFactory.Password));
        await ExecAsync(
            """
            INSERT INTO platform.roles (role_id, role_key, name, scope, tenant_id, is_system, created_at, updated_at)
            VALUES (@r, @k, 'Tenants Update Only', 'platform', NULL, false, NOW(), NOW())
            """,
            ("r", roleId), ("k", roleKey));
        await ExecAsync(
            """
            INSERT INTO platform.role_permissions (role_id, permission_id)
            SELECT @r, p.permission_id FROM platform.permissions p WHERE p.permission_key = 'platform.tenants.update'
            """,
            ("r", roleId));
        await ExecAsync(
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (gen_random_uuid(), @u, NULL, @r, true, NOW())
            """,
            ("u", userId), ("r", roleId));
    }

    private static async Task SeedPlatformOnlySuperAdminAsync(Guid userId, string email)
    {
        await ExecAsync(
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Editing SA', true, true, NOW(), NOW())
            """,
            ("id", userId), ("email", email), ("pwd", RbacSuperAdminGucWebAppFactory.Password));
        await ExecAsync(
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            """,
            ("uid", userId));
    }

    private static async Task CleanupAsync(Guid saUserId, string saEmail, Guid tenantId)
    {
        if (tenantId != default)
            await ExecAsync("DELETE FROM platform.invitations WHERE tenant_id = @t", ("t", tenantId));
        await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", saUserId));
        await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", saUserId));
        await ExecAsync("DELETE FROM platform.login_attempts WHERE email = @e", ("e", saEmail));
        await ExecAsync(
            "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
            ("anon", $"deleted+{saUserId}@edit.test"), ("u", saUserId));
        if (tenantId != default)
            await ExecAsync(
                "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t",
                ("t", tenantId));
    }

    private async Task<HttpClient> AuthedClientAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, null));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new Npgsql.NpgsqlConnection(RbacSuperAdminGucWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new Npgsql.NpgsqlConnection(RbacSuperAdminGucWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : result.ToString();
    }
}
