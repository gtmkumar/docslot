using mediq.Application.Abstractions;
using mediq.Application.Common;
using mediq.Domain.Docslot;

namespace mediq.Application.Features.Docslot.WhatsApp;

/// <summary>
/// Behalf-booking patient OTP consent (DPDP fake-patient guard). Orchestrates the OTP lifecycle over the
/// abstractions only (Clean Architecture): persist a salted-hash code via <see cref="IConsentOtpStore"/>,
/// dispatch it to the patient via the outbox, and on the patient's reply confirm / deny / expire — denial or
/// lapse cancels the booking and frees its slot. Runs inside the inbound command's tenant-scoped UoW, so all
/// writes (OTP row, outbox, booking mutation) commit atomically.
/// </summary>
public sealed class PatientConsentService(
    IConsentOtpStore store,
    IOutboxMessageEnqueuer outbox,
    IWaMessageLogWriter messageLog,
    IBookingRepository bookings,
    ISlotHoldService slotHolds,
    IAttributionClaimOtpStore claims,
    IAttributionRepository attributions,
    ICommissionLifecycleService commissionLifecycle)
    : IPatientConsentService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private const int CodeLength = OtpCodec.CodeLength;

    private static readonly HashSet<string> Declines = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "n", "cancel", "decline", "stop", "nahi", "नहीं", "ना",
    };

    public async Task SendForBehalfBookingAsync(ConsentSendRequest r, CancellationToken ct)
    {
        // Supersede any prior pending code for this patient number (one live consent per number per tenant).
        await store.ExpireExistingPendingAsync(r.TenantId, r.PatientPhone, r.NowUtc, ct);

        // Cross-table single-live-OTP invariant (auditor F1): if a post-hoc attribution CLAIM OTP is live for
        // this patient, supersede it — expire the OTP and reverse its (still-unverified) attribution — so the
        // patient can only ever have ONE pending OTP. The inbound handler routes consent-first and must never
        // face two live OTPs, or a claim-code reply could be misrouted as a wrong-code consent attempt.
        var liveClaim = await claims.GetPendingByPhoneAsync(r.TenantId, r.PatientPhone, r.NowUtc, ct);
        if (liveClaim is not null)
        {
            await claims.SetStatusAsync(liveClaim.ClaimOtpId, "expired", r.NowUtc, ct);
            await attributions.MarkPatientDeniedAsync(liveClaim.AttributionId, r.TenantId, r.NowUtc, ct);
            await commissionLifecycle.OnAttributionRejectedAsync(
                r.TenantId, liveClaim.AttributionId, liveClaim.BookingId, "superseded_by_consent", r.NowUtc, ct);
        }

        var code = OtpCodec.GenerateCode();
        var salt = OtpCodec.NewSalt();
        var hash = OtpCodec.Hash(salt, code);
        var expiresAt = r.NowUtc.Add(Ttl);

        await store.CreateAsync(new ConsentOtpInsert(
            r.TenantId, r.BookingId, r.PatientPhone, r.BookerPhone, r.Relation, salt, hash, expiresAt, r.NowUtc), ct);

        var bookerLabel = string.IsNullOrWhiteSpace(r.BookerName) ? r.BookerPhone : r.BookerName!;
        var text = WhatsAppTemplates.ConsentRequest(
            r.Lang, r.TenantName, bookerLabel, r.Relation, r.DoctorName ?? "—", r.SlotLabel ?? "—", code);

        // Send the OTP to the PATIENT (a different number than the booker conversation) via the outbox. The
        // outbox payload must carry the real text to deliver (the drain scrubs it after send); the message
        // JOURNAL must NOT — log a redacted marker so the live code never persists in wa_message_log.
        await outbox.EnqueueAsync(new OutboxMessage(
            r.TenantId, r.PatientId, "consent_otp", r.PatientPhone, text, CorrelationId: null, r.NowUtc), ct);
        await messageLog.LogAsync(new WaMessageLogEntry(
            r.TenantId, r.PatientId, ConversationId: null, WhatsAppMessageId: null,
            "outbound", "text", "[patient consent code sent]", Status: "queued", r.NowUtc), ct);
    }

    public async Task<ConsentVerifyResult?> TryVerifyReplyAsync(
        Guid tenantId, string fromPhone, string body, string lang, DateTime nowUtc, CancellationToken ct)
    {
        var otp = await store.GetPendingByPhoneAsync(tenantId, fromPhone, nowUtc, ct);
        if (otp is null)
            return null;   // not a consent reply → caller runs the normal booking state machine

        // Late reply to a lapsed code: expire it, cancel the awaiting booking, free the slot.
        if (otp.ExpiresAt <= nowUtc)
        {
            await store.SetStatusAsync(otp.ConsentOtpId, PatientConsentStatus.Expired, nowUtc, ct);
            await CancelAwaitingBookingAsync(otp.BookingId, tenantId, ConsentOutcome.Expired, nowUtc, ct);
            return new ConsentVerifyResult(ConsentOutcome.Expired, WhatsAppTemplates.ConsentExpired(lang), otp.BookingId, null);
        }

        var reply = body.Trim();

        // Explicit decline.
        if (Declines.Contains(reply))
        {
            await store.SetStatusAsync(otp.ConsentOtpId, PatientConsentStatus.Denied, nowUtc, ct);
            await CancelAwaitingBookingAsync(otp.BookingId, tenantId, ConsentOutcome.Denied, nowUtc, ct);
            return new ConsentVerifyResult(ConsentOutcome.Denied, WhatsAppTemplates.ConsentDenied(lang), otp.BookingId, null);
        }

        // Correct code → confirm consent.
        var submitted = new string(reply.Where(char.IsDigit).ToArray());
        if (submitted.Length == CodeLength && OtpCodec.Matches(otp.CodeSalt, otp.CodeHash, submitted))
        {
            await store.SetStatusAsync(otp.ConsentOtpId, PatientConsentStatus.Confirmed, nowUtc, ct);
            var booking = await bookings.GetByIdAsync(otp.BookingId, tenantId, ct);
            if (booking is { AwaitingPatientConsent: true })
                booking.ConfirmPatientConsent(nowUtc);
            return new ConsentVerifyResult(ConsentOutcome.Confirmed, WhatsAppTemplates.ConsentConfirmed(lang), otp.BookingId, booking?.PatientId);
        }

        // Wrong code → count the attempt; deny once attempts are exhausted.
        await store.IncrementAttemptsAsync(otp.ConsentOtpId, ct);
        var used = otp.Attempts + 1;
        if (used >= otp.MaxAttempts)
        {
            await store.SetStatusAsync(otp.ConsentOtpId, "failed", nowUtc, ct);
            await CancelAwaitingBookingAsync(otp.BookingId, tenantId, ConsentOutcome.Denied, nowUtc, ct);
            return new ConsentVerifyResult(ConsentOutcome.Denied, WhatsAppTemplates.ConsentDenied(lang), otp.BookingId, null);
        }

        return new ConsentVerifyResult(
            ConsentOutcome.WrongCode, WhatsAppTemplates.ConsentWrongCode(lang, otp.MaxAttempts - used), otp.BookingId, null);
    }

    private async Task CancelAwaitingBookingAsync(
        Guid bookingId, Guid tenantId, ConsentOutcome outcome, DateTime nowUtc, CancellationToken ct)
    {
        var booking = await bookings.GetByIdAsync(bookingId, tenantId, ct);
        if (booking is null || !booking.AwaitingPatientConsent)
            return;

        if (outcome == ConsentOutcome.Expired) booking.ExpirePatientConsent(nowUtc);
        else booking.DenyPatientConsent(nowUtc);

        // Only cancel a still-cancellable booking, then return the capacity it consumed at creation.
        if (booking.Status is BookingStatus.Pending or BookingStatus.Confirmed)
        {
            booking.Cancel(
                outcome == ConsentOutcome.Expired ? "Patient consent OTP expired" : "Patient declined consent",
                byUserId: null, nowUtc);
            await slotHolds.ReleaseSlotCapacityAsync(booking.SlotId, nowUtc, ct);
            // A broker-portal booking carries an auto-verified attribution; cancelling it (consent denied/lapsed)
            // must claw that back too, exactly as an explicit cancel/no-show does. No-op for an ordinary behalf
            // booking with no attribution. (The SQL consent-expiry sweep does the equivalent for the worker path.)
            await commissionLifecycle.OnBookingReversedAsync(tenantId, bookingId, nowUtc, ct);
        }
    }
}
