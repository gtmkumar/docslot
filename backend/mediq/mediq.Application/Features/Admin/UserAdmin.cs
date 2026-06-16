using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;

namespace mediq.Application.Features.Admin;

// ---- List users in a tenant (read) ---------------------------------------------------------------

/// <summary>
/// Lists users in a tenant. Joins through <c>user_tenant_roles</c>. Tenant scoping is enforced by the
/// read repository's WHERE on tenant_id (composite indexes lead with tenant_id, per schema invariant).
/// </summary>
public sealed record ListTenantUsersQuery(Guid TenantId, int Skip = 0, int Take = 50)
    : IQuery<IReadOnlyList<UserListItemDto>>;

public sealed class ListTenantUsersQueryHandler(IUserDirectory directory)
    : IQueryHandler<ListTenantUsersQuery, IReadOnlyList<UserListItemDto>>
{
    public Task<IReadOnlyList<UserListItemDto>> Handle(ListTenantUsersQuery query, CancellationToken ct)
        => directory.ListByTenantAsync(query.TenantId, query.Skip, Math.Clamp(query.Take, 1, 200), ct);
}

// ---- Create user (command) -----------------------------------------------------------------------

public sealed record CreateUserCommand(Guid TenantId, CreateUserRequest Request) : ICommand<CreateUserResult>;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Request.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.Password)
            .MinimumLength(8).When(x => !string.IsNullOrEmpty(x.Request.Password));
    }
}

public sealed class CreateUserCommandHandler(
    IUserProvisioning provisioning, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateUserCommand, CreateUserResult>
{
    public async Task<CreateUserResult> Handle(CreateUserCommand command, CancellationToken ct)
    {
        var userId = await provisioning.CreateAsync(command.TenantId, command.Request, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "user", userId, command.Request.Email, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created user {command.Request.Email} in tenant {command.TenantId}"), ct);

        return new CreateUserResult(userId);
    }
}

// ---- Assign role (command) -----------------------------------------------------------------------

public sealed record AssignRoleCommand(AssignRoleRequest Request) : ICommand<AssignRoleResult>;

public sealed class AssignRoleValidator : AbstractValidator<AssignRoleCommand>
{
    public AssignRoleValidator()
    {
        RuleFor(x => x.Request.UserId).NotEmpty();
        RuleFor(x => x.Request.RoleId).NotEmpty();
        RuleFor(x => x.Request.ExpiresAt)
            .Must(d => d is null || d > DateTime.UtcNow)
            .WithMessage("ExpiresAt must be in the future.");
    }
}

public sealed class AssignRoleCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<AssignRoleCommand, AssignRoleResult>
{
    public async Task<AssignRoleResult> Handle(AssignRoleCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var existing = await roles.FindAssignmentAsync(req.UserId, req.TenantId, req.RoleId, ct);
        if (existing is not null && existing.IsActive(clock.UtcNow))
            return new AssignRoleResult(existing.UserTenantRoleId);   // idempotent re-assign

        var assignment = mediq.Domain.Platform.UserTenantRole.Assign(
            req.UserId, req.TenantId, req.RoleId, ctx.UserId, clock.UtcNow, req.ExpiresAt, req.IsPrimary);
        await roles.AddAssignmentAsync(assignment, ct);

        await audit.RecordAsync(new AuditEntry(
            "assign_role", "user_tenant_role", assignment.UserTenantRoleId, null, ctx.UserId, req.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Assigned role {req.RoleId} to user {req.UserId}"), ct);

        return new AssignRoleResult(assignment.UserTenantRoleId);
    }
}

// ---- Set permission override (command) -----------------------------------------------------------

public sealed record SetOverrideCommand(SetOverrideRequest Request) : ICommand<SetOverrideResult>;

public sealed class SetOverrideValidator : AbstractValidator<SetOverrideCommand>
{
    public SetOverrideValidator()
    {
        RuleFor(x => x.Request.UserId).NotEmpty();
        RuleFor(x => x.Request.PermissionKey).NotEmpty();
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("An override reason is mandatory.");
    }
}

public sealed class SetOverrideCommandHandler(
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<SetOverrideCommand, SetOverrideResult>
{
    public async Task<SetOverrideResult> Handle(SetOverrideCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var permissionId = await roles.FindPermissionIdAsync(req.PermissionKey, ct)
            ?? throw new KeyNotFoundException($"Unknown permission '{req.PermissionKey}'.");

        var ovr = mediq.Domain.Platform.UserPermissionOverride.Create(
            req.UserId, permissionId, req.TenantId, req.IsAllowed, req.Reason,
            ctx.UserId, clock.UtcNow, req.ExpiresAt);
        await roles.AddOverrideAsync(ovr, ct);

        await audit.RecordAsync(new AuditEntry(
            "grant_override", "user_permission_override", ovr.OverrideId, req.PermissionKey,
            ctx.UserId, req.TenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"{(req.IsAllowed ? "GRANT" : "DENY")} {req.PermissionKey} to user {req.UserId}: {req.Reason}"), ct);

        return new SetOverrideResult(ovr.OverrideId);
    }
}
