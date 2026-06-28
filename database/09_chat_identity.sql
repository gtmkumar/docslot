-- ============================================================================
-- DocSlot Platform Database — Part 9: Chat Identity & Direct-Booking Discount
-- ============================================================================
-- Implements the WhatsApp identity flow:
--   1. wa_contact_profiles — remember per-number identity so fresh chats ask
--      "who is this for?" ONCE and returning numbers get one-tap confirmation
--   2. Behalf-booking fields on bookings (relation, patient OTP consent)
--   3. Direct-booking discount (funded from the commission pool) with
--      DB-ENFORCED mutual exclusivity: a discounted booking can never also
--      carry a broker attribution (closes the double-dip loophole)
--   4. Hidden-broker detection view (numbers booking for many patients)
--
-- NAMING: customer-facing label for brokers is "Care Partner" (केयर पार्टनर).
-- Deliberately NOT "Referral Partner" — the word "referral" undermines the
-- MCI 6.4 marketing-fee positioning (see COMMISSION_SYSTEM.md).
--
-- DEPENDENCIES: run AFTER 03_docslot.sql and 07_commission_broker.sql
-- EXECUTION: psql -d docslot_platform -f 09_chat_identity.sql
-- ============================================================================

-- ============================================================================
-- TABLE I1: WA_CONTACT_PROFILES (per-number chat memory)
-- ============================================================================
-- One row per (tenant, WhatsApp number). Stores what the bot learned so it
-- never re-asks: default booking-for, last relation, linked patient/broker.
-- "Remember the default, confirm with one tap" — every booking still shows a
-- one-tap confirm because Indian numbers are shared/recycled.
CREATE TABLE docslot.wa_contact_profiles (
    profile_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    phone               VARCHAR(15) NOT NULL,                  -- the WhatsApp number chatting with us

    -- Learned identity
    display_name        VARCHAR(200),                          -- WhatsApp profile name or stated name
    default_booking_for VARCHAR(10) NOT NULL DEFAULT 'self'
        CHECK (default_booking_for IN ('self', 'behalf')),
    last_relation       VARCHAR(20)
        CHECK (last_relation IS NULL OR last_relation IN ('family', 'friend', 'neighbour', 'care_partner', 'other')),
    linked_patient_id   UUID REFERENCES docslot.patients(patient_id),
                                                               -- when booking_for=self and patient record exists
    linked_broker_id    UUID REFERENCES commission.brokers(broker_id),
                                                               -- when this number belongs to a registered Care Partner

    -- Hidden-broker heuristic state
    distinct_patients_90d INT NOT NULL DEFAULT 0,              -- maintained by app job; drives the nudge
    partner_nudge_sent_at TIMESTAMPTZ,                         -- don't nag: one nudge per 30 days max
    partner_nudge_count INT NOT NULL DEFAULT 0,

    -- App-install nudges (don't spam)
    app_installed       BOOLEAN NOT NULL DEFAULT false,
    app_install_nudge_count INT NOT NULL DEFAULT 0,
    last_app_nudge_at   TIMESTAMPTZ,
    history_sync_consent BOOLEAN NOT NULL DEFAULT false,       -- DPDP: WhatsApp history → app timeline
    history_sync_consent_at TIMESTAMPTZ,

    -- Chat preferences
    preferred_language  VARCHAR(5) NOT NULL DEFAULT 'en',      -- 'en', 'hi'
    last_seen_at        TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, phone)
);

CREATE INDEX idx_wa_profiles_phone ON docslot.wa_contact_profiles(phone);
CREATE INDEX idx_wa_profiles_broker ON docslot.wa_contact_profiles(linked_broker_id) WHERE linked_broker_id IS NOT NULL;
CREATE INDEX idx_wa_profiles_partner_candidates ON docslot.wa_contact_profiles(tenant_id, distinct_patients_90d DESC)
    WHERE linked_broker_id IS NULL AND distinct_patients_90d >= 3;

CREATE TRIGGER trg_wa_profiles_updated_at BEFORE UPDATE ON docslot.wa_contact_profiles
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

COMMENT ON TABLE docslot.wa_contact_profiles IS 'Per-number chat memory: ask who-is-this-for once, one-tap confirm thereafter. Tracks hidden-broker heuristics and app-install nudges.';

