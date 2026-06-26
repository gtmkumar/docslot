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
