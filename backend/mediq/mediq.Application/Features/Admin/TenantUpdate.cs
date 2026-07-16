using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;

namespace mediq.Application.Features.Admin;

// ============================================================================
// Tenant edit — the platform console's "edit a clinic" path. Only mutable,
// non-structural attributes are touched: tenant_code (identity) and
// tenant_type (structure) never appear in the request DTO, so this endpoint
// can never re-key or re-type a tenant. STATUS IS NOT TOUCHED HERE — per the
// security auditor's veto, suspend/reactivate is a distinct DANGEROUS action
// with its own permission (platform.tenants.suspend) and a mandatory reason;
// it lives in TenantStatus.cs, not on this edit path.
// ============================================================================

/// <summary>Edit a tenant's mutable attributes. Gated on <c>platform.tenants.update</c> at the controller.
/// Loads via <see cref="ITenantRepository.GetByIdAsync"/> (missing → <see cref="KeyNotFoundException"/> → 404,
/// matching GetTenant), applies the update, then returns the fresh <see cref="TenantDetailDto"/> — the SAME
/// detail shape GetTenant returns, so the edit form re-syncs on save.</summary>
public sealed record UpdateTenantCommand(Guid TenantId, UpdateTenantRequest Request) : ICommand<TenantDetailDto>;

public sealed class UpdateTenantValidator : AbstractValidator<UpdateTenantCommand>
{
    public UpdateTenantValidator()
    {
        // Mirrors CreateTenantValidator for the shared editable fields (kept consistent so the create and edit
        // panels validate identically). PrimaryPhone width mirrors the create path. Status is NOT here — it is
        // owned by the suspend/reactivate path (TenantStatus.cs).
        RuleFor(x => x.Request.DisplayName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Request.LegalName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Request.PrimaryEmail).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.Request.PrimaryPhone).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Request.City).MaximumLength(100);
        RuleFor(x => x.Request.State).MaximumLength(100);
        RuleFor(x => x.Request.PinCode).Matches("^[1-9][0-9]{5}$")
            .When(x => !string.IsNullOrWhiteSpace(x.Request.PinCode))
            .WithMessage("PIN code must be 6 digits and cannot start with 0.");
    }
}

public sealed class UpdateTenantCommandHandler(
    ITenantRepository tenants, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<UpdateTenantCommand, TenantDetailDto>
{
    public async Task<TenantDetailDto> Handle(UpdateTenantCommand command, CancellationToken ct)
    {
        var req = command.Request;

        // Load first — a missing (or soft-deleted) tenant is a 404, matching GetTenant. Also carries the immutable
        // identity/structure (tenant_code, tenant_type, country) AND the current status (which this path never
        // changes) that we project back.
        var existing = await tenants.GetByIdAsync(command.TenantId, ct)
            ?? throw new KeyNotFoundException("Tenant not found.");

        var city = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim();
        var state = string.IsNullOrWhiteSpace(req.State) ? null : req.State.Trim();
        var pinCode = string.IsNullOrWhiteSpace(req.PinCode) ? null : req.PinCode.Trim();

        await tenants.UpdateAsync(
            command.TenantId, req.DisplayName.Trim(), req.LegalName.Trim(),
            req.PrimaryEmail.Trim(), req.PrimaryPhone.Trim(), city, state, pinCode, ct);

        // Audit the edit. Like the onboarding path, this is a platform-scope act (TenantId stays NULL — the audit
        // row writes on a dedicated connection and cannot see this transaction's uncommitted tenant row).
        await audit.RecordAsync(new AuditEntry(
            "update", "tenant", command.TenantId, existing.TenantCode, ctx.UserId, TenantId: null,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Updated tenant '{req.DisplayName.Trim()}' [{command.TenantId}]"), ct);

        // Project the result from the request (what we just wrote) + the immutable identity/structure fields and
        // the UNCHANGED status from the loaded row. We deliberately do NOT re-read via the repository: GetByIdAsync
        // tracks the entity, and the raw-SQL UPDATE bypasses the change tracker, so a second load would return the
        // stale identity-map instance. This is the SAME detail shape GetTenant returns, so the edit form re-syncs.
        return new TenantDetailDto(
            existing.TenantId, existing.TenantCode, req.DisplayName.Trim(), existing.TenantType,
            req.LegalName.Trim(), req.PrimaryEmail.Trim(), req.PrimaryPhone.Trim(),
            existing.Status, existing.Country, city, state, pinCode, existing.SuspendedReason);
    }
}
