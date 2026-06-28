using mediq.Application.Abstractions;
using mediq.Application.Common;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.WhatsApp;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Post-hoc broker-attribution claim OTP flow. A broker claims a (usually completed) booking; we mint a
/// 'post_hoc_claim' attribution (verification 'pending') via the attribution engine and send a one-time code
/// to the PATIENT's WhatsApp number. The patient's reply confirms (→ patient_confirmed → earns if the booking
/// is completed) or declines (→ patient_denied → reversed). Mirrors <see cref="PatientConsentService"/> but
/// over <c>commission.attribution_claim_otps</c> and the commission lifecycle. Runs inside the caller's
/// tenant-scoped UoW (the nested CreateAttributionCommand reuses the ambient transaction), so the attribution,
/// OTP row, outbox, and wallet credit all commit together.
/// </summary>
public sealed class PostHocClaimService(
    IAttributionClaimOtpStore claims,
    IConsentOtpStore consent,
    IOutboxMessageEnqueuer outbox,
    IWaMessageLogWriter messageLog,
    ICommandDispatcher commands,
    IAttributionRepository attributions,
    ICommissionLifecycleService lifecycle)
    : IPostHocClaimService
{
    // 'no_response' after 24h (the attribution verification taxonomy). The sweep lapses unanswered claims.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const int CodeLength = OtpCodec.CodeLength;

    private static readonly HashSet<string> Declines = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "n", "cancel", "decline", "stop", "nahi", "नहीं", "ना",
    };

    public async Task<Guid> SendForClaimAsync(ClaimSendRequest r, CancellationToken ct)
    {
        // Cross-table single-live-OTP guard: never overlay a claim OTP while the patient has a pending behalf-
        // consent OTP, so their YES/NO reply can never resolve the wrong action (consent is checked first inbound).
        if (await consent.GetPendingByPhoneAsync(r.TenantId, r.PatientPhone, r.NowUtc, ct) is not null)
            throw new BusinessRuleException("The patient has a pending consent request; retry the attribution claim shortly.");

        // Supersede any prior pending claim for this patient (one live claim per number per tenant).
        await claims.ExpireExistingPendingAsync(r.TenantId, r.PatientPhone, r.NowUtc, ct);

        // Mint the pending attribution via the engine (nested dispatch reuses the ambient tx → atomic). The
        // discount↔attribution trigger rejects a discounted booking here (→ 422) BEFORE any OTP is sent; broker
        // active/linked/blacklist + self-referral fraud scoring are all enforced inside the command.
        var result = await commands.Send(new CreateAttributionCommand(
            r.TenantId, new CreateAttributionRequest(r.BookingId, r.BrokerId, "post_hoc_claim", null, r.ServiceType)), ct);

        // Generate + persist the salted-hash code; the plaintext goes to the PATIENT via the outbox only.
        var code = OtpCodec.GenerateCode();
        var salt = OtpCodec.NewSalt();
        var hash = OtpCodec.Hash(salt, code);
        var expiresAt = r.NowUtc.Add(Ttl);

        await claims.CreateAsync(new ClaimOtpInsert(
            r.TenantId, result.AttributionId, r.BookingId, r.BrokerId, r.PatientPhone, r.BrokerPhone,
            r.ClaimedRelation, salt, hash, expiresAt, r.NowUtc), ct);

        var brokerLabel = string.IsNullOrWhiteSpace(r.BrokerName)
            ? (WhatsAppTemplates.IsHi(r.Lang) ? "एक रेफ़रल पार्टनर" : "a referral partner")
            : r.BrokerName!;
        var text = WhatsAppTemplates.ClaimRequest(r.Lang, r.TenantName, brokerLabel, code);

        // The outbox payload carries the real code to deliver (the drain scrubs it post-send for 'claim_otp');
        // the message JOURNAL must NOT — log a redacted marker so the live code never persists in wa_message_log.
        await outbox.EnqueueAsync(new OutboxMessage(
            r.TenantId, PatientId: null, "claim_otp", r.PatientPhone, text, CorrelationId: null, r.NowUtc), ct);
        await messageLog.LogAsync(new WaMessageLogEntry(
            r.TenantId, PatientId: null, ConversationId: null, WhatsAppMessageId: null,
            "outbound", "text", "[patient referral confirmation code sent]", Status: "queued", r.NowUtc), ct);

        return result.AttributionId;
    }

    public async Task<ClaimVerifyResult?> TryVerifyReplyAsync(
        Guid tenantId, string fromPhone, string body, string lang, DateTime nowUtc, CancellationToken ct)
    {
        var otp = await claims.GetPendingByPhoneAsync(tenantId, fromPhone, nowUtc, ct);
        if (otp is null)
            return null;   // not a claim reply → caller runs the next handler

        // Late reply to a lapsed code (the cadence sweep hasn't run yet): expire + reverse (no_response).
        if (otp.ExpiresAt <= nowUtc)
        {
            await claims.SetStatusAsync(otp.ClaimOtpId, "expired", nowUtc, ct);
            await attributions.MarkPatientDeniedAsync(otp.AttributionId, tenantId, nowUtc, ct);
            await lifecycle.OnAttributionRejectedAsync(tenantId, otp.AttributionId, otp.BookingId, "no_response", nowUtc, ct);
            return new ClaimVerifyResult(ClaimOutcome.Expired, WhatsAppTemplates.ClaimExpired(lang), otp.AttributionId, otp.BookingId);
        }

        var reply = body.Trim();

        // Explicit decline.
        if (Declines.Contains(reply))
        {
            await claims.SetStatusAsync(otp.ClaimOtpId, "denied", nowUtc, ct);
            await attributions.MarkPatientDeniedAsync(otp.AttributionId, tenantId, nowUtc, ct);
            await lifecycle.OnAttributionRejectedAsync(tenantId, otp.AttributionId, otp.BookingId, "patient_denied", nowUtc, ct);
            return new ClaimVerifyResult(ClaimOutcome.Denied, WhatsAppTemplates.ClaimDenied(lang), otp.AttributionId, otp.BookingId);
        }

        // Correct code → confirm the referral; earn now if the booking is already completed.
        var submitted = new string(reply.Where(char.IsDigit).ToArray());
        if (submitted.Length == CodeLength && OtpCodec.Matches(otp.CodeSalt, otp.CodeHash, submitted))
        {
            await claims.SetStatusAsync(otp.ClaimOtpId, "confirmed", nowUtc, ct);
            if (await attributions.MarkPatientConfirmedAsync(otp.AttributionId, tenantId, nowUtc, ct))
                await lifecycle.OnAttributionConfirmedAsync(tenantId, otp.BookingId, nowUtc, ct);
            return new ClaimVerifyResult(ClaimOutcome.Confirmed, WhatsAppTemplates.ClaimConfirmed(lang), otp.AttributionId, otp.BookingId);
        }

        // Wrong code → count the attempt; on exhaustion treat as a denial (reverse, don't leave money pending).
        await claims.IncrementAttemptsAsync(otp.ClaimOtpId, ct);
        var used = otp.Attempts + 1;
        if (used >= otp.MaxAttempts)
        {
            await claims.SetStatusAsync(otp.ClaimOtpId, "failed", nowUtc, ct);
            await attributions.MarkPatientDeniedAsync(otp.AttributionId, tenantId, nowUtc, ct);
            await lifecycle.OnAttributionRejectedAsync(tenantId, otp.AttributionId, otp.BookingId, "failed_attempts", nowUtc, ct);
            return new ClaimVerifyResult(ClaimOutcome.Denied, WhatsAppTemplates.ClaimDenied(lang), otp.AttributionId, otp.BookingId);
        }

        return new ClaimVerifyResult(
            ClaimOutcome.WrongCode, WhatsAppTemplates.ClaimWrongCode(lang, otp.MaxAttempts - used), otp.AttributionId, otp.BookingId);
    }
}
