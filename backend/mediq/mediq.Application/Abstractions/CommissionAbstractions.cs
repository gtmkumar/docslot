using mediq.Domain.Commission;
using mediq.SharedDataModel.Docslot.Commission;

namespace mediq.Application.Abstractions;

/// <summary>Broker identity (platform-level by phone) + tenant linkage + KYC. PAN stored as ciphertext.</summary>
public interface IBrokerRepository
{
    Task<Broker?> GetByPhoneAsync(string phone, CancellationToken ct);
    Task<Broker?> GetByIdAsync(Guid brokerId, CancellationToken ct);
    Task<Guid> CreateAsync(Broker broker, CancellationToken ct);
    Task LinkToTenantAsync(Guid brokerId, Guid tenantId, DateTime nowUtc, CancellationToken ct);
    Task SetActiveAsync(Guid brokerId, Guid tenantId, bool isActive, Guid? byUserId, DateTime nowUtc, CancellationToken ct);
    Task BlacklistAsync(Guid brokerId, string reason, DateTime nowUtc, CancellationToken ct);
    Task<bool> IsLinkedToTenantAsync(Guid brokerId, Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<BrokerDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct);
    Task<bool> GstRegisteredAsync(Guid brokerId, CancellationToken ct);
    Task<bool> HasPriorAttributionAsync(Guid brokerId, CancellationToken ct);   // for first_booking_only rules
}

