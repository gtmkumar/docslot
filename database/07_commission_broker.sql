-- ============================================================================
-- DocSlot Platform Database — Part 7: Commission & Broker Referral Schema
-- ============================================================================
-- A formalized broker/referral system that channels the informal Indian
-- healthcare referral economy through DocSlot, with audit trail and tax
-- compliance built in.
--
-- LEGAL POSITIONING (read before extending this schema):
-- - This is a MARKETING/FACILITATION fee from the FACILITY to the BROKER
-- - NOT a doctor-to-doctor referral fee (which violates MCI Code 6.4)
-- - The doctor never directly pays anyone; the facility pays the broker
-- - PNDT-related scans (gender determination) are HARD-EXCLUDED in code
-- - Brokers are registered as platform marketing partners with KYC
--
-- DESIGN GOALS:
-- 1. Three attribution mechanisms (link, app-booking, post-hoc claim)
-- 2. Tenant-configurable commission rules (flat, percentage, tiered)
-- 3. PAN/GST/TDS compliance for Indian tax law
-- 4. Real-time broker wallet visible in Broker Portal
-- 5. Audit trail for every attribution decision
-- 6. DPDP-compliant: brokers see minimal patient data (phone + first name only)
--
-- DEPENDENCIES:
-- Run AFTER 01_platform_core.sql, 03_docslot.sql, 05_security_hardening.sql
--
-- EXECUTION:
-- psql -d docslot_platform -f 07_commission_broker.sql
-- ============================================================================

CREATE SCHEMA IF NOT EXISTS commission;
COMMENT ON SCHEMA commission IS 'Broker referral and commission tracking. Formalizes the Indian healthcare referral economy.';

-- Register the commission "product" in platform.products so RBAC works
INSERT INTO platform.products (product_key, name, description, schema_name)
VALUES ('commission', 'Broker Commission', 'Referral attribution and commission management for healthcare facilities', 'commission')
ON CONFLICT (product_key) DO NOTHING;

-- ============================================================================
-- TABLE C1: BROKERS (registered marketing partners)
-- ============================================================================
-- A broker is anyone authorized to refer patients to DocSlot facilities:
-- medical reps, corporate HR, insurance panel managers, aggregator agents,
-- hotel concierges, community health workers, individual entrepreneurs.
--
-- Identity is the broker's PHONE NUMBER (Indian context — phone is the
-- universal identifier). KYC tied to PAN for tax compliance.
CREATE TABLE commission.brokers (
    broker_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Identity (phone is canonical)
    phone               VARCHAR(15) NOT NULL UNIQUE,         -- +91XXXXXXXXXX
    full_name           VARCHAR(200) NOT NULL,
    email               CITEXT,
    user_id             UUID REFERENCES platform.users(user_id),
                                                              -- Optional: link to platform.users if broker has a portal login

    -- KYC for tax compliance
    -- pan_number / bank_account_last_4 hold the APP-LAYER ENCRYPTED ENVELOPE (ciphertext), not the raw
    -- value — so they are TEXT, wide enough for the AES-GCM envelope. Registered in
    -- platform.encrypted_fields_registry (data_class=tax_id / banking, legal_obligation).
    pan_number          TEXT,                                 -- Indian PAN — stored ENCRYPTED (envelope), never plaintext
    pan_verified        BOOLEAN NOT NULL DEFAULT false,
    pan_verified_at     TIMESTAMPTZ,
    aadhaar_last_4      VARCHAR(4),                           -- Only last 4 stored (full Aadhaar is illegal to store)
    aadhaar_verified    BOOLEAN NOT NULL DEFAULT false,
    gst_number          VARCHAR(15),                          -- 22AAAAA0000A1Z5 format; required if total earnings >₹20L/yr
    gst_verified        BOOLEAN NOT NULL DEFAULT false,

    -- Broker classification
    broker_type         VARCHAR(30) NOT NULL CHECK (broker_type IN (
        'medical_rep',           -- Pharma sales rep
        'corporate_hr',          -- Company HR doing employee bookings
        'insurance_panel',       -- Insurance company panel coordinator
        'aggregator_agent',      -- Local healthcare navigator
        'community_worker',      -- ASHA/ANM (sensitive — requires extra approval)
        'hotel_concierge',       -- Medical tourism
        'individual',            -- Anyone else
        'platform_partner'       -- Strategic partnership (Pharmeasy, Practo, etc.)
    )),

    -- Tier (drives commission rate)
    tier_level          VARCHAR(20) NOT NULL DEFAULT 'basic'
        CHECK (tier_level IN ('basic', 'silver', 'gold', 'platinum')),
    tier_upgraded_at    TIMESTAMPTZ,
    monthly_volume_inr  DECIMAL(12,2) DEFAULT 0,             -- Rolling 30-day commission earned (drives tier)

    -- Banking for payouts
    upi_id              VARCHAR(100),                         -- name@bank
    bank_account_last_4 TEXT,                                 -- Stored ENCRYPTED (envelope); registered for encryption
    bank_ifsc           VARCHAR(11),
    payout_method       VARCHAR(20) NOT NULL DEFAULT 'upi'
        CHECK (payout_method IN ('upi', 'bank_transfer', 'razorpay_x', 'cheque', 'hold')),

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT false,       -- Must be activated by tenant admin
    activated_at        TIMESTAMPTZ,
    activated_by_user_id UUID REFERENCES platform.users(user_id),
    suspended_at        TIMESTAMPTZ,
    suspended_reason    TEXT,
    blacklisted_at      TIMESTAMPTZ,                          -- Fraud / violation — permanent
    blacklist_reason    TEXT,

    -- Onboarding
    referred_by_broker_id UUID REFERENCES commission.brokers(broker_id),
                                                              -- Brokers can refer brokers (with limited downstream commission)
    onboarded_via       VARCHAR(50),                          -- 'whatsapp', 'tenant_invite', 'web_signup', 'broker_referral'

    -- Compliance flags
    can_refer_pndt      BOOLEAN NOT NULL DEFAULT false,       -- HARD-CODED FALSE — PNDT excluded by law
    requires_consent_for_phi BOOLEAN NOT NULL DEFAULT true,   -- Default: broker sees only first-name + masked phone

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_active_at      TIMESTAMPTZ,
    metadata            JSONB NOT NULL DEFAULT '{}'           -- Flexible: language, region, specialty preference
);

