using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.WhatsApp;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Staff/admin files a post-hoc broker-attribution claim for a booking. Resolves the patient contact + tenant
/// display name + broker label, then hands off to <see cref="IPostHocClaimService"/> which mints the pending
/// attribution and sends the patient a confirmation OTP. Gated by the dedicated <c>commission.attribution.claim</c>
/// write permission (NOT the read permission — filing a claim creates pending commission).
/// </summary>
public sealed record PostHocClaimCommand(Guid TenantId, Guid BookingId, Guid BrokerId, string? ClaimedRelation)
    : ICommand<ClaimAttributionResult>;

public sealed class PostHocClaimValidator : AbstractValidator<PostHocClaimCommand>
{
    public PostHocClaimValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.BrokerId).NotEmpty();
    }
}

public sealed class PostHocClaimCommandHandler(
    IPostHocClaimService claimService,
    IBrokerRepository brokers,
    IAttributionRepository attributions,
    IWhatsAppCatalogReadService catalog,
    IClock clock)
    : ICommandHandler<PostHocClaimCommand, ClaimAttributionResult>
{
    public async Task<ClaimAttributionResult> Handle(PostHocClaimCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;

        // Patient contact (phone + language) — required to address the OTP; null ⇒ booking not in this tenant.
        var contact = await attributions.GetBookingPatientContactAsync(command.BookingId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Booking not found in this tenant.");

        // Broker label/phone for the message (the engine re-validates active/linked/blacklist when minting).
        var broker = await brokers.GetByIdAsync(command.BrokerId, ct);
        var tenantName = await catalog.GetTenantDisplayNameAsync(command.TenantId, ct)
            ?? (contact.PreferredLanguage == WhatsAppTemplates.Hi ? "हमारा क्लिनिक" : "our clinic");

        var attributionId = await claimService.SendForClaimAsync(new ClaimSendRequest(
            TenantId: command.TenantId,
            BookingId: command.BookingId,
            BrokerId: command.BrokerId,
            PatientPhone: contact.Phone,
            BrokerPhone: broker?.Phone,
            ClaimedRelation: command.ClaimedRelation,
            TenantName: tenantName,
            BrokerName: broker?.FullName,
            ServiceType: null,
            Lang: contact.PreferredLanguage,
            NowUtc: now), ct);

        return new ClaimAttributionResult(attributionId, "otp_sent");
    }
}
