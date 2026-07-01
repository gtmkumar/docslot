using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Admin;

// ============================================================================
// Branch / department membership SCOPE (epic #80, Phase C — issue #90).
// An ORGANIZATIONAL DISPLAY attribute: it powers the People "All branches"
// filter, the "N branches" stat, and the per-member scope label. It is NEVER an
// access boundary — nothing here consults or mutates permission resolution.
// ============================================================================

// ---- List a tenant's branches (read) -------------------------------------------------------------

public sealed record ListTenantBranchesQuery(Guid TenantId) : IQuery<IReadOnlyList<BranchDto>>;

public sealed class ListTenantBranchesQueryHandler(IBranchDirectory branches)
    : IQueryHandler<ListTenantBranchesQuery, IReadOnlyList<BranchDto>>
{
    public Task<IReadOnlyList<BranchDto>> Handle(ListTenantBranchesQuery query, CancellationToken ct)
        => branches.ListActiveByTenantAsync(query.TenantId, ct);
}

// ---- Create a branch (command) -------------------------------------------------------------------

public sealed record CreateBranchCommand(Guid TenantId, CreateBranchRequest Request) : ICommand<CreateBranchResult>;

public sealed class CreateBranchValidator : AbstractValidator<CreateBranchCommand>
{
    public CreateBranchValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.Code).MaximumLength(50).When(x => !string.IsNullOrEmpty(x.Request.Code));
    }
}

public sealed class CreateBranchCommandHandler(
    IBranchRepository branches, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<CreateBranchCommand, CreateBranchResult>
{
    public async Task<CreateBranchResult> Handle(CreateBranchCommand command, CancellationToken ct)
    {
        var req = command.Request;
        // Direct own-tenant insert under RLS (branches confer no permissions → no definer needed). The
        // ambient UnitOfWork commits it; RLS's WITH CHECK bounds the row to the acting tenant.
        var branchId = await branches.CreateAsync(command.TenantId, req.Name.Trim(), req.Code?.Trim(), ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "branch", branchId, req.Name.Trim(), ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created branch '{req.Name.Trim()}' in tenant {command.TenantId}"), ct);

        return new CreateBranchResult(branchId);
    }
}

// ---- Set a member's org scope (command) ----------------------------------------------------------

public sealed record SetMemberScopeCommand(Guid TenantId, Guid UserId, SetMemberScopeRequest Request)
    : ICommand<SetMemberScopeResult>;

public sealed class SetMemberScopeValidator : AbstractValidator<SetMemberScopeCommand>
{
    public SetMemberScopeValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Request.Department).MaximumLength(120)
            .When(x => !string.IsNullOrEmpty(x.Request.Department));
    }
}

public sealed class SetMemberScopeCommandHandler(
    IMembershipScopeWriter scope, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<SetMemberScopeCommand, SetMemberScopeResult>
{
    public async Task<SetMemberScopeResult> Handle(SetMemberScopeCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var department = string.IsNullOrWhiteSpace(req.Department) ? null : req.Department.Trim();

        // Resolve the target's scope-bearing active membership in this tenant. A non-member yields no row →
        // 403 (mirrors the lifecycle no-membership posture; avoids a 403/404 enumeration oracle).
        var membershipId = await scope.FindScopeMembershipAsync(command.UserId, command.TenantId, ct)
            ?? throw new ForbiddenException("User is not an active member of this tenant.");

        // platform.set_membership_scope (SECURITY DEFINER) re-checks tenant.users.update, validates the
        // branch belongs to the tenant, and writes ONLY branch_id/department — never role_id. Because it
        // cannot touch role/permission data, this can NEVER change the user's effective access.
        var utrId = await scope.SetScopeAsync(ctx.UserId!.Value, membershipId, req.BranchId, department, ct);

        await audit.RecordAsync(new AuditEntry(
            "set_scope", "user_tenant_role", utrId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Set org scope for user {command.UserId}: branch={req.BranchId?.ToString() ?? "(all)"}, department={department ?? "(all)"}"), ct);

        return new SetMemberScopeResult(utrId, req.BranchId, department);
    }
}
