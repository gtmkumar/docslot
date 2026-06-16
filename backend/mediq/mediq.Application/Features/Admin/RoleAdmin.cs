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

        var role = mediq.Domain.Platform.Role.CreateCustom(
            req.RoleKey, req.Name, req.Description, req.TenantId, req.Scope, clock.UtcNow);
        await roles.AddRoleAsync(role, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "role", role.RoleId, req.RoleKey, ctx.UserId, req.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created custom role {req.RoleKey} ({req.Scope})"), ct);

        return new CreateRoleResult(role.RoleId);
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
        var assignment = await roles.GetAssignmentByIdAsync(req.UserTenantRoleId, ct)
            ?? throw new KeyNotFoundException("Role assignment not found.");

        if (assignment.RevokedAt is not null)
            return new RevokeRoleResult(assignment.UserTenantRoleId, AlreadyRevoked: true);

        // Mutating a tracked entity; the UnitOfWork behavior commits the UPDATE.
        assignment.Revoke(ctx.UserId, req.Reason, clock.UtcNow);

        await audit.RecordAsync(new AuditEntry(
            "revoke_role", "user_tenant_role", assignment.UserTenantRoleId, null,
            ctx.UserId, assignment.TenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Revoked assignment {assignment.UserTenantRoleId}: {req.Reason}"), ct);

        return new RevokeRoleResult(assignment.UserTenantRoleId, AlreadyRevoked: false);
    }
}