-- ============================================================================
-- ALTER: BOOKINGS — behalf-booking identity + direct discount
-- ============================================================================
ALTER TABLE docslot.bookings
    ADD COLUMN IF NOT EXISTS booked_by_type VARCHAR(10) NOT NULL DEFAULT 'self'
        CHECK (booked_by_type IN ('self', 'behalf')),
    ADD COLUMN IF NOT EXISTS behalf_relation VARCHAR(20)
        CHECK (behalf_relation IS NULL OR behalf_relation IN ('family', 'friend', 'neighbour', 'care_partner', 'other')),
    ADD COLUMN IF NOT EXISTS behalf_booker_phone VARCHAR(15),  -- who actually typed the booking
    ADD COLUMN IF NOT EXISTS patient_consent_status VARCHAR(15) NOT NULL DEFAULT 'not_required'
        CHECK (patient_consent_status IN ('not_required', 'pending', 'confirmed', 'denied', 'expired')),
    ADD COLUMN IF NOT EXISTS patient_consent_at TIMESTAMPTZ,
    -- Direct-booking discount (funded from the commission pool the facility set)
    ADD COLUMN IF NOT EXISTS direct_discount_inr DECIMAL(10,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS direct_discount_rule_id UUID REFERENCES commission.commission_rules(rule_id);

-- Behalf bookings must carry a relation; self bookings must not
ALTER TABLE docslot.bookings
    ADD CONSTRAINT chk_behalf_relation CHECK (
        (booked_by_type = 'self' AND behalf_relation IS NULL)
        OR (booked_by_type = 'behalf' AND behalf_relation IS NOT NULL)
    );

COMMENT ON COLUMN docslot.bookings.patient_consent_status IS 'Behalf bookings require patient WhatsApp OTP consent (DPDP + fake-patient loophole closure). Self bookings: not_required.';
COMMENT ON COLUMN docslot.bookings.direct_discount_inr IS 'Direct-patient discount funded from commission pool. Mutually exclusive with broker attribution (enforced by trigger).';

-- ============================================================================
-- TABLE I2: BOOKING_CONSENT_OTPS (behalf-booking patient OTP consent)
-- ============================================================================
-- DPDP fake-patient loophole closure: when a number books FOR SOMEONE ELSE we do
-- not silently create a patient record. We create the booking in 'pending' with
-- patient_consent_status='pending' and send a one-time code to the PATIENT's
-- WhatsApp number naming the booker + claimed relation. The patient replies with
-- the code to grant consent (status→confirmed) or declines (denied); unanswered
-- codes expire (sweeper cancels the booking + frees the slot).
--
-- The code is NEVER stored in plaintext — only a per-row salted SHA-256 digest.
-- Verification is attempt-limited. tenant_id + RLS keep one tenant's pending
-- consents invisible to another (the patient's reply is tenant-scoped via the
-- phone_number_id → tenant resolution at the webhook edge).
CREATE TABLE docslot.booking_consent_otps (
    consent_otp_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id) ON DELETE CASCADE,
    patient_phone       VARCHAR(15) NOT NULL,                  -- the number that must consent
    booker_phone        VARCHAR(15) NOT NULL,                  -- the number that typed the booking
    relation            VARCHAR(20) NOT NULL
        CHECK (relation IN ('family', 'friend', 'neighbour', 'care_partner', 'other')),
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

-- One live (pending) consent per patient number per tenant — a new behalf booking
-- for a number with a pending consent supersedes the old (the sweeper expires it).
CREATE INDEX idx_consent_otps_pending ON docslot.booking_consent_otps(tenant_id, patient_phone)
    WHERE status = 'pending';
CREATE INDEX idx_consent_otps_booking ON docslot.booking_consent_otps(booking_id);
CREATE INDEX idx_consent_otps_expiry ON docslot.booking_consent_otps(expires_at) WHERE status = 'pending';

-- RLS: tenant isolation, mirroring the booking-table policies (file 05). The
-- patient's OTP reply is processed in a tenant-scoped UoW (app.tenant_id set from
-- the webhook's phone_number_id → tenant map), so a pending consent is only ever
-- visible to its own tenant. No god-flag bypass (super_admin is not honored here).
ALTER TABLE docslot.booking_consent_otps ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_booking_consent_otps ON docslot.booking_consent_otps
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

COMMENT ON TABLE docslot.booking_consent_otps IS 'Behalf-booking patient consent OTPs (DPDP). Salted-hash codes, attempt-limited, tenant-isolated by RLS. Unanswered codes expire and cancel the awaiting booking.';

-- ============================================================================
-- ALTER: COMMISSION_RULES — the two new knobs
-- ============================================================================
ALTER TABLE commission.commission_rules
    ADD COLUMN IF NOT EXISTS direct_discount_pct DECIMAL(5,2) NOT NULL DEFAULT 50.00
        CHECK (direct_discount_pct >= 0 AND direct_discount_pct <= 100),
    ADD COLUMN IF NOT EXISTS first_booking_only BOOLEAN NOT NULL DEFAULT false;

COMMENT ON COLUMN commission.commission_rules.direct_discount_pct IS 'Direct patients get this % of the would-be commission as a discount (default 50). Facility saves the rest.';
COMMENT ON COLUMN commission.commission_rules.first_booking_only IS 'If true, commission paid only on a patient''s FIRST booking — repeat visits shift to the direct+discount channel.';

-- ============================================================================
-- TRIGGER: discount and attribution are MUTUALLY EXCLUSIVE (anti double-dip)
-- ============================================================================
-- Closes loophole #2: broker coaches patient to take the direct discount, then
-- files a post-hoc attribution claim. Accepting the discount IS the patient's
-- declaration that no one referred them, so any later attribution is rejected
-- at the database level — application bugs cannot bypass this.
CREATE OR REPLACE FUNCTION commission.fn_block_attribution_on_discounted()
RETURNS TRIGGER AS $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM docslot.bookings b
        WHERE b.booking_id = NEW.booking_id
          AND b.direct_discount_inr > 0
    ) THEN
        RAISE EXCEPTION 'Booking % carries a direct-booking discount; broker attribution is not allowed (mutual exclusivity).', NEW.booking_id
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_no_attribution_on_discounted
    BEFORE INSERT ON commission.attributions
    FOR EACH ROW EXECUTE FUNCTION commission.fn_block_attribution_on_discounted();