CREATE INDEX idx_brokers_phone ON commission.brokers(phone) WHERE is_active = true;
CREATE INDEX idx_brokers_type_active ON commission.brokers(broker_type, tier_level) WHERE is_active = true;
CREATE INDEX idx_brokers_tier ON commission.brokers(tier_level) WHERE is_active = true;
CREATE INDEX idx_brokers_user ON commission.brokers(user_id) WHERE user_id IS NOT NULL;
CREATE INDEX idx_brokers_blacklisted ON commission.brokers(blacklisted_at) WHERE blacklisted_at IS NOT NULL;

-- PNDT constraint enforced at DB level — code mistakes won't bypass
ALTER TABLE commission.brokers
    ADD CONSTRAINT pndt_referral_forbidden CHECK (can_refer_pndt = false);

COMMENT ON TABLE commission.brokers IS 'Registered marketing partners eligible for referral commissions. PAN required for >₹15k/yr earnings.';
COMMENT ON COLUMN commission.brokers.pan_number IS 'Encrypted at app layer; registered in platform.encrypted_fields_registry as data_class=pan';
COMMENT ON CONSTRAINT pndt_referral_forbidden ON commission.brokers IS 'Hard block — PCPNDT Act prohibits commission on gender-determination referrals.';

CREATE TRIGGER trg_brokers_updated_at BEFORE UPDATE ON commission.brokers
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE C2: BROKER_TENANT_LINKS (which brokers work with which facilities)
-- ============================================================================
-- A broker can work with multiple tenants; commission rules are tenant-specific.
CREATE TABLE commission.broker_tenant_links (
    link_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    broker_id           UUID NOT NULL REFERENCES commission.brokers(broker_id) ON DELETE CASCADE,
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    -- Per-tenant status (broker can be active globally but suspended for one tenant)
    is_active           BOOLEAN NOT NULL DEFAULT true,
    activated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    activated_by_user_id UUID REFERENCES platform.users(user_id),
    suspended_at        TIMESTAMPTZ,
    suspended_reason    TEXT,

    -- Per-tenant tier override (rare; usually use global tier)
    tier_override       VARCHAR(20)
        CHECK (tier_override IS NULL OR tier_override IN ('basic', 'silver', 'gold', 'platinum')),

    -- Tenant-specific terms
    contract_signed_at  TIMESTAMPTZ,
    contract_document_url TEXT,                                -- Signed broker agreement
    bilateral_terms     JSONB NOT NULL DEFAULT '{}',          -- Special arrangement (e.g., "first 100 referrals free")

    -- Stats (denormalized for fast lookup)
    total_attributions  INT NOT NULL DEFAULT 0,
    total_earned_inr    DECIMAL(12,2) NOT NULL DEFAULT 0,
    last_attribution_at TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(broker_id, tenant_id)
);

CREATE INDEX idx_bt_links_broker ON commission.broker_tenant_links(broker_id) WHERE is_active = true;
CREATE INDEX idx_bt_links_tenant ON commission.broker_tenant_links(tenant_id) WHERE is_active = true;

