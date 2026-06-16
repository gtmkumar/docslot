namespace mediq.SharedDataModel.Docslot.Navigation;

/// <summary>
/// The caller's effective, flat set of permission keys for the active tenant.
/// <para>
/// Mirrors <c>platform.resolve_user_permissions(p_user_id, p_tenant_id)</c>
/// (database/08_rbac_navigation.sql), which returns a single-query effective set:
/// role grants MINUS deny-overrides PLUS grant-overrides (deny wins; time-boxed
/// overrides respected). The SQL function comment is explicit that callers should invoke
/// it ONCE per request and check the returned set IN MEMORY — never hit the DB per
/// permission check. This DTO is that per-request snapshot.
/// </para>
/// <para>
/// Per CLAUDE.md: NO hardcoded role checks anywhere. The frontend and API both authorize
/// against these permission KEYS (e.g. <c>docslot.booking.approve</c>), never against role
/// names. Each entry maps to one <c>platform.permissions.permission_key</c>.
/// </para>
/// </summary>
/// <param name="UserId">The user the set was resolved for. Maps to <c>platform.users.user_id</c>.</param>
/// <param name="TenantId">
/// The tenant scope the set was resolved within. Maps to <c>platform.tenants.tenant_id</c>.
/// Permissions are tenant-scoped, so the set is only valid for this tenant.
/// </param>
/// <param name="PermissionKeys">
/// The flat, de-duplicated set of effective permission keys
/// (rows of <c>resolve_user_permissions().permission_key</c>). A set so in-memory
/// <c>Contains</c> checks are O(1).
/// </param>
public sealed record PermissionSetDto(
    Guid UserId,
    Guid TenantId,
    IReadOnlySet<string> PermissionKeys);