COMMENT ON TRIGGER trg_no_attribution_on_discounted ON commission.attributions IS 'Anti-double-dip: discounted direct bookings can never also pay a broker commission.';

-- ============================================================================
-- VIEW: suspected hidden Care Partners (the conversion funnel)
-- ============================================================================
-- Numbers that book for many distinct patients but are not registered brokers.
-- The app job sends them the "become a Care Partner" nudge — carrot, not stick.
CREATE OR REPLACE VIEW docslot.v_suspected_care_partners AS
SELECT
    wcp.tenant_id,
    wcp.phone,
    wcp.display_name,
    wcp.distinct_patients_90d,
    wcp.partner_nudge_count,
    wcp.partner_nudge_sent_at,
    (wcp.partner_nudge_sent_at IS NULL
     OR wcp.partner_nudge_sent_at < NOW() - INTERVAL '30 days') AS nudge_eligible
FROM docslot.wa_contact_profiles wcp
WHERE wcp.linked_broker_id IS NULL
  AND wcp.distinct_patients_90d >= 3
ORDER BY wcp.distinct_patients_90d DESC;

COMMENT ON VIEW docslot.v_suspected_care_partners IS 'Hidden-broker detection: high behalf-booking volume without registration. Targets for the Care Partner conversion nudge.';

-- ============================================================================
-- END OF CHAT IDENTITY SCHEMA
-- ============================================================================
-- New tables: 2 (docslot.wa_contact_profiles, docslot.booking_consent_otps)  → platform total: 114 tables
-- Altered: docslot.bookings (+7 cols), commission.commission_rules (+2 cols)
-- Trigger: anti-double-dip (attribution blocked on discounted bookings)
-- View: v_suspected_care_partners (hidden-broker conversion funnel)
--
-- RUNTIME LOGIC (application layer, documented in COMMISSION_SYSTEM.md):
--   - Fresh number → "who is this for?" → relation picker → Care Partner path
--   - Known number → one-tap confirm defaulting to last choice
--   - Behalf booking → patient OTP consent message (shows claimed relation)
--   - Direct booking with no attribution → apply direct_discount_pct of the
--     matched rule's commission as patient discount
--   - Nightly job maintains distinct_patients_90d and sends nudges
-- ============================================================================
