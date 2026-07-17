using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;

namespace mediq.Application.Features.Admin;

// ============================================================================
// Tenant suspend / reactivate — split OUT of the ordinary edit path per the
// security auditor's veto, mirroring the established broker pattern
// (CommissionController SuspendBroker/ActivateBroker → SetBrokerStatusCommand):
// suspend and reactivate are SEPARATE routes, the transition is implied by the
// route, and both carry a reason. This is a DANGEROUS platform action — BOTH
// directions are gated on platform.tenants.suspend (is_dangerous=true), distinct
// from platform.tenants.update. A reason is MANDATORY on suspend (stored in
// tenants.suspended_reason), cleared on reactivate. The status column is written
// ONLY here — never on the edit path.
// ============================================================================

/// <summary>Suspend (<c>IsActive=false</c>) or reactivate (<c>IsActive=true</c>) a tenant. The transition is set by
/// the controller from the route. Gated on <c>platform.tenants.suspend</c>. Loads via
/// <see cref="ITenantRepository.GetByIdAsync"/> (missing → <see cref="KeyNotFoundException"/> → 404) and returns the
/// fresh <see cref="TenantDetailDto"/> so the frontend re-syncs the status chip + suspended_reason.</summary>
public sealed record SetTenantStatusCommand(Guid TenantId, bool IsActive, string? Reason) : ICommand<TenantDetailDto>;

public sealed class SetTenantStatusValidator : AbstractValidator<SetTenantStatusCommand>
{
    public SetTenantStatusValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // A reason is MANDATORY to suspend (persisted to suspended_reason + recorded in the audit trail).
        // Reactivate needs none — the handler clears the stored reason.
        RuleFor(x => x.Reason).NotEmpty().When(x => !x.IsActive)
            .WithMessage("A reason is required to suspend a tenant.");
    }
}

public sealed class SetTenantStatusCommandHandler(
    ITenantRepository tenants, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<SetTenantStatusCommand, TenantDetailDto>
{
    public async Task<TenantDetailDto> Handle(SetTenantStatusCommand command, CancellationToken ct)
    {
        // 404 on a missing/soft-deleted tenant, matching GetTenant. Carries the immutable identity/contact fields we
        // project back unchanged, plus tenant_code + prior status for the audit summary.
        var existing = await tenants.GetByIdAsync(command.TenantId, ct)
            ?? throw new KeyNotFoundException("Tenant not found.");

        var targetStatus = command.IsActive ? "active" : "suspended";
        // On suspend the reason is validated non-empty and persisted; on reactivate the stored reason is cleared.
        var reason = command.IsActive ? null : command.Reason!.Trim();

        await tenants.SetStatusAsync(command.TenantId, targetStatus, reason, ct);

        // Audit as a first-class suspend/reactivate action (NOT a generic update), so the DANGEROUS transition is
        // greppable in the tamper-evident trail with its reason. Platform-scope act → TenantId NULL, same as the
        // onboarding/edit paths (dedicated audit connection cannot see this txn's uncommitted row).
        var summary = command.IsActive
            ? $"Reactivated tenant {existing.TenantCode} [{command.TenantId}] (was {existing.Status})"
            : $"Suspended tenant {existing.TenantCode} [{command.TenantId}] (was {existing.Status}): {reason}";
        await audit.RecordAsync(new AuditEntry(
            command.IsActive ? "reactivate" : "suspend", "tenant", command.TenantId, existing.TenantCode,
            ctx.UserId, TenantId: null, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: summary), ct);

        // Full detail shape (same as GET) so the frontend re-syncs. Contact/display fields + geo are unchanged
        // (from the loaded row); status + suspended_reason reflect what we just wrote.
        var (latitude, longitude) = TenantGeo.Read(existing.Settings);
        return new TenantDetailDto(
            existing.TenantId, existing.TenantCode, existing.DisplayName, existing.TenantType,
            existing.LegalName, existing.PrimaryEmail, existing.PrimaryPhone,
            targetStatus, existing.Country, existing.City, existing.State, existing.PinCode, reason,
            latitude, longitude);
    }
}