CREATE TRIGGER trg_bt_links_updated_at BEFORE UPDATE ON commission.broker_tenant_links
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE C3: REFERRAL_LINKS (trackable URLs each broker generates)
-- ============================================================================
-- Each broker can generate unique referral links for different campaigns,
-- WhatsApp shares, QR codes for clinic posters, etc.
CREATE TABLE commission.referral_links (
    link_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    broker_id           UUID NOT NULL REFERENCES commission.brokers(broker_id) ON DELETE CASCADE,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                              -- NULL = works for any tenant the broker is linked to

    -- The trackable code (short, shareable)
    short_code          VARCHAR(20) NOT NULL UNIQUE,          -- 'BRK-RAVI-1234' or 'AP-MUM-9001'
    target_url          TEXT,                                  -- Where the link redirects (default: WhatsApp deep link)

    -- Targeting
    target_facility_id  UUID REFERENCES docslot.healthcare_facilities(facility_id),
    target_doctor_id    UUID REFERENCES docslot.doctors(doctor_id),
    target_department_id UUID REFERENCES docslot.departments(department_id),
    target_service_type VARCHAR(50),                          -- 'cardiology', 'cataract', 'lab_test'

    -- Campaign metadata
    campaign_name       VARCHAR(200),
    notes               TEXT,

    -- Lifecycle
    expires_at          TIMESTAMPTZ,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    click_count         INT NOT NULL DEFAULT 0,
    conversion_count    INT NOT NULL DEFAULT 0,               -- Clicks that became bookings

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_referral_links_code ON commission.referral_links(short_code) WHERE is_active = true;
CREATE INDEX idx_referral_links_broker ON commission.referral_links(broker_id, created_at DESC);

CREATE TRIGGER trg_referral_links_updated_at BEFORE UPDATE ON commission.referral_links
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE C4: REFERRAL_CLICKS (tracking link engagement)
-- ============================================================================
-- Every click on a referral link logged. Enables broker analytics dashboards
-- and click-to-conversion ratio measurement. Lightweight write-heavy table.
CREATE TABLE commission.referral_clicks (
    click_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    link_id             UUID NOT NULL REFERENCES commission.referral_links(link_id) ON DELETE CASCADE,
    short_code          VARCHAR(20) NOT NULL,                 -- Denormalized for speed
    clicked_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Attribution data (cookie/session level — no PII yet)
    session_token       VARCHAR(64),                          -- Browser session ID
    ip_address_hash     VARCHAR(64),                          -- Hashed IP (not raw — privacy)
    user_agent_brief    VARCHAR(50),                          -- 'iOS_Chrome', 'Android_WA'
    referrer_source     VARCHAR(30),                          -- 'whatsapp', 'qr_scan', 'sms', 'instagram'

    -- Conversion tracking (set when click leads to booking)
    converted_to_booking_id UUID REFERENCES docslot.bookings(booking_id),
    converted_at        TIMESTAMPTZ
);

CREATE INDEX idx_clicks_link ON commission.referral_clicks(link_id, clicked_at DESC);
CREATE INDEX idx_clicks_session ON commission.referral_clicks(session_token) WHERE session_token IS NOT NULL;
CREATE INDEX idx_clicks_converted ON commission.referral_clicks(converted_at DESC) WHERE converted_at IS NOT NULL;

-- ============================================================================
-- TABLE C5: COMMISSION_RULES (tenant-defined rate cards)
-- ============================================================================
-- Each tenant configures their own commission structure. Can have multiple
-- rules with conditions (service type, broker tier, booking value).
-- When a booking is attributed, the engine picks the highest-priority matching
-- rule.
CREATE TABLE commission.commission_rules (
    rule_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    -- Rule identification
    rule_name           VARCHAR(200) NOT NULL,                -- 'Standard consult commission', 'Cardiac surgery special'
    rule_key            VARCHAR(50) NOT NULL,                 -- Programmatic key
    description         TEXT,

    -- Conditions (when does this rule apply?)
    applies_to_broker_tier VARCHAR(20)[]                       -- ['silver', 'gold'] or NULL = all tiers
        CHECK (applies_to_broker_tier IS NULL OR
               applies_to_broker_tier <@ ARRAY['basic','silver','gold','platinum']::VARCHAR(20)[]),
    applies_to_broker_type VARCHAR(30)[],                     -- ['medical_rep', 'corporate_hr'] or NULL
    applies_to_service_type VARCHAR(50)[],                    -- ['consultation', 'lab_test', 'procedure'] or NULL
    applies_to_department_id UUID REFERENCES docslot.departments(department_id),
    applies_to_doctor_id UUID REFERENCES docslot.doctors(doctor_id),
    min_booking_value_inr DECIMAL(12,2),
    max_booking_value_inr DECIMAL(12,2),

    -- Calculation (one of: flat, percentage, tiered_table)
    calc_type           VARCHAR(20) NOT NULL CHECK (calc_type IN ('flat', 'percentage', 'tiered_table')),
    flat_amount_inr     DECIMAL(10,2),                        -- For calc_type=flat
    percentage          DECIMAL(5,2),                         -- For calc_type=percentage, e.g., 10.00 = 10%
    tiered_table        JSONB,                                -- For calc_type=tiered_table: [{min, max, amount_or_pct}]

    -- Caps and floors
    min_commission_inr  DECIMAL(10,2),                        -- Floor — pay at least this much
    max_commission_inr  DECIMAL(10,2),                        -- Ceiling — pay at most this much per booking
    max_monthly_per_broker_inr DECIMAL(12,2),                 -- Anti-runaway: cap monthly payout to one broker

    -- Priority (higher = applied first when multiple rules match)
    priority            INT NOT NULL DEFAULT 100,

    -- PCPNDT compliance enforcement
    excludes_pndt       BOOLEAN NOT NULL DEFAULT true,        -- Always true — schema enforces below

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT false,       -- Rules start inactive until approved
    effective_from      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    effective_until     TIMESTAMPTZ,
    approved_by_user_id UUID REFERENCES platform.users(user_id),
    approved_at         TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, rule_key)
);

CREATE INDEX idx_rules_tenant_active ON commission.commission_rules(tenant_id, priority DESC)
    WHERE is_active = true;

ALTER TABLE commission.commission_rules
    ADD CONSTRAINT excludes_pndt_required CHECK (excludes_pndt = true);

CREATE TRIGGER trg_rules_updated_at BEFORE UPDATE ON commission.commission_rules
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE C6: ATTRIBUTIONS (the core — who gets credit for which booking)
-- ============================================================================
-- One row per (broker, booking) attribution. The attribution_source field
-- captures HOW the broker got credit. The verification_status traces
-- whether the patient actually came through this broker.
CREATE TABLE commission.attributions (
    attribution_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id) ON DELETE CASCADE,
    broker_id           UUID NOT NULL REFERENCES commission.brokers(broker_id),

    -- How was this attribution made?
    attribution_source  VARCHAR(30) NOT NULL CHECK (attribution_source IN (
        'referral_link',           -- Patient clicked broker's URL
        'broker_portal_booking',   -- Broker logged in and booked
        'whatsapp_template',       -- Broker sent template message with embedded code
        'post_hoc_claim',          -- Broker claimed within 48h, patient confirmed
        'qr_scan',                 -- Patient scanned broker's QR code
        'manual_admin'             -- Tenant admin manually attributed (rare)
    )),

    -- Verification (especially for post-hoc claims)
    verification_status VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (verification_status IN (
            'pending',          -- Just created
            'auto_verified',    -- Link/portal booking — no patient confirmation needed
            'patient_confirmed',-- WhatsApp OTP confirmed the broker referred
            'patient_denied',   -- Patient said no — attribution rejected
            'no_response',      -- Patient didn't respond within 24h
            'admin_override'    -- Tenant admin manually verified or denied
        )),
    verified_at         TIMESTAMPTZ,
    patient_confirmation_message_id UUID REFERENCES docslot.wa_message_log(log_id),
    admin_override_by_user_id UUID REFERENCES platform.users(user_id),
    admin_override_reason TEXT,

    -- The supporting evidence
    referral_link_id    UUID REFERENCES commission.referral_links(link_id),
    referral_click_id   UUID REFERENCES commission.referral_clicks(click_id),
    source_metadata     JSONB NOT NULL DEFAULT '{}',           -- E.g., {wa_template_id, qr_scan_location}

    -- Commission calculation
    rule_id             UUID REFERENCES commission.commission_rules(rule_id),
    commission_amount_inr DECIMAL(10,2),                       -- Calculated at attribution time
    commission_status   VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (commission_status IN (
            'pending',          -- Booking not yet completed
            'earned',           -- Visit completed; commission accrued
            'ready_to_pay',     -- Past settlement window; included in next payout
            'paid',             -- Actually disbursed
            'reversed',         -- Patient refunded → commission clawed back
            'disputed',         -- Tenant or broker disputes; under review
            'rejected'          -- Verification failed
        )),

    -- Lifecycle timestamps
    attributed_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    earned_at           TIMESTAMPTZ,                          -- When booking marked complete
    paid_at             TIMESTAMPTZ,
    payout_id           UUID,                                  -- References commission.payouts(payout_id)

    -- Anti-fraud
    fraud_score         DECIMAL(4,3),                          -- 0.000 to 1.000; flagged if >0.7
    fraud_flags         VARCHAR(50)[],                        -- ['repeat_phone', 'rapid_burst', 'self_referral']

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(booking_id, broker_id)                              -- A broker can attribute a booking only once
);

