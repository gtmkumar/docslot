namespace mediq.SharedDataModel.Docslot.Commission;

/// <summary>
/// Commission/broker DTOs (slice 07). Customer-facing term for a broker is "Care Partner" (per MCI 6.4
/// positioning). PAN and full bank account are NEVER serialized (encrypted at rest; only last-4 display).
/// Brokers see patients as first-name + masked-phone only (DPDP).
/// </summary>

// ---- Broker / KYC --------------------------------------------------------------------------------

public sealed record RegisterBrokerRequest(
    string Phone, string FullName, string? Email, string BrokerType, string? Pan, string? GstNumber, string OnboardedVia = "tenant_invite");

public sealed record RegisterBrokerResult(Guid BrokerId, bool AlreadyExisted);

/// <summary>Broker (Care Partner) profile. Display-safe fields ONLY — NO PAN, NO full bank account, and the
/// phone is MASKED (DPDP): the raw number is never serialised in a list. Mirrors FE BrokerSchema.maskedPhone.</summary>
public sealed record BrokerDto(
    Guid BrokerId,
    string MaskedPhone,
    string FullName,
    string? Email,
    string BrokerType,
    string TierLevel,
    bool PanVerified,
    bool GstVerified,
    bool IsActive,
    bool IsBlacklisted,
    string CarePartnerLabel);   // "Care Partner" — the customer-facing term

public sealed record SetBrokerStatusRequest(bool IsActive, string? Reason);
/// <summary>
/// Body for the split <c>/suspend</c> and <c>/activate</c> broker endpoints. The transition (active flag) is
/// implied by the route — each route is gated by its OWN permission (<c>commission.broker.suspend</c> vs
/// <c>commission.broker.activate</c>, SoD) — so only an optional reason travels in the body.
/// </summary>
public sealed record SetBrokerStatusReasonRequest(string? Reason);
public sealed record BlacklistBrokerRequest(string Reason);

// ---- Referral links ------------------------------------------------------------------------------

public sealed record CreateReferralLinkRequest(Guid? TenantId, Guid? TargetDoctorId, string? CampaignName);
public sealed record ReferralLinkDto(Guid LinkId, string ShortCode, string? TargetUrl, int ClickCount, int ConversionCount, bool IsActive);

// ---- Commission rules ----------------------------------------------------------------------------

public sealed record CreateCommissionRuleRequest(
    string RuleName, string RuleKey, string CalcType, decimal? FlatAmountInr, decimal? Percentage,
    decimal? MinCommissionInr, decimal? MaxCommissionInr, decimal? MaxMonthlyPerBrokerInr,
    IReadOnlyList<string>? AppliesToBrokerTier, IReadOnlyList<string>? AppliesToServiceType, int Priority, bool FirstBookingOnly);

public sealed record CommissionRuleDto(
    Guid RuleId, string RuleName, string RuleKey, string CalcType, decimal? FlatAmountInr, decimal? Percentage,
    int Priority, bool IsActive, bool ExcludesPndt);

// ---- Attribution ---------------------------------------------------------------------------------

/// <summary>Create an attribution for a (booking, broker). The engine matches a rule, calculates commission, scores fraud.</summary>
public sealed record CreateAttributionRequest(
    Guid BookingId, Guid BrokerId, string AttributionSource, Guid? ReferralLinkId, string? ServiceType);

public sealed record AttributionResultDto(
    Guid AttributionId, Guid BookingId, Guid BrokerId, string AttributionSource, string VerificationStatus,
    string CommissionStatus, decimal? CommissionAmountInr, decimal FraudScore, IReadOnlyList<string> FraudFlags);

/// <summary>Body for POST /commission/bookings/{bookingId}/claim-attribution — the broker filing a post-hoc claim.</summary>
public sealed record ClaimAttributionRequest(Guid BrokerId, string? ClaimedRelation);

/// <summary>Result of filing a post-hoc claim: the pending attribution id + status ('otp_sent').</summary>
public sealed record ClaimAttributionResult(Guid AttributionId, string Status);

/// <summary>Body for POST /commission/me/bookings — a broker booking on behalf of a referred patient (broker self-service).</summary>
public sealed record CreateBrokerBookingRequest(
    string PatientPhone, string? PatientName, short? PatientAge, string? PatientGender,
    Guid SlotId, Guid DoctorId, Guid? DepartmentId, string? ChiefComplaint);

