using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Admin;

// ---- Create role (command) -----------------------------------------------------------------------

/// <summary>Creates a custom (non-system) role. Gated by <c>platform.roles.manage</c> at the API.</summary>
public sealed record CreateRoleCommand(CreateRoleRequest Request) : ICommand<CreateRoleResult>;

public sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator()
    {
        RuleFor(x => x.Request.RoleKey).NotEmpty().MaximumLength(50)
            .Matches("^[a-z0-9_]+$").WithMessage("RoleKey must be lower_snake_case.");
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.Scope).Must(s => s is "tenant" or "platform")
            .WithMessage("Scope must be 'tenant' or 'platform'.");
        RuleFor(x => x.Request.TenantId).NotNull()
            .When(x => x.Request.Scope == "tenant")
            .WithMessage("A tenant-scoped role requires a TenantId.");
    }
}

public sealed class CreateRoleCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateRoleCommand, CreateRoleResult>
{
    public async Task<CreateRoleResult> Handle(CreateRoleCommand command, CancellationToken ct)
    {
        var req = command.Request;
        if (await roles.RoleKeyExistsAsync(req.RoleKey, req.TenantId, ct))
            throw new BusinessRuleException($"A role with key '{req.RoleKey}' already exists in this scope.");

        // platform.create_custom_role (SECURITY DEFINER) generates the id and enforces the manage-roles
        // privilege guard at the DB (→ 403 on SQLSTATE 42501). The actor is the authenticated principal.
        var roleId = await roles.CreateCustomRoleAsync(
            ctx.UserId!.Value, req.RoleKey, req.Name, req.Description, req.TenantId, req.Scope, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "role", roleId, req.RoleKey, ctx.UserId, req.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created custom role {req.RoleKey} ({req.Scope})"), ct);

        return new CreateRoleResult(roleId);
    }
}

// ---- Revoke role assignment (command) ------------------------------------------------------------

/// <summary>
/// Revokes an existing <c>user_tenant_roles</c> assignment (soft — sets revoked_at/by/reason; the row is
/// never deleted, preserving history). Idempotent: re-revoking an already-revoked assignment is a no-op.
/// </summary>
public sealed record RevokeRoleCommand(RevokeRoleRequest Request) : ICommand<RevokeRoleResult>;

public sealed class RevokeRoleValidator : AbstractValidator<RevokeRoleCommand>
{
    public RevokeRoleValidator()
    {
        RuleFor(x => x.Request.UserTenantRoleId).NotEmpty();
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("A revoke reason is mandatory.");
    }
}

public sealed class RevokeRoleCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<RevokeRoleCommand, RevokeRoleResult>
{
    public async Task<RevokeRoleResult> Handle(RevokeRoleCommand command, CancellationToken ct)
    {
        var req = command.Request;

        // platform.revoke_role_assignment (SECURITY DEFINER) performs the soft-revoke and enforces the
        // assign-roles privilege guard (→ 403 on SQLSTATE 42501). It returns false when the assignment was
        // already revoked (idempotent), preserving the AlreadyRevoked semantics without a prior read.
        var didRevoke = await roles.RevokeAssignmentAsync(
            ctx.UserId!.Value, req.UserTenantRoleId, req.Reason, ct);

        if (!didRevoke)
            return new RevokeRoleResult(req.UserTenantRoleId, AlreadyRevoked: true);

        await audit.RecordAsync(new AuditEntry(
            "revoke_role", "user_tenant_role", req.UserTenantRoleId, null,
            ctx.UserId, ctx.TenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Revoked assignment {req.UserTenantRoleId}: {req.Reason}"), ct);

        return new RevokeRoleResult(req.UserTenantRoleId, AlreadyRevoked: false);
    }
}
