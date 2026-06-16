using mediq.Domain.Commission;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace mediq.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the commission tables (slice 07, schema commission). Writes mostly use parameterized raw SQL in the
/// repositories (PAN encryption, the discount-exclusivity trigger, wallet/attribution updates), so EF needs
/// valid mappings primarily for reads. text[] columns map to string[] via Npgsql.
/// </summary>
public sealed class BrokerConfiguration : IEntityTypeConfiguration<Broker>
{
    public void Configure(EntityTypeBuilder<Broker> b)
    {
        b.ToTable("brokers", "commission");
        b.HasKey(x => x.BrokerId);
        b.Property(x => x.BrokerId).HasColumnName("broker_id");
        b.Property(x => x.Phone).HasColumnName("phone");
        b.Property(x => x.FullName).HasColumnName("full_name");
        b.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.PanNumberEnc).HasColumnName("pan_number");
        b.Property(x => x.PanVerified).HasColumnName("pan_verified");
        b.Property(x => x.AadhaarLast4).HasColumnName("aadhaar_last_4");
        b.Property(x => x.GstNumber).HasColumnName("gst_number");
        b.Property(x => x.GstVerified).HasColumnName("gst_verified");
        b.Property(x => x.BrokerType).HasColumnName("broker_type");
        b.Property(x => x.TierLevel).HasColumnName("tier_level");
        b.Property(x => x.MonthlyVolumeInr).HasColumnName("monthly_volume_inr");
        b.Property(x => x.UpiId).HasColumnName("upi_id");
        b.Property(x => x.BankAccountLast4Enc).HasColumnName("bank_account_last_4");
        b.Property(x => x.BankIfsc).HasColumnName("bank_ifsc");
        b.Property(x => x.PayoutMethod).HasColumnName("payout_method");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.BlacklistedAt).HasColumnName("blacklisted_at");
        b.Property(x => x.BlacklistReason).HasColumnName("blacklist_reason");
        b.Property(x => x.CanReferPndt).HasColumnName("can_refer_pndt");
        b.Property(x => x.RequiresConsentForPhi).HasColumnName("requires_consent_for_phi");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class AttributionConfiguration : IEntityTypeConfiguration<Attribution>
{
    public void Configure(EntityTypeBuilder<Attribution> b)
    {
        b.ToTable("attributions", "commission");
        b.HasKey(x => x.AttributionId);
        b.Property(x => x.AttributionId).HasColumnName("attribution_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.BookingId).HasColumnName("booking_id");
        b.Property(x => x.BrokerId).HasColumnName("broker_id");
        b.Property(x => x.AttributionSource).HasColumnName("attribution_source");
        b.Property(x => x.VerificationStatus).HasColumnName("verification_status");
        b.Property(x => x.RuleId).HasColumnName("rule_id");
        b.Property(x => x.CommissionAmountInr).HasColumnName("commission_amount_inr");
        b.Property(x => x.CommissionStatus).HasColumnName("commission_status");
        b.Property(x => x.FraudScore).HasColumnName("fraud_score");
        b.Property(x => x.FraudFlags).HasColumnName("fraud_flags");
        b.Property(x => x.PayoutId).HasColumnName("payout_id");
        b.Property(x => x.AttributedAt).HasColumnName("attributed_at");
        b.Property(x => x.EarnedAt).HasColumnName("earned_at");
    }
}

public sealed class CommissionRuleConfiguration : IEntityTypeConfiguration<CommissionRule>
{
    public void Configure(EntityTypeBuilder<CommissionRule> b)
    {
        b.ToTable("commission_rules", "commission");
        b.HasKey(x => x.RuleId);
        b.Property(x => x.RuleId).HasColumnName("rule_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.RuleName).HasColumnName("rule_name");
        b.Property(x => x.AppliesToBrokerTier).HasColumnName("applies_to_broker_tier");
        b.Property(x => x.AppliesToBrokerType).HasColumnName("applies_to_broker_type");
        b.Property(x => x.AppliesToServiceType).HasColumnName("applies_to_service_type");
        b.Property(x => x.MinBookingValueInr).HasColumnName("min_booking_value_inr");
        b.Property(x => x.MaxBookingValueInr).HasColumnName("max_booking_value_inr");
        b.Property(x => x.CalcType).HasColumnName("calc_type");
        b.Property(x => x.FlatAmountInr).HasColumnName("flat_amount_inr");
        b.Property(x => x.Percentage).HasColumnName("percentage");
        b.Property(x => x.MinCommissionInr).HasColumnName("min_commission_inr");
        b.Property(x => x.MaxCommissionInr).HasColumnName("max_commission_inr");
        b.Property(x => x.MaxMonthlyPerBrokerInr).HasColumnName("max_monthly_per_broker_inr");
        b.Property(x => x.Priority).HasColumnName("priority");
        b.Property(x => x.ExcludesPndt).HasColumnName("excludes_pndt");
        b.Property(x => x.FirstBookingOnly).HasColumnName("first_booking_only");   // added by 09_chat_identity
    }
}

public sealed class PayoutConfiguration : IEntityTypeConfiguration<Payout>
{
    public void Configure(EntityTypeBuilder<Payout> b)
    {
        b.ToTable("payouts", "commission");
        b.HasKey(x => x.PayoutId);
        b.Property(x => x.PayoutId).HasColumnName("payout_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.BrokerId).HasColumnName("broker_id");
        b.Property(x => x.PeriodStart).HasColumnName("period_start");
        b.Property(x => x.PeriodEnd).HasColumnName("period_end");
        b.Property(x => x.AttributionCount).HasColumnName("attribution_count");
        b.Property(x => x.GrossAmountInr).HasColumnName("gross_amount_inr");
        b.Property(x => x.TdsRate).HasColumnName("tds_rate");
        b.Property(x => x.TdsAmountInr).HasColumnName("tds_amount_inr");
        b.Property(x => x.GstRate).HasColumnName("gst_rate");
        b.Property(x => x.GstAmountInr).HasColumnName("gst_amount_inr");
        b.Property(x => x.NetAmountInr).HasColumnName("net_amount_inr");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.PaymentMethod).HasColumnName("payment_method");
        b.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
        b.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        b.Property(x => x.PaymentReference).HasColumnName("payment_reference");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}
