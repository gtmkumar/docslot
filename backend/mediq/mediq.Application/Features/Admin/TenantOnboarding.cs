using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Admin;

// ============================================================================
// Tenant onboarding — the platform console's "create a clinic" path. One
// command, one transaction: the tenants row AND the initial tenant_owner
// invitation commit (or roll back) together, so there is never an orphan
// tenant nobody can enter, and never a live owner token for a tenant that
// failed to create. Passwords are never involved: the owner sets their own
// credential when they redeem the invitation (platform.accept_invitation).
// ============================================================================

/// <summary>Onboard a tenant + mint its owner invitation. Gated on <c>platform.tenants.create</c> at the
/// controller. Carries a one-time plaintext token in the result → never idempotency-cached.</summary>
public sealed record CreateTenantCommand(CreateTenantRequest Request)
    : ICommand<CreateTenantResult>, IDoNotCacheResponse;

public sealed class CreateTenantValidator : AbstractValidator<CreateTenantCommand>
{
    /// <summary>The tenant_type vocabulary from database/01_platform_core.sql (drives menu filtering via
    /// navigation_menus.applies_to_tenant_types — an unknown type would silently blank parts of the nav).</summary>
    internal static readonly string[] TenantTypes =
        ["hospital", "individual_doctor", "pathology_lab", "mobile_lab_operator", "pharmacy"];

    public CreateTenantValidator()
    {
        RuleFor(x => x.Request.TenantCode).NotEmpty().Matches("^[a-z0-9][a-z0-9-]{2,49}$")
            .WithMessage("Tenant code must be 3-50 chars of lowercase letters, digits and hyphens.");
        RuleFor(x => x.Request.LegalName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Request.DisplayName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Request.TenantType).Must(t => TenantTypes.Contains(t))
            .WithMessage($"Tenant type must be one of: {string.Join(", ", TenantTypes)}.");
        RuleFor(x => x.Request.PrimaryEmail).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.Request.PrimaryPhone).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Request.City).MaximumLength(100);
        RuleFor(x => x.Request.State).MaximumLength(100);
        RuleFor(x => x.Request.PinCode).Matches("^[1-9][0-9]{5}$")
            .When(x => !string.IsNullOrWhiteSpace(x.Request.PinCode))
            .WithMessage("PIN code must be 6 digits and cannot start with 0.");
        // Geo tag comes as a pair (both or neither) and must be plausible coordinates.
        RuleFor(x => x.Request.Latitude).InclusiveBetween(-90m, 90m).When(x => x.Request.Latitude is not null);
        RuleFor(x => x.Request.Longitude).InclusiveBetween(-180m, 180m).When(x => x.Request.Longitude is not null);
        RuleFor(x => x.Request)
            .Must(r => r.Latitude is null == r.Longitude is null)
            .WithMessage("Latitude and longitude must be provided together.");
        RuleFor(x => x.Request.AdminEmail).NotEmpty().EmailAddress().MaximumLength(255);
    }
}

public sealed class CreateTenantCommandHandler(
    ITenantRepository tenants, IRoleAssignmentRepository roles,
    IInvitationRepository invitations, IInvitationTokenFactory tokens,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateTenantCommand, CreateTenantResult>
{
    /// <summary>Owner invitations reuse the standard invitation TTL (7 days).</summary>
    private static readonly TimeSpan InviteTtl = TimeSpan.FromDays(7);

    public async Task<CreateTenantResult> Handle(CreateTenantCommand command, CancellationToken ct)
    {
        var req = command.Request;

        // The initial role is ALWAYS the system tenant_owner — the request never chooses a role, so this
        // endpoint can never be steered into minting a platform-scoped (or arbitrary) grant.
        var ownerRole = (await roles.ListRolesAsync(null, ct)).FirstOrDefault(r => r.RoleKey == "tenant_owner")
            ?? throw new BusinessRuleException("System role 'tenant_owner' is missing from this environment.");

        var tenantId = await tenants.CreateAsync(
            req.TenantCode.Trim(), req.LegalName.Trim(), req.DisplayName.Trim(), req.TenantType,
            req.PrimaryEmail.Trim(), req.PrimaryPhone.Trim(),
            string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim(),
            string.IsNullOrWhiteSpace(req.State) ? null : req.State.Trim(),
            string.IsNullOrWhiteSpace(req.PinCode) ? null : req.PinCode.Trim(),
            req.Latitude, req.Longitude, ct);

        // Owner invitation via the SAME definer path the Team screen uses: create_invitation re-checks the
        // actor (super_admin passes; anyone else needs tenant.users.create + may-confer in the NEW tenant)
        // and enforces the one-live-pending-per-email rule. Same transaction → atomic with the tenant insert.
        var (token, tokenHash) = tokens.Create();
        var expiresAt = clock.UtcNow.Add(InviteTtl);
        var invitationId = await invitations.CreateAsync(
            ctx.UserId!.Value, tenantId, req.AdminEmail.Trim(), ownerRole.RoleId, tokenHash, expiresAt, ct);

        // Audit AFTER both business writes (auditor: closes the orphan-audit window — a failed invitation
        // rolls back the tenant insert, so nothing is recorded for a tenant that never persisted). Audit rows
        // write on a DEDICATED connection so they survive rollbacks — which also means they cannot see this
        // transaction's uncommitted tenant row: TenantId therefore stays NULL (a platform-scope act) and the
        // new tenant is identified by ResourceId + the summary instead of the FK link. The EMAIL + role are
        // recorded, never the token/hash (the token is a live credential).
        await audit.RecordAsync(new AuditEntry(
            "create", "tenant", tenantId, req.TenantCode, ctx.UserId, TenantId: null,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Onboarded tenant '{req.DisplayName}' ({req.TenantType}) as {req.TenantCode} [{tenantId}]"), ct);
        await audit.RecordAsync(new AuditEntry(
            "create", "invitation", invitationId, req.AdminEmail, ctx.UserId, TenantId: null,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Invited {req.AdminEmail} as tenant_owner of new tenant {req.TenantCode} [{tenantId}]"), ct);

        return new CreateTenantResult(
            tenantId, req.TenantCode.Trim(), req.DisplayName.Trim(),
            invitationId, token, expiresAt, req.AdminEmail.Trim());
    }
}
