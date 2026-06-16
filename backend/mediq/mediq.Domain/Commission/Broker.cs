namespace mediq.Domain.Commission;

/// <summary>
/// A registered marketing partner — customer-facing term "Care Partner" (NOT "Referral Partner", per MCI
/// 6.4 positioning). Platform-level identity by PHONE (like patients); works with tenants via
/// broker_tenant_links. PAN is encrypted at rest (the entity holds the ciphertext envelope). Maps to
/// <c>commission.brokers</c>.
/// <para>
/// PCPNDT (criminal): <see cref="CanReferPndt"/> is CHECK-forced false at the DB — never set true.
/// </para>
/// </summary>
public sealed class Broker
{
    public Guid BrokerId { get; private set; }
    public string Phone { get; private set; } = default!;
    public string FullName { get; private set; } = default!;
    public string? Email { get; private set; }
    public Guid? UserId { get; private set; }

    public string? PanNumberEnc { get; private set; }   // ciphertext envelope (encrypted_fields_registry, data_class=tax_id)
    public bool PanVerified { get; private set; }
    public string? AadhaarLast4 { get; private set; }
    public string? GstNumber { get; private set; }
    public bool GstVerified { get; private set; }

    public string BrokerType { get; private set; } = default!;
    public string TierLevel { get; private set; } = "basic";
    public decimal MonthlyVolumeInr { get; private set; }

    public string? UpiId { get; private set; }
    public string? BankAccountLast4Enc { get; private set; }   // ciphertext envelope (data_class=banking)
    public string? BankIfsc { get; private set; }
    public string PayoutMethod { get; private set; } = "upi";

    public bool IsActive { get; private set; }
    public DateTime? BlacklistedAt { get; private set; }
    public string? BlacklistReason { get; private set; }

    public bool CanReferPndt { get; private set; }            // ALWAYS false (DB CHECK).
    public bool RequiresConsentForPhi { get; private set; } = true;

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Broker() { }

    public bool IsBlacklisted => BlacklistedAt is not null;
    public bool CanEarn => IsActive && BlacklistedAt is null;

    public static Broker Register(
        string phone, string fullName, string? email, string brokerType, string? panNumberEnc,
        string? gstNumber, string onboardedVia, DateTime nowUtc)
        => new()
        {
            BrokerId = Guid.CreateVersion7(),
            Phone = phone, FullName = fullName, Email = email, BrokerType = brokerType,
            PanNumberEnc = panNumberEnc, GstNumber = gstNumber,
            TierLevel = "basic", PayoutMethod = "upi",
            IsActive = false,                       // must be activated by a tenant admin
            CanReferPndt = false,                   // PCPNDT — never true
            RequiresConsentForPhi = true,
            CreatedAt = nowUtc, UpdatedAt = nowUtc,
        };

    public static Broker FromRow(
        Guid id, string phone, string fullName, string? email, Guid? userId, string? panEnc, bool panVerified,
        string? aadhaarLast4, string? gstNumber, bool gstVerified, string brokerType, string tierLevel,
        decimal monthlyVolume, string? upiId, string? bankLast4Enc, string? bankIfsc, string payoutMethod,
        bool isActive, DateTime? blacklistedAt, string? blacklistReason, bool canReferPndt, DateTime createdAt, DateTime updatedAt)
        => new()
        {
            BrokerId = id, Phone = phone, FullName = fullName, Email = email, UserId = userId,
            PanNumberEnc = panEnc, PanVerified = panVerified, AadhaarLast4 = aadhaarLast4, GstNumber = gstNumber,
            GstVerified = gstVerified, BrokerType = brokerType, TierLevel = tierLevel, MonthlyVolumeInr = monthlyVolume,
            UpiId = upiId, BankAccountLast4Enc = bankLast4Enc, BankIfsc = bankIfsc, PayoutMethod = payoutMethod,
            IsActive = isActive, BlacklistedAt = blacklistedAt, BlacklistReason = blacklistReason,
            CanReferPndt = canReferPndt, CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
}