/// <summary>Result of a broker-portal booking: the booking + its auto-verified attribution + status ('awaiting_patient_consent').</summary>
public sealed record BrokerBookingResult(Guid BookingId, string? BookingNumber, Guid AttributionId, string Status);

// ---- Payouts -------------------------------------------------------------------------------------

public sealed record CreatePayoutBatchRequest(Guid BrokerId, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>Payout with full tax breakdown (gross → TDS → GST → net). No PAN; only the math + broker name.</summary>
public sealed record PayoutDto(
    Guid PayoutId, Guid BrokerId, string BrokerName, DateOnly PeriodStart, DateOnly PeriodEnd, int AttributionCount,
    decimal GrossAmountInr, decimal TdsRate, decimal TdsAmountInr, decimal? GstRate, decimal GstAmountInr,
    decimal NetAmountInr, string Status, string? PaymentReference);

public sealed record ApprovePayoutRequest(Guid PayoutId);
public sealed record ExecutePayoutRequest(Guid PayoutId);
public sealed record PayoutActionResult(Guid PayoutId, string Status, string? PaymentReference);

// ---- Disputes ------------------------------------------------------------------------------------

public sealed record RaiseDisputeRequest(Guid AttributionId, string RaisedBy, string DisputeReason, string Description);
public sealed record ResolveDisputeRequest(Guid DisputeId, string Status, string? ResolutionNotes, decimal? AmountAdjustmentInr);

/// <summary>A dispute row, enriched for the list with the booking ref + broker name. Mirrors FE DisputeSchema.</summary>
public sealed record DisputeDto(
    Guid DisputeId, Guid AttributionId, string BookingRef, string BrokerName,
    string RaisedBy, string DisputeReason, string Status, DateTimeOffset RaisedAt);

// ---- Attribution ledger list ---------------------------------------------------------------------

/// <summary>
/// One attribution ledger row for the list view. Mirrors FE AttributionSchema. PHI: patient is a FIRST
/// NAME + MASKED phone only; PAN/full phone never appear. bookingRef is the human BKG- number.
/// </summary>
public sealed record AttributionListItemDto(
    Guid AttributionId, string BookingRef, Guid BrokerId, string BrokerName,
    string PatientFirstName, string PatientMaskedPhone, string AttributionSource, string VerificationStatus,
    string CommissionStatus, decimal? CommissionAmountInr, decimal FraudScore, IReadOnlyList<string> FraudFlags, DateTimeOffset CreatedAt);

// ---- Broker wallet (self-service) ----------------------------------------------------------------

public sealed record BrokerWalletDto(
    Guid BrokerId, decimal PendingInr, decimal EarnedInr, decimal ReadyToPayInr,
    decimal LifetimePaidInr, decimal CurrentMonthInr, int CurrentMonthAttributions);

// ---- Campaigns -----------------------------------------------------------------------------------

public sealed record CreateCampaignRequest(
    string CampaignName, string BonusType, decimal? BonusValue, DateTimeOffset StartsAt, DateTimeOffset EndsAt, decimal? TotalBudgetInr);
public sealed record CampaignDto(Guid CampaignId, string CampaignName, string BonusType, decimal? BonusValue, bool IsActive, decimal? TotalBudgetInr, decimal SpentSoFarInr);

// ---- TDS / Form 16A (section 194H) ----------------------------------------------------------------

/// <summary>
/// A TDS certificate (Form 16A) for a paid commission payout. PHI discipline: the deductee PAN is exposed as
/// LAST 4 only here; the legally-required full PAN appears solely on the rendered document at <c>DocumentUrl</c>.
/// <c>Status</c> is 'provisional' until the quarterly TDS return is filed on TRACES and a real
/// <c>TracesCertificateNumber</c> is recorded (external). <c>InvoiceNumber</c> is the payout's INV- number.
/// </summary>
public sealed record Form16ACertificateDto(
    Guid CertificateId, Guid PayoutId, string? InvoiceNumber, string Section, string FinancialYear, string Quarter,
    string DeductorName, string? DeductorTan, string DeducteeName, string? DeducteePanLast4,
    decimal GrossAmountInr, decimal TdsRate, decimal TdsAmountInr, string Status,
    string? TracesCertificateNumber, string DocumentUrl);