CREATE INDEX idx_attribution_booking ON commission.attributions(booking_id);
CREATE INDEX idx_attribution_broker ON commission.attributions(broker_id, attributed_at DESC);
CREATE INDEX idx_attribution_tenant ON commission.attributions(tenant_id, attributed_at DESC);
CREATE INDEX idx_attribution_pending_payout ON commission.attributions(tenant_id, broker_id)
    WHERE commission_status = 'ready_to_pay';
CREATE INDEX idx_attribution_pending_verification ON commission.attributions(attributed_at)
    WHERE verification_status = 'pending';
CREATE INDEX idx_attribution_flagged ON commission.attributions(fraud_score DESC, created_at DESC)
    WHERE fraud_score > 0.5;

CREATE TRIGGER trg_attributions_updated_at BEFORE UPDATE ON commission.attributions
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

COMMENT ON TABLE commission.attributions IS 'Core attribution ledger. One row per (broker, booking). Drives commission calculation.';

-- ============================================================================
-- TABLE C7: PAYOUTS (batch settlement records)
-- ============================================================================
-- A payout aggregates many attributions for one broker for one settlement
-- period. Tax compliance fields (TDS, GST) populated automatically.
CREATE TABLE commission.payouts (
    payout_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    broker_id           UUID NOT NULL REFERENCES commission.brokers(broker_id),

    -- Settlement window
    period_start        DATE NOT NULL,
    period_end          DATE NOT NULL,

    -- Calculation
    attribution_count   INT NOT NULL DEFAULT 0,
    gross_amount_inr    DECIMAL(12,2) NOT NULL DEFAULT 0,
    tds_rate            DECIMAL(5,2) NOT NULL DEFAULT 5.00,    -- 5% u/s 194H by default
    tds_amount_inr      DECIMAL(12,2) NOT NULL DEFAULT 0,
    gst_rate            DECIMAL(5,2),                          -- 18% if broker is GST-registered
    gst_amount_inr      DECIMAL(12,2) DEFAULT 0,
    net_amount_inr      DECIMAL(12,2) NOT NULL DEFAULT 0,      -- gross - tds (or +gst if registered)

    -- Status
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN (
            'pending',          -- Aggregated but not yet sent
            'approved',         -- Tenant finance approved
            'processing',       -- Sent to payment gateway
            'paid',             -- Confirmed by gateway
            'failed',           -- Gateway returned failure
            'on_hold',          -- KYC issue, compliance check
            'reversed'          -- Returned by bank, disputed
        )),

    -- Payment execution
    payment_method      VARCHAR(20) NOT NULL,                   -- 'upi', 'bank_transfer', 'razorpay_x'
    payment_reference   VARCHAR(100),                           -- UTR or transaction ID
    payment_gateway     VARCHAR(50),                            -- 'razorpay', 'cashfree', 'paytm_business'

    -- Invoicing (tax compliance)
    invoice_number      VARCHAR(50) UNIQUE,                     -- Auto-generated: TENANT-BRK-YYYYMM-NNNN
    invoice_pdf_url     TEXT,
    invoice_generated_at TIMESTAMPTZ,
    form_16a_url        TEXT,                                   -- TDS certificate (quarterly issued)

    -- Approval and audit
    approved_by_user_id UUID REFERENCES platform.users(user_id),
    approved_at         TIMESTAMPTZ,

    -- Lifecycle
    initiated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    failure_reason      TEXT,

    metadata            JSONB NOT NULL DEFAULT '{}',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_payouts_broker ON commission.payouts(broker_id, period_end DESC);
CREATE INDEX idx_payouts_tenant_pending ON commission.payouts(tenant_id, initiated_at)
    WHERE status IN ('pending', 'approved', 'processing');
CREATE INDEX idx_payouts_failed ON commission.payouts(initiated_at DESC) WHERE status = 'failed';

CREATE TRIGGER trg_payouts_updated_at BEFORE UPDATE ON commission.payouts
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- Add the FK from attributions.payout_id now that payouts table exists
ALTER TABLE commission.attributions
    ADD CONSTRAINT fk_attribution_payout
    FOREIGN KEY (payout_id) REFERENCES commission.payouts(payout_id);

