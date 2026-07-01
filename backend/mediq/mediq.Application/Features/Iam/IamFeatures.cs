using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Iam;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Iam;

// ============================================================================
// IAM — Roles & permissions admin (the "Team & roles" screen).
// Reads project the canonical RBAC catalog; writes go through the SECURITY
// DEFINER functions, which enforce the escalation / system-role / SoD guards at
// the database. The actor is ALWAYS the authenticated principal, never a body.
// ============================================================================

// ---- Queries -------------------------------------------------------------------------------------

/// <summary>Lists the modules (resource groups) that head the privilege matrix.</summary>
public sealed record ListModulesQuery : IQuery<IReadOnlyList<ModuleDto>>;

public sealed class ListModulesQueryHandler(IIamReadService iam)
    : IQueryHandler<ListModulesQuery, IReadOnlyList<ModuleDto>>
{
    public Task<IReadOnlyList<ModuleDto>> Handle(ListModulesQuery query, CancellationToken ct)
        => iam.ListModulesAsync(ct);
}

/// <summary>Lists catalog permissions, optionally narrowed to one module (resource key).</summary>
public sealed record ListPermissionsQuery(string? ResourceKey) : IQuery<IReadOnlyList<PermissionDto>>;

public sealed class ListPermissionsQueryHandler(IIamReadService iam)
    : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionDto>>
{
    public Task<IReadOnlyList<PermissionDto>> Handle(ListPermissionsQuery query, CancellationToken ct)
        => iam.ListPermissionsAsync(query.ResourceKey, ct);
}

/// <summary>The full grant matrix for a role (the heart of the screen).</summary>
public sealed record GetRoleMatrixQuery(Guid RoleId) : IQuery<RoleMatrixDto>;

public sealed class GetRoleMatrixQueryHandler(IIamReadService iam)
    : IQueryHandler<GetRoleMatrixQuery, RoleMatrixDto>
{
    public async Task<RoleMatrixDto> Handle(GetRoleMatrixQuery query, CancellationToken ct)
        => await iam.GetRoleMatrixAsync(query.RoleId, ct)
           ?? throw new KeyNotFoundException("Role not found.");
}

/// <summary>The effective (resolved) permission set for a user in a tenant.</summary>
public sealed record GetEffectiveAccessQuery(Guid UserId, Guid? TenantId) : IQuery<EffectiveAccessDto>;

public sealed class GetEffectiveAccessQueryHandler(IIamReadService iam, ICurrentUserContext ctx)
    : IQueryHandler<GetEffectiveAccessQuery, EffectiveAccessDto>
{
    public Task<EffectiveAccessDto> Handle(GetEffectiveAccessQuery query, CancellationToken ct)
        // Default to the caller's tenant: resolve_user_permissions(user, NULL) only returns platform-scoped
        // grants (a NULL tenant never equals a row's tenant_id), which would hide all tenant permissions.
        => iam.GetEffectiveAccessAsync(query.UserId, query.TenantId ?? ctx.TenantId, ct);
}

/// <summary>A user's effective permissions WITH source attribution (role | override_grant) for the explainer.</summary>
public sealed record GetEffectivePermissionsQuery(Guid UserId, Guid? TenantId) : IQuery<IReadOnlyList<EffectivePermissionDto>>;

public sealed class GetEffectivePermissionsQueryHandler(IIamReadService iam, ICurrentUserContext ctx)
    : IQueryHandler<GetEffectivePermissionsQuery, IReadOnlyList<EffectivePermissionDto>>
{
    public Task<IReadOnlyList<EffectivePermissionDto>> Handle(GetEffectivePermissionsQuery query, CancellationToken ct)
        => iam.GetEffectivePermissionsAsync(query.UserId, query.TenantId ?? ctx.TenantId, ct);
}

/// <summary>A user's currently-effective per-user permission overrides (deny-wins, time-boxed).</summary>
public sealed record ListUserOverridesQuery(Guid UserId, Guid? TenantId) : IQuery<IReadOnlyList<UserPermissionOverrideDto>>;

public sealed class ListUserOverridesQueryHandler(IIamReadService iam, ICurrentUserContext ctx)
    : IQueryHandler<ListUserOverridesQuery, IReadOnlyList<UserPermissionOverrideDto>>
{
    public Task<IReadOnlyList<UserPermissionOverrideDto>> Handle(ListUserOverridesQuery query, CancellationToken ct)
        => iam.ListUserOverridesAsync(query.UserId, query.TenantId ?? ctx.TenantId, ct);
}

/// <summary>ALL active per-user overrides for the caller's CURRENT tenant (the tenant-wide overrides tab).</summary>
public sealed record ListTenantOverridesQuery : IQuery<TenantOverridesListDto>;