/// <summary>Commission rules: highest-priority matching active rule for an attribution context.</summary>
public interface ICommissionRuleRepository
{
    Task<Guid> CreateAsync(Guid tenantId, CreateCommissionRuleRequest request, DateTime nowUtc, CancellationToken ct);
    Task ApproveAsync(Guid ruleId, Guid byUserId, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<CommissionRuleDto>> ListAsync(Guid tenantId, CancellationToken ct);
    /// <summary>Active rules for a tenant, ordered by priority DESC (engine picks the first that matches).</summary>
    Task<IReadOnlyList<CommissionRule>> GetActiveRulesAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// The attribution ledger. <see cref="AddAsync"/> inserts the row; the DB trigger
/// <c>trg_no_attribution_on_discounted</c> REJECTS it (SQLSTATE 23514) when the booking carries a
/// direct-booking discount — the repo translates that to <see cref="Domain.Commission.AttributionOnDiscountedBookingException"/>.
/// </summary>
public interface IAttributionRepository
{
    Task AddAsync(Attribution attribution, CancellationToken ct);
    Task<bool> ExistsAsync(Guid bookingId, Guid brokerId, CancellationToken ct);
    Task<BookingValue?> GetBookingValueAsync(Guid bookingId, Guid tenantId, CancellationToken ct);
    Task<int> CountRecentByBrokerAsync(Guid brokerId, TimeSpan window, CancellationToken ct);       // rapid-burst fraud
    Task<bool> BookingPatientReferredBySelfAsync(Guid bookingId, Guid brokerId, CancellationToken ct); // self-referral fraud
    Task<decimal> BrokerEarnedThisMonthAsync(Guid brokerId, Guid tenantId, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ReadyToPayAttributionIdsAsync(Guid tenantId, Guid brokerId, CancellationToken ct);
    Task<decimal> ReadyToPayGrossAsync(Guid tenantId, Guid brokerId, CancellationToken ct);
    Task MarkPaidAsync(IReadOnlyList<Guid> attributionIds, Guid payoutId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Booking completed → advance that booking's verification-cleared 'pending' attributions to 'earned'
    /// (sets earned_at). Returns the (broker, amount) earned so the caller can credit wallets + emit events.
    /// </summary>
    Task<IReadOnlyList<EarnedAttribution>> MarkEarnedForBookingAsync(Guid tenantId, Guid bookingId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Booking cancelled/no-show → reverse that booking's not-yet-paid attributions (pending/earned/
    /// ready_to_pay → 'reversed'). Returns (broker, amount, fromStatus) so the caller can debit the right
    /// wallet bucket + lifetime_reversed.
    /// </summary>
    Task<IReadOnlyList<ReversedAttribution>> MarkReversedForBookingAsync(Guid tenantId, Guid bookingId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Reverse a SINGLE attribution (dispute clawback). Returns (broker, amount, fromStatus) or null if not reversible.</summary>
    Task<ReversedAttribution?> ReverseOneAsync(Guid attributionId, Guid tenantId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Settlement-window job: flip 'earned' attributions older than <paramref name="window"/> to 'ready_to_pay'
    /// and move the broker wallet earned→ready_to_pay. Cross-tenant via a SECURITY DEFINER fn. Returns the count.
    /// </summary>
    Task<int> SettleEarnedAsync(TimeSpan window, CancellationToken ct);

    /// <summary>Writes the computed direct-patient discount onto the booking (direct_discount_inr + rule).</summary>
    Task WriteDirectDiscountAsync(Guid bookingId, Guid tenantId, decimal discountInr, Guid ruleId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Attribution ledger list (most recent first), tenant-scoped + offset-paginated. Returns display-safe
    /// rows: the patient is a FIRST NAME + MASKED phone (PHI), and the booking is the human BKG- ref.
    /// Returns the SharedDataModel list DTO directly (masking applied at the infra seam).
    /// </summary>
    Task<IReadOnlyList<SharedDataModel.Docslot.Commission.AttributionListItemDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct);

    /// <summary>
    /// Patient confirmed a post-hoc claim (OTP) → flip the attribution's verification 'pending' → 'patient_confirmed'
    /// (+ verified_at). Returns true if it flipped (idempotent: a non-pending attribution returns false). Earning
    /// is then handled by <see cref="MarkEarnedForBookingAsync"/> once/if the booking is completed.
    /// </summary>
    Task<bool> MarkPatientConfirmedAsync(Guid attributionId, Guid tenantId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Patient declined a post-hoc claim → flip verification 'pending' → 'patient_denied' (+ verified_at). Reversal is separate (<see cref="ReverseOneAsync"/>).</summary>
    Task MarkPatientDeniedAsync(Guid attributionId, Guid tenantId, DateTime nowUtc, CancellationToken ct);

    /// <summary>True if the booking is in a completed state (drives earn-on-confirm for post-hoc claims filed after the visit).</summary>
    Task<bool> IsBookingCompletedAsync(Guid bookingId, Guid tenantId, CancellationToken ct);

    /// <summary>Patient phone + preferred language for a booking, to address + render a claim OTP. Null if the booking is not in this tenant.</summary>
    Task<BookingPatientContact?> GetBookingPatientContactAsync(Guid bookingId, Guid tenantId, CancellationToken ct);
}

/// <summary>Minimal patient contact for OTP delivery (phone is PHI; resolved only for the referral-claim purpose).</summary>
public sealed record BookingPatientContact(string Phone, string PreferredLanguage);

public sealed record BookingValue(decimal AmountInr, decimal DirectDiscountInr, string? ServiceType, Guid PatientId);

/// <summary>An attribution that just earned (booking completed): broker + commission amount.</summary>
public sealed record EarnedAttribution(Guid BrokerId, decimal Amount);

/// <summary>A reversed attribution: broker + amount + the status it held before reversal (drives the wallet bucket).</summary>
public sealed record ReversedAttribution(Guid BrokerId, decimal Amount, string FromStatus);

/// <summary>Payout batch persistence + approve/execute (TWO distinct steps, gated by distinct permissions).</summary>
public interface IPayoutRepository
{
    Task<Guid> CreateAsync(Payout payout, CancellationToken ct);
    Task<Payout?> GetByIdAsync(Guid payoutId, Guid tenantId, CancellationToken ct);
    Task ApproveAsync(Guid payoutId, Guid byUserId, DateTime nowUtc, CancellationToken ct);
    /// <summary>
    /// Atomically claims an APPROVED payout for execution (approved → processing) — the concurrency gate so
    /// two concurrent executes can't both disburse / double-credit the wallet. Returns true for the single
    /// winner; false if the payout was not 'approved' (already executing/paid). Caller must early-return on false.
    /// </summary>
    Task<bool> TryClaimForExecutionAsync(Guid payoutId, Guid tenantId, DateTime nowUtc, CancellationToken ct);
    Task MarkPaidAsync(Guid payoutId, string paymentReference, string paymentGateway, DateTime nowUtc, CancellationToken ct);
    Task MarkFailedAsync(Guid payoutId, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<PayoutDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct);
}

/// <summary>Materialized broker wallet (self-service portal balance).</summary>
public interface IBrokerWalletRepository
{
    Task EnsureExistsAsync(Guid brokerId, CancellationToken ct);
    /// <summary>Attribution created (commission_status 'pending'): credit pending_inr + lifetime/month counters.</summary>
    Task ApplyAttributedAsync(Guid brokerId, decimal amountInr, DateTime nowUtc, CancellationToken ct);
    /// <summary>Booking completed (pending → earned): move pending_inr → earned_inr.</summary>
    Task ApplyEarnedAsync(Guid brokerId, decimal amountInr, DateTime nowUtc, CancellationToken ct);
    /// <summary>Payout executed (ready_to_pay → paid): debit ready_to_pay_inr, credit lifetime_paid_inr.</summary>
    Task ApplyPaidAsync(Guid brokerId, decimal grossInr, DateTime nowUtc, CancellationToken ct);
    /// <summary>Reversal/clawback: debit the bucket matching <paramref name="fromStatus"/> + credit lifetime_reversed_inr.</summary>
    Task ApplyReversedAsync(Guid brokerId, decimal amountInr, string fromStatus, DateTime nowUtc, CancellationToken ct);
    Task<BrokerWalletDto?> GetAsync(Guid brokerId, CancellationToken ct);
}

/// <summary>Disputes + campaigns (admin surface).</summary>
public interface ICommissionAdminRepository
{
    Task<Guid> RaiseDisputeAsync(Guid tenantId, RaiseDisputeRequest request, Guid? byUserId, DateTime nowUtc, CancellationToken ct);
    /// <summary>Resolves the dispute and returns the disputed attribution_id (so the handler can claw back if the tenant won).</summary>
    Task<Guid> ResolveDisputeAsync(Guid disputeId, ResolveDisputeRequest request, Guid byUserId, DateTime nowUtc, CancellationToken ct);
    Task<Guid> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, Guid? byUserId, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<DisputeDto>> ListDisputesAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Referral link generation (BRK-codes) + click logging (hashed IP).</summary>
public interface IReferralLinkRepository
{
    Task<ReferralLinkDto> CreateAsync(Guid brokerId, CreateReferralLinkRequest request, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<ReferralLinkDto>> ListByBrokerAsync(Guid brokerId, CancellationToken ct);

    /// <summary>Resolve an ACTIVE, non-expired referral link by its public short code (for the anonymous click endpoint + the WA code-detector). Null if missing/inactive/expired.</summary>
    Task<ResolvedReferralLink?> ResolveActiveByShortCodeAsync(string shortCode, DateTime nowUtc, CancellationToken ct);

    /// <summary>Log a click (privacy: IP is pre-hashed by the caller; no raw IP, no PHI) + bump click_count. Returns the click id.</summary>
    Task<Guid> LogClickAsync(Guid linkId, string shortCode, string? sessionToken, string? ipHash, string? userAgentBrief, string referrerSource, DateTime nowUtc, CancellationToken ct);

    /// <summary>Mark the link's most-recent unconverted click as converted to this booking + bump conversion_count (best-effort click↔booking join for the WhatsApp channel).</summary>
    Task MarkConvertedAsync(Guid linkId, Guid bookingId, DateTime nowUtc, CancellationToken ct);
}

/// <summary>An active referral link resolved from its public short code: who earns (broker), where it points (tenant + target url).</summary>
public sealed record ResolvedReferralLink(Guid LinkId, Guid BrokerId, Guid? TenantId, string? TargetUrl);

/// <summary>Pure fraud scoring for an attribution (repeat_phone / rapid_burst / self_referral). Score >0.5 → flag.</summary>
public interface IFraudScorer
{
    Task<(decimal Score, string[] Flags)> ScoreAsync(Guid bookingId, Guid brokerId, CancellationToken ct);
}

/// <summary>
/// Computes + writes the direct-booking discount (the flywheel incentive): a broker-LESS booking gets
/// <c>direct_discount_pct</c> of the would-be commission as a patient discount, funded from the commission
/// pool. Mutually exclusive with a broker attribution (the DB trigger <c>trg_no_attribution_on_discounted</c>
/// blocks attribution once a discount is written). Runs in the booking-creation UoW.
/// </summary>
public interface IDirectDiscountService
{
    /// <summary>Applies the discount for a direct booking; returns the amount written (0 if no matching rule).</summary>
    Task<decimal> ApplyAsync(Guid tenantId, Guid bookingId, DateTime nowUtc, CancellationToken ct);
}

/// <summary>
/// The payout disbursement rail. Selected by config like the WhatsApp sender: when real gateway credentials
/// are present the live adapter (RazorpayX/Cashfree) is wired; otherwise the dev <c>StubPayoutGateway</c> runs
/// a HONEST DRY RUN — it returns a clearly-labelled <c>DRYRUN-…</c> reference and gateway name <c>stub_dryrun</c>
/// so a payout is never silently reported "paid" with a fabricated UTR. The execute handler records whatever
/// the gateway returns (success → 'paid' + reference + gateway; failure → 'failed' + error).
/// </summary>
public interface IPayoutGateway
{
    /// <summary>The configured gateway name (e.g. 'stub_dryrun', 'razorpayx'), surfaced for audit/transparency.</summary>
    string Name { get; }
    Task<PayoutGatewayResult> SendAsync(PayoutInstruction instruction, CancellationToken ct);
}

public sealed record PayoutInstruction(Guid PayoutId, Guid BrokerId, decimal NetAmountInr, string PaymentMethod, string? UpiId);

public sealed record PayoutGatewayResult(bool Success, string Reference, string GatewayName, bool IsDryRun, string? Error)
{
    public static PayoutGatewayResult Ok(string reference, string gateway, bool isDryRun) => new(true, reference, gateway, isDryRun, null);
    public static PayoutGatewayResult Failed(string error, string gateway) => new(false, "", gateway, false, error);
}

/// <summary>
/// Drives the commission EARNING lifecycle off booking lifecycle events (the missing link that left every
/// attribution stuck at 'pending'). Invoked from the booking action handler inside its tenant-scoped UoW:
/// a completed booking earns its attributions (+ credits wallets + emits <c>commission.commission.earned</c>);
/// a cancelled/no-show booking reverses them (+ debits wallets + emits <c>commission.commission.reversed</c>).
/// </summary>
public interface ICommissionLifecycleService
{
    Task OnBookingCompletedAsync(Guid tenantId, Guid bookingId, DateTime nowUtc, CancellationToken ct);
    Task OnBookingReversedAsync(Guid tenantId, Guid bookingId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// A post-hoc claim's patient just CONFIRMED (verification → patient_confirmed). Because such claims are
    /// usually filed AFTER the visit, the booking-completed event already fired before the attribution existed —
    /// so if the booking is already completed, earn the attribution now (+ wallet + event); otherwise it stays
    /// 'pending' and the eventual completion earns it.
    /// </summary>
    Task OnAttributionConfirmedAsync(Guid tenantId, Guid bookingId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// A post-hoc claim was REJECTED (patient denied, or no response on lapse) → reverse that single attribution
    /// (commission_status → reversed) + debit the broker wallet from its current bucket (+ lifetime_reversed) +
    /// emit <c>commission.commission.reversed</c>. Closes the phantom-pending_inr gap for the in-request deny path
    /// (the no-response lapse is handled by the SECURITY DEFINER sweep <c>commission.expire_stale_attribution_claims</c>).
    /// </summary>
    Task OnAttributionRejectedAsync(Guid tenantId, Guid attributionId, Guid bookingId, string reason, DateTime nowUtc, CancellationToken ct);
}

// ============================================================================
// Post-hoc broker-attribution claim (patient OTP confirm/deny)
// ============================================================================

/// <summary>
/// Orchestrates the post-hoc attribution-claim OTP flow. A broker claims a (usually completed) booking and we
/// mint a 'post_hoc_claim' attribution in verification 'pending', then send a one-time code to the PATIENT's
/// WhatsApp number. The patient's reply confirms (→ patient_confirmed → earns) or declines (→ patient_denied →
/// reversed). Mirrors <see cref="IPatientConsentService"/> but over <c>commission.attribution_claim_otps</c>.
/// Runs inside the request/inbound tenant-scoped UoW so the attribution, OTP row, outbox and wallet all commit
/// together.
/// </summary>
public interface IPostHocClaimService
{
    /// <summary>
    /// Mints the pending attribution (via the attribution engine; the discount-exclusivity trigger rejects a
    /// discounted booking → 422) and sends a claim OTP to the patient. Throws if the patient already has a live
    /// consent OTP (so the patient's YES/NO can never resolve the wrong action). Returns the new attribution id.
    /// </summary>
    Task<Guid> SendForClaimAsync(ClaimSendRequest request, CancellationToken ct);

    /// <summary>
    /// If <paramref name="fromPhone"/> has a pending CLAIM OTP in this tenant, interpret <paramref name="body"/>
    /// as the code (or a decline) and resolve it — confirming the attribution (→ patient_confirmed, earns if the
    /// booking is completed) or denying it (→ patient_denied, reversed). Returns null when there is NO pending
    /// claim for this number (the caller then runs the next handler).
    /// </summary>
    Task<ClaimVerifyResult?> TryVerifyReplyAsync(Guid tenantId, string fromPhone, string body, string lang, DateTime nowUtc, CancellationToken ct);
}

public sealed record ClaimSendRequest(
    Guid TenantId, Guid BookingId, Guid BrokerId, string PatientPhone, string? BrokerPhone,
    string? ClaimedRelation, string TenantName, string? BrokerName, string? ServiceType, string Lang, DateTime NowUtc);

public enum ClaimOutcome { Confirmed, Denied, WrongCode, Expired }

public sealed record ClaimVerifyResult(ClaimOutcome Outcome, string OutboundText, Guid AttributionId, Guid BookingId);

/// <summary>Persistence over <c>commission.attribution_claim_otps</c> (tenant-isolated by RLS). Mirrors <see cref="IConsentOtpStore"/>.</summary>
public interface IAttributionClaimOtpStore
{
    /// <summary>Expire any existing 'pending' claim for (tenant, patientPhone) so a newer claim supersedes it.</summary>
    Task ExpireExistingPendingAsync(Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct);

    Task CreateAsync(ClaimOtpInsert row, CancellationToken ct);

    /// <summary>The single live (pending) claim for this number in this tenant, or null.</summary>
    Task<PendingClaimOtp?> GetPendingByPhoneAsync(Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct);

    Task SetStatusAsync(Guid claimOtpId, string status, DateTime? verifiedAtUtc, CancellationToken ct);

    Task IncrementAttemptsAsync(Guid claimOtpId, CancellationToken ct);

    /// <summary>
    /// Sweeps lapsed pending claim OTPs: marks them 'expired', sets the attribution to no_response, reverses it
    /// and debits the broker wallet. Backed by the SECURITY DEFINER fn <c>commission.expire_stale_attribution_claims</c>
    /// (cross-tenant). Returns the number of claims lapsed.
    /// </summary>
    Task<int> ExpireStaleAsync(CancellationToken ct);
}

public sealed record ClaimOtpInsert(
    Guid TenantId, Guid AttributionId, Guid BookingId, Guid BrokerId, string PatientPhone, string? BrokerPhone,
    string? ClaimedRelation, string CodeSalt, string CodeHash, DateTime ExpiresAt, DateTime NowUtc);

public sealed record PendingClaimOtp(
    Guid ClaimOtpId, Guid AttributionId, Guid BookingId, Guid BrokerId, string PatientPhone,
    string CodeSalt, string CodeHash, short Attempts, short MaxAttempts, DateTime ExpiresAt);