-- ============================================================================
-- TABLE C8: BROKER_WALLET (real-time balance, denormalized for speed)
-- ============================================================================
-- One row per broker. Updated by triggers on attributions and payouts.
-- Source of truth for "what does broker see in their portal right now?"
CREATE TABLE commission.broker_wallets (
    broker_id           UUID PRIMARY KEY REFERENCES commission.brokers(broker_id) ON DELETE CASCADE,

    -- Pending (booking not yet completed)
    pending_inr         DECIMAL(12,2) NOT NULL DEFAULT 0,

    -- Earned (booking completed, awaiting settlement window)
    earned_inr          DECIMAL(12,2) NOT NULL DEFAULT 0,

    -- Ready to pay (past settlement window, included in next payout)
    ready_to_pay_inr    DECIMAL(12,2) NOT NULL DEFAULT 0,

    -- Lifetime totals
    lifetime_attributions INT NOT NULL DEFAULT 0,
    lifetime_paid_inr   DECIMAL(12,2) NOT NULL DEFAULT 0,
    lifetime_reversed_inr DECIMAL(12,2) NOT NULL DEFAULT 0,

    -- Current month (resets monthly)
    current_month_inr   DECIMAL(12,2) NOT NULL DEFAULT 0,
    current_month_attributions INT NOT NULL DEFAULT 0,

    -- Last activity
    last_attribution_at TIMESTAMPTZ,
    last_payout_at      TIMESTAMPTZ,

    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE commission.broker_wallets IS 'Materialized wallet state. Updated by triggers; query this for real-time Broker Portal balance.';

-- ============================================================================
-- TABLE C9: ATTRIBUTION_DISPUTES (when tenant or broker disagrees)
-- ============================================================================
CREATE TABLE commission.attribution_disputes (
    dispute_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    attribution_id      UUID NOT NULL REFERENCES commission.attributions(attribution_id) ON DELETE CASCADE,
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    -- Who disputed
    raised_by           VARCHAR(20) NOT NULL CHECK (raised_by IN ('broker', 'tenant_staff', 'platform_audit')),
    raised_by_user_id   UUID REFERENCES platform.users(user_id),
    raised_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- The argument
    dispute_reason      VARCHAR(50) NOT NULL CHECK (dispute_reason IN (
        'incorrect_attribution',    -- Broker didn't actually refer
        'duplicate_claim',          -- Two brokers claiming same booking
        'pndt_violation',           -- Booking should be excluded
        'patient_dispute',          -- Patient says no broker referred them
        'fraud_suspected',          -- Self-referral or fake bookings
        'commission_calculation_wrong',
        'other'
    )),
    description         TEXT NOT NULL,
    evidence_urls       TEXT[],

    -- Resolution
    status              VARCHAR(20) NOT NULL DEFAULT 'open'
        CHECK (status IN ('open', 'investigating', 'resolved_broker_wins', 'resolved_tenant_wins', 'resolved_compromise', 'closed_no_action')),
    resolved_at         TIMESTAMPTZ,
    resolved_by_user_id UUID REFERENCES platform.users(user_id),
    resolution_notes    TEXT,
    resolution_amount_adjustment_inr DECIMAL(10,2),            -- Negative = clawback

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_disputes_attribution ON commission.attribution_disputes(attribution_id);
CREATE INDEX idx_disputes_open ON commission.attribution_disputes(tenant_id, raised_at) WHERE status IN ('open', 'investigating');

CREATE TRIGGER trg_disputes_updated_at BEFORE UPDATE ON commission.attribution_disputes
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE C10: BROKER_CAMPAIGNS (organized promotional campaigns)
-- ============================================================================
-- Tenants run periodic campaigns: "20% extra commission on cardiac surgery
-- referrals this month". Tracks these explicitly with start/end dates.
CREATE TABLE commission.broker_campaigns (
    campaign_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    campaign_name       VARCHAR(200) NOT NULL,
    description         TEXT,

    -- Targeting (which brokers + which services)
    target_broker_types VARCHAR(30)[],
    target_broker_tiers VARCHAR(20)[],
    target_services     VARCHAR(50)[],
    target_doctor_ids   UUID[],

    -- Bonus structure (on top of base commission)
    bonus_type          VARCHAR(20) NOT NULL CHECK (bonus_type IN ('flat_bonus_per_booking', 'percentage_multiplier', 'tier_upgrade')),
    bonus_value         DECIMAL(10,2),                          -- ₹500 flat or 1.5x multiplier
    min_bookings_for_bonus INT,                                 -- Hit 10 bookings to unlock

    -- Timing
    starts_at           TIMESTAMPTZ NOT NULL,
    ends_at             TIMESTAMPTZ NOT NULL,

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT false,
    total_budget_inr    DECIMAL(12,2),                          -- Cap total campaign spend
    spent_so_far_inr    DECIMAL(12,2) NOT NULL DEFAULT 0,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by_user_id  UUID REFERENCES platform.users(user_id)
);

CREATE INDEX idx_campaigns_active ON commission.broker_campaigns(tenant_id, ends_at) WHERE is_active = true;

CREATE TRIGGER trg_campaigns_updated_at BEFORE UPDATE ON commission.broker_campaigns
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- VIEW: Active rule for a booking (used by attribution engine)
-- ============================================================================
CREATE OR REPLACE VIEW commission.v_applicable_rules AS
SELECT
    cr.rule_id,
    cr.tenant_id,
    cr.rule_name,
    cr.calc_type,
    cr.flat_amount_inr,
    cr.percentage,
    cr.tiered_table,
    cr.min_commission_inr,
    cr.max_commission_inr,
    cr.priority,
    cr.applies_to_broker_tier,
    cr.applies_to_broker_type,
    cr.applies_to_service_type
FROM commission.commission_rules cr
WHERE cr.is_active = true
  AND cr.effective_from <= NOW()
  AND (cr.effective_until IS NULL OR cr.effective_until > NOW())
ORDER BY cr.priority DESC;

-- ============================================================================
-- VIEW: Broker leaderboard (for healthy competition)
-- ============================================================================
CREATE OR REPLACE VIEW commission.v_broker_leaderboard AS
SELECT
    b.broker_id,
    b.full_name,
    b.tier_level,
    b.broker_type,
    bw.current_month_inr,
    bw.current_month_attributions,
    bw.lifetime_attributions,
    bw.lifetime_paid_inr,
    RANK() OVER (PARTITION BY b.tier_level ORDER BY bw.current_month_inr DESC) AS tier_rank,
    RANK() OVER (ORDER BY bw.current_month_inr DESC) AS overall_rank
FROM commission.brokers b
JOIN commission.broker_wallets bw ON bw.broker_id = b.broker_id
WHERE b.is_active = true
  AND b.blacklisted_at IS NULL;

-- ============================================================================
-- VIEW: Pending payouts ready for batch processing
-- ============================================================================
CREATE OR REPLACE VIEW commission.v_ready_payouts AS
SELECT
    a.tenant_id,
    a.broker_id,
    b.full_name AS broker_name,
    b.pan_number,
    b.upi_id,
    b.gst_number,
    COUNT(*) AS attribution_count,
    SUM(a.commission_amount_inr) AS gross_amount_inr,
    MIN(a.earned_at) AS oldest_earned_at,
    MAX(a.earned_at) AS newest_earned_at
FROM commission.attributions a
JOIN commission.brokers b ON b.broker_id = a.broker_id
WHERE a.commission_status = 'ready_to_pay'
  AND b.is_active = true
  AND b.blacklisted_at IS NULL
GROUP BY a.tenant_id, a.broker_id, b.full_name, b.pan_number, b.upi_id, b.gst_number
HAVING SUM(a.commission_amount_inr) >= 100;  -- Minimum payout threshold ₹100

-- ============================================================================
-- COMMISSION-SPECIFIC PERMISSIONS (registered with platform.permissions)
-- ============================================================================
DO $perms$
DECLARE
    commission_product_id UUID;
BEGIN
    SELECT product_id INTO commission_product_id FROM platform.products WHERE product_key = 'commission';

    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    -- Broker management
    ('commission.broker.read', commission_product_id, 'brokers', 'read', 'tenant', 'View brokers linked to tenant', false),
    ('commission.broker.invite', commission_product_id, 'brokers', 'create', 'tenant', 'Invite new broker', false),
    ('commission.broker.activate', commission_product_id, 'brokers', 'update', 'tenant', 'Activate a broker for this tenant', true),
    ('commission.broker.suspend', commission_product_id, 'brokers', 'update', 'tenant', 'Suspend a broker', true),
    ('commission.broker.blacklist', commission_product_id, 'brokers', 'update', 'platform', 'Permanently blacklist (platform-level)', true),

    -- Self-service (broker accessing own data)
    ('commission.broker.read_self', commission_product_id, 'brokers', 'read', 'self', 'Broker views own profile, attributions, wallet', false),
    ('commission.broker.update_self', commission_product_id, 'brokers', 'update', 'self', 'Broker updates own bank/UPI details', false),
    ('commission.broker.generate_link_self', commission_product_id, 'referral_links', 'create', 'self', 'Broker creates referral links', false),

    -- Rule management
    ('commission.rules.read', commission_product_id, 'commission_rules', 'read', 'tenant', 'View commission rate cards', false),
    ('commission.rules.create', commission_product_id, 'commission_rules', 'create', 'tenant', 'Create commission rule (draft)', true),
    ('commission.rules.approve', commission_product_id, 'commission_rules', 'update', 'tenant', 'Approve commission rule for production', true),

    -- Attribution
    ('commission.attribution.read', commission_product_id, 'attributions', 'read', 'tenant', 'View attribution ledger', false),
    ('commission.attribution.override', commission_product_id, 'attributions', 'update', 'tenant', 'Manual override of attribution (audited)', true),
    ('commission.attribution.claim', commission_product_id, 'attributions', 'create', 'tenant', 'File a post-hoc broker attribution claim (patient confirms via OTP before it can earn)', false),

    -- Payouts (financial — requires elevated permission)
    ('commission.payouts.read', commission_product_id, 'payouts', 'read', 'tenant', 'View payout history', false),
    ('commission.payouts.approve', commission_product_id, 'payouts', 'update', 'tenant', 'Approve payout for execution', true),
    ('commission.payouts.execute', commission_product_id, 'payouts', 'update', 'platform', 'Trigger actual UPI/bank transfer', true),

    -- Disputes
    ('commission.dispute.raise', commission_product_id, 'attribution_disputes', 'create', 'tenant', 'Raise a dispute', false),
    ('commission.dispute.resolve', commission_product_id, 'attribution_disputes', 'update', 'tenant', 'Resolve disputes', true),

    -- Campaigns
    ('commission.campaign.manage', commission_product_id, 'broker_campaigns', 'update', 'tenant', 'Run promotional campaigns', true);

    -- Super admin gets EVERY commission permission (incl. platform-scoped payouts.execute + broker.blacklist).
    -- The original super_admin seed (01_platform_core) granted all permissions that existed THEN; product
    -- permissions added by later files (like these) must be granted to super_admin explicitly.
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'super_admin' AND r.is_system = true
      AND p.product_id = commission_product_id
    ON CONFLICT DO NOTHING;

    -- Tenant owner gets all commission permissions except platform-scoped ones
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_owner'
      AND p.product_id = commission_product_id
      AND p.scope IN ('tenant', 'self');

    -- Tenant admin: same but no payout execution
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_admin'
      AND p.product_id = commission_product_id
      AND p.scope = 'tenant'
      AND p.permission_key NOT IN ('commission.payouts.execute', 'commission.broker.blacklist');

    -- Tenant staff: only read + invite + raise disputes
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_staff'
      AND p.permission_key IN (
        'commission.broker.read',
        'commission.broker.invite',
        'commission.attribution.read',
        'commission.payouts.read',
        'commission.dispute.raise'
      );

    -- New role: broker (self-service). Use NOT EXISTS pattern since (role_key, tenant_id)
    -- is the unique constraint and tenant_id is NULL for system roles.
    INSERT INTO platform.roles (role_key, name, description, product_id, scope, is_system)
    SELECT 'broker', 'Broker', 'Referral partner with self-service portal access', commission_product_id, 'tenant', true
    WHERE NOT EXISTS (
        SELECT 1 FROM platform.roles
        WHERE role_key = 'broker' AND tenant_id IS NULL AND is_system = true
    );

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'broker'
      AND p.permission_key IN (
        'commission.broker.read_self',
        'commission.broker.update_self',
        'commission.broker.generate_link_self'
      );
END $perms$;

-- ============================================================================
-- REGISTER ENCRYPTED FIELDS (PAN is sensitive PII)
-- ============================================================================
INSERT INTO platform.encrypted_fields_registry
    (schema_name, table_name, column_name, data_class, pii_category, legal_basis)
VALUES
    ('commission', 'brokers', 'pan_number', 'tax_id', 'financial', 'legal_obligation'),
    ('commission', 'brokers', 'bank_account_last_4', 'banking', 'financial', 'legal_obligation')
ON CONFLICT DO NOTHING;

-- ============================================================================
-- ROW-LEVEL SECURITY (tenant isolation on the money tables)
-- ============================================================================
-- The tenant-scoped commission tables carry tenant_id and hold financial data; isolate them with the same
-- policy as bookings/PHI (file 05). brokers + broker_wallets are GLOBAL identities (no tenant_id — a broker
-- and their wallet span tenants, like docslot.patients), so they are not RLS-gated here. The cross-tenant
-- settlement worker uses the SECURITY DEFINER fn below (no per-request tenant context). Enqueue/reads run
-- with a tenant context (UnitOfWork / TenantScopeQuery set app.tenant_id), satisfying the policy directly.
ALTER TABLE commission.attributions         ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.payouts              ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.commission_rules     ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.attribution_disputes ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.broker_campaigns     ENABLE ROW LEVEL SECURITY;
ALTER TABLE commission.broker_tenant_links  ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_attributions ON commission.attributions
    FOR ALL USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant());
CREATE POLICY tenant_isolation_payouts ON commission.payouts
    FOR ALL USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant());
