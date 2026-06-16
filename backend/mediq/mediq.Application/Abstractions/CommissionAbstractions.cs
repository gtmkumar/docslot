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
    /// Attribution ledger list (most recent first), tenant-scoped + offset-paginated. Returns display-safe
    /// rows: the patient is a FIRST NAME + MASKED phone (PHI), and the booking is the human BKG- ref.
    /// Returns the SharedDataModel list DTO directly (masking applied at the infra seam).
    /// </summary>
    Task<IReadOnlyList<SharedDataModel.Docslot.Commission.AttributionListItemDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct);
}

public sealed record BookingValue(decimal AmountInr, decimal DirectDiscountInr, string? ServiceType, Guid PatientId);

/// <summary>Payout batch persistence + approve/execute (TWO distinct steps, gated by distinct permissions).</summary>
public interface IPayoutRepository
{
    Task<Guid> CreateAsync(Payout payout, CancellationToken ct);
    Task<Payout?> GetByIdAsync(Guid payoutId, Guid tenantId, CancellationToken ct);
    Task ApproveAsync(Guid payoutId, Guid byUserId, DateTime nowUtc, CancellationToken ct);
    Task MarkPaidAsync(Guid payoutId, string paymentReference, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<PayoutDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct);
}

/// <summary>Materialized broker wallet (self-service portal balance).</summary>
public interface IBrokerWalletRepository
{
    Task EnsureExistsAsync(Guid brokerId, CancellationToken ct);
    Task ApplyEarnedAsync(Guid brokerId, decimal amountInr, DateTime nowUtc, CancellationToken ct);
    Task ApplyPaidAsync(Guid brokerId, decimal grossInr, DateTime nowUtc, CancellationToken ct);
    Task<BrokerWalletDto?> GetAsync(Guid brokerId, CancellationToken ct);
}

/// <summary>Disputes + campaigns (admin surface).</summary>
public interface ICommissionAdminRepository
{
    Task<Guid> RaiseDisputeAsync(Guid tenantId, RaiseDisputeRequest request, Guid? byUserId, DateTime nowUtc, CancellationToken ct);
    Task ResolveDisputeAsync(Guid disputeId, ResolveDisputeRequest request, Guid byUserId, DateTime nowUtc, CancellationToken ct);
    Task<Guid> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, Guid? byUserId, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<DisputeDto>> ListDisputesAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Referral link generation (BRK-codes) + click logging (hashed IP).</summary>
public interface IReferralLinkRepository
{
    Task<ReferralLinkDto> CreateAsync(Guid brokerId, CreateReferralLinkRequest request, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<ReferralLinkDto>> ListByBrokerAsync(Guid brokerId, CancellationToken ct);
}

/// <summary>Pure fraud scoring for an attribution (repeat_phone / rapid_burst / self_referral). Score >0.5 → flag.</summary>
public interface IFraudScorer
{
    Task<(decimal Score, string[] Flags)> ScoreAsync(Guid bookingId, Guid brokerId, CancellationToken ct);
}
