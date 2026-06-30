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
        // No password rule: a password is never accepted from the admin (the invite seeds a server-generated
        // temp credential + must-change-password). Any supplied Password is ignored by the provisioner.
    }
}

public sealed class CreateUserCommandHandler(
    IUserProvisioning provisioning, IRoleAssignmentRepository roles,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateUserCommand, CreateUserResult>
{
    public async Task<CreateUserResult> Handle(CreateUserCommand command, CancellationToken ct)
    {
        var (userId, alreadyExisted) = await provisioning.CreateAsync(command.Request, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            alreadyExisted ? "link_user" : "create", "user", userId, command.Request.Email, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: alreadyExisted
                ? $"Linked existing user {command.Request.Email} to tenant {command.TenantId}"
                : $"Created user {command.Request.Email} in tenant {command.TenantId}"), ct);

        // Initial-role assignment routes through the SAME escalation-safe definer path that
        // AssignRoleCommandHandler uses (no-escalation + SoD guards), INSIDE this command's UoW transaction —
        // so a 403 here rolls the whole create back (no orphan auth-less user). Closes the escalation-by-proxy
        // hole where the previous raw INSERT let tenant.users.create alone mint a user holding any role.
        if (command.Request.InitialRoleId is { } roleId)
        {
            var userTenantRoleId = await roles.AssignRoleAsync(ctx.UserId!.Value, userId, roleId, command.TenantId, ct);
            await audit.RecordAsync(new AuditEntry(
                "assign_role", "user_tenant_role", userTenantRoleId, null, ctx.UserId, command.TenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                ChangeSummary: $"Assigned initial role {roleId} to user {userId}"), ct);
        }

        return new CreateUserResult(userId, alreadyExisted);
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

        // platform.assign_role_to_user (SECURITY DEFINER) generates the id, enforces the grant-option
        // escalation guard (→ 403 on SQLSTATE 42501) and the SoD trigger (→ 409 on 23000), and ON CONFLICT
        // un-revokes — so this is idempotent at the DB too. The actor is the authenticated principal.
        var userTenantRoleId = await roles.AssignRoleAsync(
            ctx.UserId!.Value, req.UserId, req.RoleId, req.TenantId, ct);

        await audit.RecordAsync(new AuditEntry(
            "assign_role", "user_tenant_role", userTenantRoleId, null, ctx.UserId, req.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Assigned role {req.RoleId} to user {req.UserId}"), ct);

        return new AssignRoleResult(userTenantRoleId);
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
    IRoleAssignmentRepository roles, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<SetOverrideCommand, SetOverrideResult>
{
    public async Task<SetOverrideResult> Handle(SetOverrideCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var permissionId = await roles.FindPermissionIdAsync(req.PermissionKey, ct)
            ?? throw new KeyNotFoundException($"Unknown permission '{req.PermissionKey}'.");

        // platform.set_user_permission_override (SECURITY DEFINER) generates the override id and enforces the
        // grant-option guard: granting a permission the actor does NOT themselves hold raises SQLSTATE 42501
        // (→ 403). effective_from defaults to now in the function when null.
        var overrideId = await roles.SetPermissionOverrideAsync(
            ctx.UserId!.Value, req.UserId, permissionId, req.TenantId, req.IsAllowed, req.Reason,
            effectiveFrom: null, expiresAt: req.ExpiresAt, ct);

        await audit.RecordAsync(new AuditEntry(
            "grant_override", "user_permission_override", overrideId, req.PermissionKey,
            ctx.UserId, req.TenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"{(req.IsAllowed ? "GRANT" : "DENY")} {req.PermissionKey} to user {req.UserId}: {req.Reason}"), ct);

        return new SetOverrideResult(overrideId);
    }
}

// ---- Set user status — deactivate / reactivate within a tenant (command) -------------------------

public sealed record SetUserStatusCommand(Guid TenantId, Guid UserId, SetUserStatusRequest Request)
    : ICommand<SetUserStatusResult>;

public sealed class SetUserStatusValidator : AbstractValidator<SetUserStatusCommand>
{
    public SetUserStatusValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
        // A reason is mandatory to DEACTIVATE (the DB enforces it for both directions; the handler supplies a
        // default for reactivate so the client need not type one to re-enable a user).
        RuleFor(x => x.Request.Reason).NotEmpty().When(x => !x.Request.IsActive)
            .WithMessage("A reason is required to deactivate a user.");
    }
}

public sealed class SetUserStatusCommandHandler(
    IUserLifecycle lifecycle, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<SetUserStatusCommand, SetUserStatusResult>
{
    public async Task<SetUserStatusResult> Handle(SetUserStatusCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var reason = string.IsNullOrWhiteSpace(req.Reason)
            ? (req.IsActive ? "Reactivated from Team & roles" : "Deactivated from Team & roles")
            : req.Reason.Trim();

        // Definer fn enforces permission re-check, tenant-membership scoping, self-guard, last-admin guard.
        // Actor is the authenticated principal — never a body field.
        await lifecycle.SetActiveAsync(ctx.UserId!.Value, command.UserId, command.TenantId, req.IsActive, reason, ct);

        await audit.RecordAsync(new AuditEntry(
            req.IsActive ? "reactivate" : "deactivate", "user", command.UserId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"{(req.IsActive ? "Reactivated" : "Deactivated")} user {command.UserId} in tenant {command.TenantId}: {reason}"), ct);

        return new SetUserStatusResult(command.UserId, req.IsActive);
    }
}

// ---- Update user profile (command) ---------------------------------------------------------------

public sealed record UpdateUserProfileCommand(Guid TenantId, Guid UserId, UpdateUserProfileRequest Request)
    : ICommand<UpdateUserProfileResult>;

public sealed class UpdateUserProfileValidator : AbstractValidator<UpdateUserProfileCommand>
{
    public UpdateUserProfileValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Request.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.PreferredLanguage).Must(l => l is "en" or "hi")
            .WithMessage("Preferred language must be en or hi.");
        RuleFor(x => x.Request.Phone).MaximumLength(15).When(x => !string.IsNullOrEmpty(x.Request.Phone));
    }
}

public sealed class UpdateUserProfileCommandHandler(
    IUserLifecycle lifecycle, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<UpdateUserProfileCommand, UpdateUserProfileResult>
{
    public async Task<UpdateUserProfileResult> Handle(UpdateUserProfileCommand command, CancellationToken ct)
    {
        var req = command.Request;
        await lifecycle.UpdateProfileAsync(
            ctx.UserId!.Value, command.UserId, command.TenantId, req.FullName, req.Phone, req.PreferredLanguage, ct);

        // PHI: log CHANGED-FIELD NAMES only — never the phone value (it would land in the hash-chained audit_log).
        await audit.RecordAsync(new AuditEntry(
            "update", "user", command.UserId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Updated profile (name, phone, language) for user {command.UserId}"), ct);

        return new UpdateUserProfileResult(command.UserId);
    }
}

// ---- Reset user access — force password change + clear lockout (command) -------------------------

public sealed record ResetAccessCommand(Guid TenantId, Guid UserId, ResetAccessRequest Request)
    : ICommand<ResetAccessResult>;

public sealed class ResetAccessValidator : AbstractValidator<ResetAccessCommand>
{
    public ResetAccessValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("A reason is required to reset access.");
    }
}

public sealed class ResetAccessCommandHandler(
    IUserLifecycle lifecycle, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<ResetAccessCommand, ResetAccessResult>
{
    public async Task<ResetAccessResult> Handle(ResetAccessCommand command, CancellationToken ct)
    {
        // Flags only — no plaintext generated/returned/stored. Definer self-guards (no self-reset).
        await lifecycle.ResetAccessAsync(ctx.UserId!.Value, command.UserId, command.TenantId, command.Request.Reason.Trim(), ct);

        await audit.RecordAsync(new AuditEntry(
            "reset_access", "user", command.UserId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Reset access (force password change + unlock) for user {command.UserId}: {command.Request.Reason.Trim()}"), ct);

        return new ResetAccessResult(command.UserId);
    }
}