CREATE POLICY tenant_isolation_commission_rules ON commission.commission_rules
    FOR ALL USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant());
CREATE POLICY tenant_isolation_attribution_disputes ON commission.attribution_disputes
    FOR ALL USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant());
CREATE POLICY tenant_isolation_broker_campaigns ON commission.broker_campaigns
    FOR ALL USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant());
CREATE POLICY tenant_isolation_broker_tenant_links ON commission.broker_tenant_links
    FOR ALL USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant());

-- ============================================================================
-- SETTLEMENT FUNCTION (earned → ready_to_pay) — SECURITY DEFINER, cross-tenant
-- ============================================================================
-- The settlement-window job has no per-request tenant context, so a plain app-role UPDATE would match zero
-- rows under the attributions RLS. This definer fn flips 'earned' attributions whose earned_at is older than
-- p_window to 'ready_to_pay' (so refunds within the window can still reverse before payout) and moves the
-- broker wallet earned→ready_to_pay in the same statement. Returns the number of attributions settled.
CREATE OR REPLACE FUNCTION commission.settle_earned_attributions(p_window INTERVAL DEFAULT INTERVAL '24 hours')
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = commission, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    WITH settled AS (
        UPDATE commission.attributions
        SET commission_status = 'ready_to_pay'
        WHERE commission_status = 'earned'
          AND earned_at IS NOT NULL
          AND earned_at < NOW() - p_window
        RETURNING broker_id, commission_amount_inr
    ),
    agg AS (
        SELECT broker_id, COALESCE(SUM(commission_amount_inr), 0) AS amt
        FROM settled GROUP BY broker_id
    ),
    moved AS (
        UPDATE commission.broker_wallets w
        SET earned_inr = GREATEST(0, w.earned_inr - agg.amt),
            ready_to_pay_inr = w.ready_to_pay_inr + agg.amt,
            updated_at = NOW()
        FROM agg WHERE w.broker_id = agg.broker_id
        RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_n FROM settled;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION commission.settle_earned_attributions IS
    'Settle earned attributions past the window to ready_to_pay + move broker wallet earned→ready_to_pay. SECURITY DEFINER for the RLS-less settlement worker. Returns attributions settled.';

GRANT EXECUTE ON FUNCTION commission.settle_earned_attributions(INTERVAL) TO docslot_app;

-- ============================================================================
-- POST-HOC ATTRIBUTION CLAIM OTPs (Phase 2 — organic attribution path: post-hoc claim)
-- ============================================================================
-- A broker can claim a (usually already-completed) booking — "I referred this patient" — AFTER the fact. The
-- claim mints a 'post_hoc_claim' attribution in verification_status='pending' (CreateAttributionCommand) and a
-- one-time code is sent to the PATIENT's WhatsApp number. The patient's reply confirms (→ patient_confirmed →
-- the attribution earns) or declines (→ patient_denied → reversed). Unanswered claims lapse to 'no_response'
-- and are reversed by the sweep below. This table is SEPARATE from docslot.booking_consent_otps so a behalf-
-- consent OTP and a claim OTP on the same patient phone never collide on the one-live-OTP-per-(tenant,phone)
-- scope. The code is NEVER stored in plaintext — only a per-row salted SHA-256 digest; verification is
-- attempt-limited; tenant_id + RLS keep one tenant's pending claims invisible to another.
CREATE TABLE commission.attribution_claim_otps (
    claim_otp_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    attribution_id      UUID NOT NULL REFERENCES commission.attributions(attribution_id) ON DELETE CASCADE,
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id) ON DELETE CASCADE,
    broker_id           UUID NOT NULL REFERENCES commission.brokers(broker_id) ON DELETE CASCADE,
    patient_phone       VARCHAR(15) NOT NULL,                  -- the number that must confirm the referral
    broker_phone        VARCHAR(15),                           -- the claiming broker's number (for the message)
    claimed_relation    VARCHAR(20),                           -- optional broker-stated context
    code_salt           TEXT NOT NULL,                         -- per-row random salt (base64)
    code_hash           TEXT NOT NULL,                         -- base64( sha256(salt || code) ) — never plaintext
    status              VARCHAR(15) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'confirmed', 'denied', 'expired', 'failed')),
    attempts            SMALLINT NOT NULL DEFAULT 0,
    max_attempts        SMALLINT NOT NULL DEFAULT 5,
    expires_at          TIMESTAMPTZ NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    verified_at         TIMESTAMPTZ
);

