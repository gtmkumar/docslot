using System.Globalization;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Security;

namespace mediq.Application.Features.Security;

// =====================================================================================================
// Tenant SECURITY-POLICY subsystem (issue #91). Management surface is tenant-scoped: the tenant is bound
// from the caller's signed JWT (never a client param), so a caller can only ever read/write THEIR tenant.
// =====================================================================================================

// ---- Read the effective policy (+ derived pending-enrolment count) -------------------------------

/// <summary>Reads the effective policy for a tenant (defaults merged) plus the "N staff will be prompted to
/// enrol" count. Gated by <c>tenant.settings.read</c>.</summary>
public sealed record GetSecurityPolicyQuery(Guid TenantId) : IQuery<SecurityPolicyDto>;

public sealed class GetSecurityPolicyQueryHandler(ITenantSecurityPolicyService policies)
    : IQueryHandler<GetSecurityPolicyQuery, SecurityPolicyDto>
{
    public async Task<SecurityPolicyDto> Handle(GetSecurityPolicyQuery q, CancellationToken ct)
    {
        var policy = await policies.GetAsync(q.TenantId, ct);
        var pending = await policies.CountStaffPendingMfaEnrolmentAsync(q.TenantId, policy, ct);
        return policy.ToDto(pending);
    }
}

// ---- Update the policy ---------------------------------------------------------------------------

/// <summary>Persists the tenant security policy under <c>tenants.settings-&gt;'security'</c>. Gated by
/// <c>tenant.settings.update</c>. Returns the re-read effective policy + fresh pending count.</summary>
public sealed record UpdateSecurityPolicyCommand(Guid TenantId, UpdateSecurityPolicyRequest Request)
    : ICommand<SecurityPolicyDto>;

public sealed class UpdateSecurityPolicyValidator : AbstractValidator<UpdateSecurityPolicyCommand>
{
    public UpdateSecurityPolicyValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.MfaPolicy)
            .Must(v => MfaPolicyTiers.All_.Contains(v))
            .WithMessage("mfaPolicy must be one of: optional, owners_admins, all.");
        RuleFor(x => x.Request.MinPasswordLength).InclusiveBetween(8, 128);
        RuleFor(x => x.Request.IdleTimeoutMinutes).InclusiveBetween(1, 1440);   // 1 minute .. 24 hours
        RuleFor(x => x.Request.LoginHoursStart).Must(BeHhmm).WithMessage("loginHoursStart must be HH:mm (24h).");
        RuleFor(x => x.Request.LoginHoursEnd).Must(BeHhmm).WithMessage("loginHoursEnd must be HH:mm (24h).");
    }

    private static bool BeHhmm(string v) =>
        TimeOnly.TryParseExact(v, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

public sealed class UpdateSecurityPolicyCommandHandler(
    ITenantSecurityPolicyService policies, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<UpdateSecurityPolicyCommand, SecurityPolicyDto>
{
    public async Task<SecurityPolicyDto> Handle(UpdateSecurityPolicyCommand command, CancellationToken ct)
    {
        var r = command.Request;
        var policy = new SecurityPolicy(
            r.MfaPolicy, r.MinPasswordLength, r.IdleTimeoutMinutes, r.RequireNewDeviceVerification,
            r.RestrictLoginHours, r.LoginHoursStart, r.LoginHoursEnd, r.DoctorsExemptFromHours,
            r.IpAllowlistEnabled, r.MaskSensitiveForReceptionist);

        await policies.SaveAsync(command.TenantId, policy, ct);

        await audit.RecordAsync(new AuditEntry(
            "update", "tenant_settings", command.TenantId, "security_policy", ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Security policy updated (mfa={r.MfaPolicy}, hours={r.RestrictLoginHours}, ipAllowlist={r.IpAllowlistEnabled})",
            Purpose: "security"), ct);

        var pending = await policies.CountStaffPendingMfaEnrolmentAsync(command.TenantId, policy, ct);
        return policy.ToDto(pending);
    }
}

// ---- IP allow-list management (platform.ip_allowlist) --------------------------------------------

/// <summary>Lists this tenant's IP allow-list entries. Gated by <c>platform.ip_allowlist.manage</c>.</summary>
public sealed record ListIpAllowlistQuery(Guid TenantId) : IQuery<IReadOnlyList<IpAllowlistEntryDto>>;

public sealed class ListIpAllowlistQueryHandler(IIpAllowlistService ip)
    : IQueryHandler<ListIpAllowlistQuery, IReadOnlyList<IpAllowlistEntryDto>>
{
    public Task<IReadOnlyList<IpAllowlistEntryDto>> Handle(ListIpAllowlistQuery q, CancellationToken ct)
        => ip.ListAsync(q.TenantId, ct);
}

/// <summary>Adds a CIDR entry to this tenant's allow-list. Gated by <c>platform.ip_allowlist.manage</c>.</summary>
public sealed record AddIpAllowlistCommand(Guid TenantId, AddIpAllowlistRequest Request) : ICommand<Guid>;

public sealed class AddIpAllowlistValidator : AbstractValidator<AddIpAllowlistCommand>
{
    public AddIpAllowlistValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.CidrRange).NotEmpty()
            .Must(BeCidrOrIp).WithMessage("cidrRange must be a valid IPv4/IPv6 address or CIDR (e.g. 203.0.113.0/24).");
        RuleFor(x => x.Request.Label).MaximumLength(100);
    }

    /// <summary>Accepts a bare IP (→ /32 or /128) or an explicit CIDR block.</summary>
    private static bool BeCidrOrIp(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        if (System.Net.IPAddress.TryParse(v, out _)) return true;
        return System.Net.IPNetwork.TryParse(v, out _);
    }
}

public sealed class AddIpAllowlistCommandHandler(
    IIpAllowlistService ip, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<AddIpAllowlistCommand, Guid>
{
    public async Task<Guid> Handle(AddIpAllowlistCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var id = await ip.AddAsync(
            command.TenantId, userId, command.Request.CidrRange.Trim(), command.Request.Label, command.Request.ExpiresAt, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "ip_allowlist", id, command.Request.CidrRange, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"IP allow-list entry added ({command.Request.CidrRange})", Purpose: "security"), ct);
        return id;
    }
}

/// <summary>Deactivates (soft-deletes) an allow-list entry. Gated by <c>platform.ip_allowlist.manage</c>.</summary>
public sealed record RemoveIpAllowlistCommand(Guid TenantId, Guid AllowlistId) : ICommand<bool>;

public sealed class RemoveIpAllowlistValidator : AbstractValidator<RemoveIpAllowlistCommand>
{
    public RemoveIpAllowlistValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.AllowlistId).NotEmpty();
    }
}

public sealed class RemoveIpAllowlistCommandHandler(
    IIpAllowlistService ip, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RemoveIpAllowlistCommand, bool>
{
    public async Task<bool> Handle(RemoveIpAllowlistCommand command, CancellationToken ct)
    {
        var removed = await ip.DeactivateAsync(command.TenantId, command.AllowlistId, ct);
        if (!removed)
            // Absent, already-inactive, or owned by another tenant → refuse without leaking existence.
            throw new KeyNotFoundException("Allow-list entry not found for this tenant.");

        await audit.RecordAsync(new AuditEntry(
            "delete", "ip_allowlist", command.AllowlistId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "IP allow-list entry deactivated", Purpose: "security"), ct);
        return true;
    }
}