public sealed class ListTenantOverridesQueryHandler(IIamReadService iam, ICurrentUserContext ctx)
    : IQueryHandler<ListTenantOverridesQuery, TenantOverridesListDto>
{
    public Task<TenantOverridesListDto> Handle(ListTenantOverridesQuery query, CancellationToken ct)
        // Tenant is bound STRICTLY from the server-signed context — never a client param — so the list can
        // only ever be the caller's own tenant. A null tenant (platform actor w/o a tenant) yields an empty
        // list (the strict = @p_tenant predicate matches nothing), never a cross-tenant disclosure.
        => iam.ListTenantOverridesAsync(ctx.TenantId, ct);
}

// ---- Grant a permission to a role (matrix checkbox ON) -------------------------------------------

public sealed record GrantRolePermissionCommand(Guid RoleId, Guid PermissionId, Guid? TenantId, bool Grantable)
    : ICommand<SetRolePermissionResult>;

public sealed class GrantRolePermissionCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<GrantRolePermissionCommand, SetRolePermissionResult>
{
    public async Task<SetRolePermissionResult> Handle(GrantRolePermissionCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId ?? ctx.TenantId;

        // platform.grant_permission_to_role (SECURITY DEFINER) enforces the escalation guard
        // (→ 403 on SQLSTATE 42501) and upserts the grant. Actor = authenticated principal.
        await roles.GrantPermissionToRoleAsync(
            ctx.UserId!.Value, command.RoleId, command.PermissionId, tenantId, command.Grantable, ct);

        await audit.RecordAsync(new AuditEntry(
            "grant_permission", "role", command.RoleId, command.PermissionId.ToString(),
            ctx.UserId, tenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Granted permission {command.PermissionId} to role {command.RoleId}"), ct);

        return new SetRolePermissionResult(command.RoleId, command.PermissionId, Granted: true);
    }
}

// ---- Revoke a permission from a role (matrix checkbox OFF) ---------------------------------------

public sealed record RevokeRolePermissionCommand(Guid RoleId, Guid PermissionId, Guid? TenantId)
    : ICommand<SetRolePermissionResult>;

public sealed class RevokeRolePermissionCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RevokeRolePermissionCommand, SetRolePermissionResult>
{
    public async Task<SetRolePermissionResult> Handle(RevokeRolePermissionCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId ?? ctx.TenantId;

        // platform.revoke_permission_from_role enforces the escalation + system-role guards (→ 403) and
        // returns false when the permission was not granted (idempotent). Either way the role no longer
        // grants it, so the result is Granted: false.
        var didRevoke = await roles.RevokePermissionFromRoleAsync(
            ctx.UserId!.Value, command.RoleId, command.PermissionId, tenantId, ct);

        if (didRevoke)
            await audit.RecordAsync(new AuditEntry(
                "revoke_permission", "role", command.RoleId, command.PermissionId.ToString(),
                ctx.UserId, tenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                ChangeSummary: $"Revoked permission {command.PermissionId} from role {command.RoleId}"), ct);

        return new SetRolePermissionResult(command.RoleId, command.PermissionId, Granted: false);
    }
}

// ---- Duplicate a role (the "Duplicate built-in role" gesture) ------------------------------------

public sealed record DuplicateRoleCommand(DuplicateRoleRequest Request) : ICommand<DuplicateRoleResult>;

public sealed class DuplicateRoleValidator : AbstractValidator<DuplicateRoleCommand>
{
    public DuplicateRoleValidator()
    {
        RuleFor(x => x.Request.SourceRoleId).NotEmpty();
        RuleFor(x => x.Request.NewRoleKey).NotEmpty().MaximumLength(50)
            .Matches("^[a-z0-9_]+$").WithMessage("RoleKey must be lower_snake_case.");
        RuleFor(x => x.Request.NewName).NotEmpty().MaximumLength(100);
    }
}

public sealed class DuplicateRoleCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<DuplicateRoleCommand, DuplicateRoleResult>
{
    public async Task<DuplicateRoleResult> Handle(DuplicateRoleCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var tenantId = req.TenantId ?? ctx.TenantId;

        if (await roles.RoleKeyExistsAsync(req.NewRoleKey, tenantId, ct))
            throw new BusinessRuleException($"A role with key '{req.NewRoleKey}' already exists in this scope.");

        // platform.duplicate_role (SECURITY DEFINER) clones the role + copies grants atomically, enforcing
        // the no-escalation rule for non-super actors (→ 403 on SQLSTATE 42501).
        var newRoleId = await roles.DuplicateRoleAsync(
            ctx.UserId!.Value, req.SourceRoleId, req.NewRoleKey, req.NewName, req.Description, tenantId, ct);

        await audit.RecordAsync(new AuditEntry(
            "duplicate_role", "role", newRoleId, req.NewRoleKey,
            ctx.UserId, tenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Duplicated role {req.SourceRoleId} → {req.NewRoleKey}"), ct);

        return new DuplicateRoleResult(newRoleId);
    }
}

// ---- Catalog plane: create a module (resource_type) ----------------------------------------------

public sealed record CreateModuleCommand(CreateModuleRequest Request) : ICommand<CreateModuleResult>;

public sealed class CreateModuleValidator : AbstractValidator<CreateModuleCommand>
{
    public CreateModuleValidator()
    {
        RuleFor(x => x.Request.ResourceKey).NotEmpty().MaximumLength(50)
            .Matches("^[a-z0-9_]+$").WithMessage("ResourceKey must be lower_snake_case.");
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class CreateModuleCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<CreateModuleCommand, CreateModuleResult>
{
    public async Task<CreateModuleResult> Handle(CreateModuleCommand command, CancellationToken ct)
    {
        var req = command.Request;
        // platform.create_resource_type (SECURITY DEFINER) enforces platform.permissions.manage at the DB
        // (→ 403 on 42501) and rejects a duplicate key (→ 409 on 23505).
        var id = await roles.CreateResourceTypeAsync(
            ctx.UserId!.Value, req.ResourceKey, req.Name, req.Description, req.DisplayOrder, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "resource_type", id, req.ResourceKey, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created module {req.ResourceKey}"), ct);

        return new CreateModuleResult(id);
    }
}

// ---- Catalog plane: create a permission ----------------------------------------------------------

public sealed record CreatePermissionCommand(CreatePermissionRequest Request) : ICommand<CreatePermissionResult>;

public sealed class CreatePermissionValidator : AbstractValidator<CreatePermissionCommand>
{
    public CreatePermissionValidator()
    {
        RuleFor(x => x.Request.PermissionKey).NotEmpty().MaximumLength(150)
            .Matches("^[a-z0-9_]+(\\.[a-z0-9_]+)+$")
            .WithMessage("PermissionKey must be dotted lower_snake_case, e.g. 'docslot.report.sign'.");
        RuleFor(x => x.Request.Resource).NotEmpty().Matches("^[a-z0-9_]+$");
        RuleFor(x => x.Request.Action).NotEmpty().Matches("^[a-z0-9_]+$");
        RuleFor(x => x.Request.Scope).Must(s => s is "platform" or "tenant" or "self")
            .WithMessage("Scope must be 'platform', 'tenant', or 'self'.");
        RuleFor(x => x.Request.Description).NotEmpty().MaximumLength(500);
    }
}

public sealed class CreatePermissionCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<CreatePermissionCommand, CreatePermissionResult>
{
    public async Task<CreatePermissionResult> Handle(CreatePermissionCommand command, CancellationToken ct)
    {
        var req = command.Request;
        // platform.create_permission (SECURITY DEFINER) enforces platform.permissions.manage (→ 403),
        // validates scope, ensures the action_type exists, and links the registries. A permission is inert
        // until application code checks it — this only adds the catalog row.
        var id = await roles.CreatePermissionAsync(
            ctx.UserId!.Value, req.PermissionKey, req.Resource, req.Action, req.Scope, req.Description,
            req.IsDangerous, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "permission", id, req.PermissionKey, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created permission {req.PermissionKey} ({req.Scope})"), ct);

        return new CreatePermissionResult(id);
    }
}

// ---- Module licensing (commercial display gate) --------------------------------------------------

public sealed record SetModuleLicenseCommand(Guid ResourceTypeId, SetModuleLicenseRequest Request)
    : ICommand<SetModuleLicenseResult>;

public sealed class SetModuleLicenseCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<SetModuleLicenseCommand, SetModuleLicenseResult>
{
    public async Task<SetModuleLicenseResult> Handle(SetModuleLicenseCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var tenantId = req.TenantId ?? ctx.TenantId
            ?? throw new BusinessRuleException("A tenant is required to set a module license.");

        // platform.set_module_license (SECURITY DEFINER) enforces platform.settings.update (→ 403). This is
        // a display gate only — it never changes permission resolution, just what the matrix greys out.
        var id = await roles.SetModuleLicenseAsync(
            ctx.UserId!.Value, tenantId, command.ResourceTypeId, req.IsLicensed, req.Reason, ct);

        await audit.RecordAsync(new AuditEntry(
            "set_module_license", "resource_type", command.ResourceTypeId, req.IsLicensed ? "licensed" : "unlicensed",
            ctx.UserId, tenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Module {command.ResourceTypeId} licensed={req.IsLicensed} for tenant {tenantId}"), ct);

        return new SetModuleLicenseResult(id, command.ResourceTypeId, req.IsLicensed);
    }
}