-- One live (pending) claim per patient number per tenant — a newer claim supersedes the old (rare).
CREATE UNIQUE INDEX idx_claim_otps_one_pending ON commission.attribution_claim_otps(tenant_id, patient_phone)
    WHERE status = 'pending';
CREATE INDEX idx_claim_otps_attribution ON commission.attribution_claim_otps(attribution_id);
CREATE INDEX idx_claim_otps_expiry ON commission.attribution_claim_otps(expires_at) WHERE status = 'pending';

-- RLS: tenant isolation, mirroring the commission money tables + booking_consent_otps. The patient's reply is
-- processed in a tenant-scoped UoW (app.tenant_id from the webhook's phone_number_id → tenant map), so a
-- pending claim is only ever visible to its own tenant. No super_admin god-flag bypass (patient phone is PHI).
ALTER TABLE commission.attribution_claim_otps ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_attribution_claim_otps ON commission.attribution_claim_otps
    FOR ALL USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant());

COMMENT ON TABLE commission.attribution_claim_otps IS 'Post-hoc broker-attribution claim OTPs. Salted-hash codes, attempt-limited, tenant-isolated by RLS. Separate from booking_consent_otps to avoid one-live-OTP collisions. Unanswered claims lapse to no_response and reverse the attribution.';

-- Sweep: lapse pending claims past expiry → mark the OTP 'expired', set the attribution verification to
-- 'no_response', reverse it (commission_status='reversed'), and debit the broker wallet from the bucket the
-- attribution was sitting in (+ lifetime_reversed) — mirroring IBrokerWalletRepository.ApplyReversedAsync so
-- the no-response path and the synchronous patient-deny path produce identical wallet effects. Closes the
-- phantom-pending_inr gap (a no-response claim must not leave commission credited). SECURITY DEFINER: the
-- maintenance worker is cross-tenant with no app.tenant_id, so it cannot satisfy RLS. Returns claims lapsed.
CREATE OR REPLACE FUNCTION commission.expire_stale_attribution_claims()
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = commission, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    WITH expired_claims AS (
        UPDATE commission.attribution_claim_otps c
        SET status = 'expired', verified_at = NOW()
        WHERE c.status = 'pending' AND c.expires_at < NOW()
        RETURNING c.attribution_id
    ),
    -- Capture each attribution's CURRENT bucket BEFORE flipping it (so the wallet debit hits the right bucket).
    -- Filter out already-terminal rows so a concurrent deny/reverse can't be double-debited (idempotent).
    to_reverse AS (
        SELECT a.attribution_id, a.broker_id, COALESCE(a.commission_amount_inr, 0) AS amt, a.commission_status AS prev
        FROM commission.attributions a
        JOIN expired_claims e ON e.attribution_id = a.attribution_id
        WHERE a.commission_status IN ('pending', 'earned', 'ready_to_pay')   -- never reverse a 'paid' or already-'reversed' one
    ),
    reversed AS (
        UPDATE commission.attributions a
        SET verification_status = 'no_response', commission_status = 'reversed', updated_at = NOW()
        FROM to_reverse t WHERE a.attribution_id = t.attribution_id
        RETURNING t.broker_id, t.amt, t.prev
    ),
    -- One row per broker; conditional per-bucket sums so a broker with claims in >1 bucket still debits correctly.
    wallet_moves AS (
        SELECT broker_id,
               SUM(CASE WHEN prev = 'pending'      THEN amt ELSE 0 END) AS pending_amt,
               SUM(CASE WHEN prev = 'earned'       THEN amt ELSE 0 END) AS earned_amt,
               SUM(CASE WHEN prev = 'ready_to_pay' THEN amt ELSE 0 END) AS ready_amt,
               SUM(amt) AS total_amt
        FROM reversed GROUP BY broker_id
    ),
    debited AS (
        UPDATE commission.broker_wallets w
        SET pending_inr      = GREATEST(0, w.pending_inr - m.pending_amt),
            earned_inr       = GREATEST(0, w.earned_inr - m.earned_amt),
            ready_to_pay_inr = GREATEST(0, w.ready_to_pay_inr - m.ready_amt),
            lifetime_reversed_inr = w.lifetime_reversed_inr + m.total_amt,
            updated_at = NOW()
        FROM wallet_moves m WHERE w.broker_id = m.broker_id
        RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_n FROM reversed;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION commission.expire_stale_attribution_claims IS
    'Lapse post-hoc claim OTPs past expiry: OTP→expired, attribution verification→no_response + reversed, debit the broker wallet from its current bucket (+lifetime_reversed). SECURITY DEFINER for the RLS-less maintenance worker. Returns claims lapsed.';

GRANT EXECUTE ON FUNCTION commission.expire_stale_attribution_claims() TO docslot_app;

-- ============================================================================
-- END OF COMMISSION SCHEMA
-- ============================================================================
-- Tables: 10 (C1-C10)
-- Indexes: ~35
-- Views: 3
-- Permissions: 19 registered in platform.permissions
-- New role: 'broker' (self-service)
-- Encrypted fields: 2 (pan_number, bank_account_last_4)
-- Compliance enforced via CHECK constraints:
--   - PCPNDT: brokers.can_refer_pndt = false (hard constraint)
--   - PCPNDT: commission_rules.excludes_pndt = true (hard constraint)
-- ============================================================================
