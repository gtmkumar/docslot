-- ============================================================================
-- DocSlot Platform Database — Part 5: Security Hardening
-- ============================================================================
-- This adds the security layer for medical-grade data protection.
-- Medical data = highest sensitivity tier under DPDP Act + HIPAA-equivalent rules.
--
-- WHY THIS IS A SEPARATE FILE:
-- The base platform (01-04) gives you working RBAC and audit. This file adds
-- the defense-in-depth layers needed for production deployment with real
-- patient data: encryption metadata, key rotation, anomaly detection,
-- tamper-proof audit chains, row-level security, and compliance scaffolding.
--
-- DPDP ACT 2023 REQUIREMENTS COVERED:
-- - Section 5: Data minimization (PII masking helpers)
-- - Section 6: Consent management (extended from 03_docslot)
-- - Section 8(4): Purpose limitation (access purpose tracking)
-- - Section 8(5): Security safeguards (encryption keys, MFA enforcement)
-- - Section 8(6): Breach reporting (extended from 01_platform_core)
-- - Section 8(7): Accountability (tamper-proof audit chains)
-- - Section 11: Right to portability (data_export_requests)
-- - Section 12: Right to erasure (cryptographic deletion certificates)
--
-- EXECUTION:
--   psql -d docslot_platform -f 05_security_hardening.sql
--
-- This runs AFTER 01-04. It adds tables, RLS policies, and audit triggers
-- on top of the existing schema.
-- ============================================================================

-- ============================================================================
-- LAYER 1: KEY MANAGEMENT & ENCRYPTION METADATA
-- ============================================================================

-- ----------------------------------------------------------------------------
-- S1. ENCRYPTION_KEYS (registry of encryption keys per tenant per data class)
-- ----------------------------------------------------------------------------
-- DocSlot encrypts sensitive fields (medical_history, prescriptions, ABHA IDs,
-- WhatsApp tokens) at the application layer before INSERT. The actual key
-- material lives in a KMS (Google Cloud KMS, Azure Key Vault, AWS KMS) —
-- this table stores only key references, rotation history, and metadata.
--
-- Field-level encryption pattern:
--   1. App fetches active key from KMS by key_reference
--   2. App encrypts plaintext, stores ciphertext + key_id in target column
--   3. App stores encrypted_data with envelope: {key_id, iv, ciphertext, tag}
--   4. On read: app fetches key by key_id, decrypts envelope
-- ----------------------------------------------------------------------------
CREATE TABLE platform.encryption_keys (
    key_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform-wide key
    data_class          VARCHAR(50) NOT NULL,                     -- 'medical_history', 'prescriptions', 'abha_ids', 'whatsapp_tokens', 'payment_info'
    key_reference       VARCHAR(500) NOT NULL,                    -- KMS key path: 'projects/X/locations/Y/keyRings/Z/cryptoKeys/W'
    kms_provider        VARCHAR(30) NOT NULL CHECK (kms_provider IN ('gcp_kms', 'azure_key_vault', 'aws_kms', 'hashicorp_vault', 'local_dev')),
    key_algorithm       VARCHAR(30) NOT NULL DEFAULT 'AES_256_GCM',
    key_version         INT NOT NULL DEFAULT 1,

    -- Rotation
    activated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    rotated_at          TIMESTAMPTZ,                              -- Last rotation
    next_rotation_due_at TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '90 days'),
    deactivated_at      TIMESTAMPTZ,                              -- Soft retire (still usable for decrypt)
    destroyed_at        TIMESTAMPTZ,                              -- Cryptographic erasure marker

    -- Status
    status              VARCHAR(20) NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'rotating', 'deactivated', 'destroyed')),

    -- Audit
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata            JSONB NOT NULL DEFAULT '{}',

    UNIQUE(tenant_id, data_class, key_version)
);

CREATE INDEX idx_keys_active ON platform.encryption_keys(tenant_id, data_class) WHERE status = 'active';
CREATE INDEX idx_keys_rotation_due ON platform.encryption_keys(next_rotation_due_at) WHERE status = 'active';

COMMENT ON TABLE platform.encryption_keys IS 'KMS key reference registry. Actual key material NEVER stored here.';

-- ----------------------------------------------------------------------------
-- S2. KEY_USAGE_LOG (every encrypt/decrypt operation logged)
-- ----------------------------------------------------------------------------
-- For forensics and key compromise detection. If a key is leaked, this log
-- tells you exactly which records were encrypted with it (re-encrypt needed)
-- and which decrypt operations happened during the compromise window.
CREATE TABLE platform.key_usage_log (
    usage_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    key_id              UUID NOT NULL REFERENCES platform.encryption_keys(key_id),
    operation           VARCHAR(20) NOT NULL CHECK (operation IN ('encrypt', 'decrypt', 'rotate', 'destroy')),
    user_id             UUID REFERENCES platform.users(user_id),
    api_client_id       UUID REFERENCES platform_api.api_clients(client_id),
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),
    resource_type       VARCHAR(50),                              -- 'prescription', 'medical_history'
    resource_id         UUID,
    ip_address          INET,
    success             BOOLEAN NOT NULL DEFAULT true,
    error_message       TEXT,
    occurred_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_key_usage_key ON platform.key_usage_log(key_id, occurred_at DESC);
CREATE INDEX idx_key_usage_failures ON platform.key_usage_log(occurred_at DESC) WHERE success = false;

-- Partition by month for performance (large volume table)
COMMENT ON TABLE platform.key_usage_log IS 'Partition by month in production; retain 7 years for medical compliance.';

-- ----------------------------------------------------------------------------
-- S3. ENCRYPTED_FIELDS_REGISTRY (which columns are encrypted)
-- ----------------------------------------------------------------------------
-- Documents the encrypted-field schema so application code knows what to
-- encrypt/decrypt without hardcoding it. Acts as a contract between DB and app.
CREATE TABLE platform.encrypted_fields_registry (
    registry_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    schema_name         VARCHAR(50) NOT NULL,                     -- 'docslot'
    table_name          VARCHAR(50) NOT NULL,                     -- 'patient_medical_history'
    column_name         VARCHAR(50) NOT NULL,                     -- 'description'
    data_class          VARCHAR(50) NOT NULL,                     -- Maps to encryption_keys.data_class
    encryption_required BOOLEAN NOT NULL DEFAULT true,
    is_searchable       BOOLEAN NOT NULL DEFAULT false,           -- If true, use deterministic encryption + searchable index
    pii_category        VARCHAR(50)                                -- 'medical', 'financial', 'identity', 'contact'
        CHECK (pii_category IS NULL OR pii_category IN ('medical', 'financial', 'identity', 'contact', 'biometric')),
    legal_basis         VARCHAR(50)                                -- DPDP basis: 'consent', 'contract', 'legal_obligation'
        CHECK (legal_basis IS NULL OR legal_basis IN ('consent', 'contract', 'legal_obligation', 'vital_interest', 'public_interest')),
    retention_days      INT,                                       -- After deletion request
    notes               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(schema_name, table_name, column_name)
);

-- Seed registry with known sensitive fields
INSERT INTO platform.encrypted_fields_registry (schema_name, table_name, column_name, data_class, pii_category, legal_basis) VALUES
('docslot', 'patient_medical_history', 'description', 'medical_history', 'medical', 'consent'),
('docslot', 'patient_medical_history', 'title', 'medical_history', 'medical', 'consent'),
('docslot', 'prescriptions', 'diagnosis', 'medical_history', 'medical', 'consent'),
('docslot', 'prescriptions', 'medications', 'medical_history', 'medical', 'consent'),
('docslot', 'prescriptions', 'examination', 'medical_history', 'medical', 'consent'),
('docslot', 'prescriptions', 'chief_complaints', 'medical_history', 'medical', 'consent'),
('docslot', 'lab_reports', 'structured_results', 'medical_history', 'medical', 'consent'),
('docslot', 'abdm_health_records', 'fhir_bundle', 'medical_history', 'medical', 'consent'),
('docslot', 'patients', 'aadhaar_last_4', 'aadhaar_partial', 'identity', 'consent'),
('docslot', 'healthcare_facilities', 'whatsapp_access_token', 'whatsapp_tokens', 'contact', 'contract'),
('platform', 'users', 'mfa_secret', 'mfa_secrets', 'identity', 'contract'),
-- Webhook HMAC signing secret: AES-encrypted at rest (must be recoverable to sign each delivery),
-- registered here so the encryption contract is documented (slice 02 finding).
('platform_api', 'webhook_subscriptions', 'secret_hash', 'webhook_signing_secrets', 'contact', 'contract');

-- ============================================================================
-- LAYER 2: TAMPER-PROOF AUDIT CHAIN
-- ============================================================================

-- ----------------------------------------------------------------------------
-- S4. AUDIT_CHAIN (cryptographic hash chain on top of audit_log)
-- ----------------------------------------------------------------------------
-- Standard audit_log can be tampered with by a DB admin. For medical-grade
-- compliance, we layer a hash chain on top: each row's hash includes the
-- previous row's hash, so any tampering breaks the chain.
--
-- Daily verification job: re-computes hashes and alerts if any link broken.
-- Anchoring strategy: every 24 hours, hash the latest chain head and publish
-- to an external store (e.g. a transparency log, blockchain, or notary service).
-- ----------------------------------------------------------------------------
CREATE TABLE platform.audit_chain (
    chain_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    audit_id            UUID NOT NULL UNIQUE REFERENCES platform.audit_log(audit_id),
    sequence_number     BIGSERIAL NOT NULL UNIQUE,
    previous_hash       VARCHAR(64) NOT NULL,                     -- SHA-256 of previous row, or zeros for genesis
    row_hash            VARCHAR(64) NOT NULL,                     -- SHA-256 of (audit_id || data || previous_hash)
    chained_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_chain_seq ON platform.audit_chain(sequence_number);

-- Function: compute hash for a new audit_log row and chain it
CREATE OR REPLACE FUNCTION platform.append_to_audit_chain(p_audit_id UUID)
RETURNS VOID AS $$
DECLARE
    v_previous_hash VARCHAR(64);
    v_row_content TEXT;
    v_new_hash VARCHAR(64);
BEGIN
    -- CONCURRENCY SAFETY (slice 03b): serialize all appenders on a single fixed
    -- advisory lock held until the end of THIS transaction. Without it, two
    -- concurrent audit_log INSERTs both read the same chain head and fork the chain
    -- (duplicate previous_hash → verify_audit_chain() reports a break). The lock makes
    -- read-head → compute → insert atomic across sessions. Key is an arbitrary fixed
    -- constant unique to the audit chain.
    PERFORM pg_advisory_xact_lock(8675309001);

    -- Fetch the latest chain hash (or genesis zeros)
    SELECT row_hash INTO v_previous_hash
    FROM platform.audit_chain
    ORDER BY sequence_number DESC
    LIMIT 1;

    IF v_previous_hash IS NULL THEN
        v_previous_hash := '0000000000000000000000000000000000000000000000000000000000000000';
    END IF;

    -- Build deterministic content string from the audit_log row
    SELECT
        audit_id::TEXT || '|' ||
        COALESCE(user_id::TEXT, '') || '|' ||
        COALESCE(tenant_id::TEXT, '') || '|' ||
        action || '|' ||
        resource_type || '|' ||
        COALESCE(resource_id::TEXT, '') || '|' ||
        COALESCE(before_data::TEXT, '') || '|' ||
        COALESCE(after_data::TEXT, '') || '|' ||
        occurred_at::TEXT
    INTO v_row_content
    FROM platform.audit_log
    WHERE audit_id = p_audit_id;

    -- Hash = SHA-256(row_content || previous_hash)
    v_new_hash := encode(digest(v_row_content || v_previous_hash, 'sha256'), 'hex');

    INSERT INTO platform.audit_chain (audit_id, previous_hash, row_hash)
    VALUES (p_audit_id, v_previous_hash, v_new_hash);
END;
$$ LANGUAGE plpgsql;

-- Trigger: auto-chain every new audit_log row
CREATE OR REPLACE FUNCTION platform.trigger_audit_chain()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM platform.append_to_audit_chain(NEW.audit_id);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_audit_chain AFTER INSERT ON platform.audit_log
    FOR EACH ROW EXECUTE FUNCTION platform.trigger_audit_chain();

-- Function: verify chain integrity (run daily)
CREATE OR REPLACE FUNCTION platform.verify_audit_chain(
    p_from_seq BIGINT DEFAULT 1,
    p_to_seq BIGINT DEFAULT NULL
) RETURNS TABLE(broken_at_sequence BIGINT, audit_id UUID, expected_hash VARCHAR, actual_hash VARCHAR) AS $$
DECLARE
    v_row RECORD;
    v_previous_hash VARCHAR(64) := '0000000000000000000000000000000000000000000000000000000000000000';
    v_expected_hash VARCHAR(64);
    v_row_content TEXT;
BEGIN
    FOR v_row IN
        SELECT ac.sequence_number, ac.audit_id, ac.previous_hash, ac.row_hash, al.*
        FROM platform.audit_chain ac
        JOIN platform.audit_log al ON al.audit_id = ac.audit_id
        WHERE ac.sequence_number >= p_from_seq
          AND (p_to_seq IS NULL OR ac.sequence_number <= p_to_seq)
        ORDER BY ac.sequence_number ASC
    LOOP
        v_row_content :=
            v_row.audit_id::TEXT || '|' ||
            COALESCE(v_row.user_id::TEXT, '') || '|' ||
            COALESCE(v_row.tenant_id::TEXT, '') || '|' ||
            v_row.action || '|' ||
            v_row.resource_type || '|' ||
            COALESCE(v_row.resource_id::TEXT, '') || '|' ||
            COALESCE(v_row.before_data::TEXT, '') || '|' ||
            COALESCE(v_row.after_data::TEXT, '') || '|' ||
            v_row.occurred_at::TEXT;

        v_expected_hash := encode(digest(v_row_content || v_previous_hash, 'sha256'), 'hex');

        IF v_expected_hash != v_row.row_hash THEN
            broken_at_sequence := v_row.sequence_number;
            audit_id := v_row.audit_id;
            expected_hash := v_expected_hash;
            actual_hash := v_row.row_hash;
            RETURN NEXT;
        END IF;

        v_previous_hash := v_row.row_hash;
    END LOOP;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION platform.verify_audit_chain IS 'Returns rows where hash chain is broken. Empty result = chain intact.';

-- ----------------------------------------------------------------------------
-- S5. AUDIT_ANCHORS (external anchoring for ultimate tamper-proofing)
-- ----------------------------------------------------------------------------
CREATE TABLE platform.audit_anchors (
    anchor_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    chain_head_sequence BIGINT NOT NULL,
    chain_head_hash     VARCHAR(64) NOT NULL,
    anchored_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    anchor_type         VARCHAR(30) NOT NULL CHECK (anchor_type IN ('transparency_log', 'notary_api', 'blockchain', 'paper_print', 'external_storage')),
    anchor_reference    VARCHAR(500) NOT NULL,                    -- URL, transaction ID, or storage path
    anchored_by_user_id UUID REFERENCES platform.users(user_id),
    metadata            JSONB DEFAULT '{}'
);

CREATE INDEX idx_anchors_recent ON platform.audit_anchors(anchored_at DESC);

-- ============================================================================
-- LAYER 3: ACCESS CONTROL ENHANCEMENTS
-- ============================================================================

-- ----------------------------------------------------------------------------
-- S6. ACCESS_POLICIES (column-level access rules)
-- ----------------------------------------------------------------------------
-- Beyond table-level RBAC, sensitive columns need column-level restrictions.
-- Example: a receptionist can see patient_name and phone but NOT medical_history.
-- This table defines those rules; application code enforces them.
CREATE TABLE platform.access_policies (
    policy_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    policy_name         VARCHAR(100) NOT NULL,
    schema_name         VARCHAR(50) NOT NULL,
    table_name          VARCHAR(50) NOT NULL,
    column_name         VARCHAR(50),                              -- NULL = applies to entire table
    required_permission VARCHAR(150) NOT NULL,                    -- Must hold this permission to access
    additional_conditions JSONB,                                  -- Extra rules: {require_mfa: true, require_purpose: true}
    mask_strategy       VARCHAR(50),                              -- For unauthorized: 'hide', 'mask', 'partial', 'hash'
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(schema_name, table_name, column_name, required_permission)
);

CREATE INDEX idx_policies_table ON platform.access_policies(schema_name, table_name) WHERE is_active = true;

-- Seed: high-sensitivity column policies
INSERT INTO platform.access_policies (policy_name, schema_name, table_name, column_name, required_permission, additional_conditions, mask_strategy) VALUES
('Medical history read', 'docslot', 'patient_medical_history', NULL, 'docslot.patient.read', '{"require_purpose": true}', 'hide'),
('Prescription diagnosis', 'docslot', 'prescriptions', 'diagnosis', 'docslot.patient.read', '{"require_purpose": true}', 'mask'),
('ABHA records access', 'docslot', 'abdm_health_records', NULL, 'docslot.abdm.records.read', '{"require_consent": true, "require_mfa": true}', 'hide'),
('Patient phone full', 'docslot', 'patients', 'phone_number', 'docslot.patient.read', '{}', 'partial'),
('Patient Aadhaar', 'docslot', 'patients', 'aadhaar_last_4', 'docslot.patient.read', '{"require_mfa": true}', 'mask'),
('WhatsApp tokens', 'docslot', 'healthcare_facilities', 'whatsapp_access_token', 'platform.settings.update', '{"require_mfa": true}', 'hide');

-- ----------------------------------------------------------------------------
-- S7. PURPOSE_OF_USE_LOG (DPDP Section 8(4) — purpose limitation)
-- ----------------------------------------------------------------------------
-- When a doctor opens a patient's record, they must declare WHY they're
-- accessing it. This creates accountability for medical record access.
-- Models the "break glass" pattern used in hospital EHRs.
CREATE TABLE platform.purpose_of_use_log (
    log_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES platform.users(user_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    session_id          UUID REFERENCES platform.user_sessions(session_id),
    accessed_resource_type VARCHAR(50) NOT NULL,                  -- 'patient', 'medical_history', 'prescription'
    accessed_resource_id UUID NOT NULL,
    declared_purpose    VARCHAR(50) NOT NULL CHECK (declared_purpose IN (
        'treatment',                                              -- Treating current encounter
        'follow_up',                                              -- Follow-up visit
        'emergency',                                              -- Break-glass emergency access
        'consultation',                                           -- Specialist consultation
        'research',                                               -- Research (requires extra approval)
        'audit',                                                  -- Internal audit
        'patient_request',                                        -- Patient asked for their own data
        'legal_obligation'                                        -- Court order, regulatory
    )),
    purpose_notes       TEXT,
    is_break_glass      BOOLEAN NOT NULL DEFAULT false,           -- Emergency override used?
    break_glass_reason  TEXT,
    accessed_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    review_required     BOOLEAN NOT NULL DEFAULT false,            -- Flagged for post-hoc review
    reviewed_at         TIMESTAMPTZ,
    reviewed_by_user_id UUID REFERENCES platform.users(user_id),
    review_outcome      VARCHAR(20) CHECK (review_outcome IS NULL OR review_outcome IN ('approved', 'rejected', 'further_action'))
);

CREATE INDEX idx_purpose_user ON platform.purpose_of_use_log(user_id, accessed_at DESC);
CREATE INDEX idx_purpose_break_glass ON platform.purpose_of_use_log(accessed_at DESC) WHERE is_break_glass = true;
CREATE INDEX idx_purpose_review_pending ON platform.purpose_of_use_log(accessed_at) WHERE review_required = true AND reviewed_at IS NULL;

-- ----------------------------------------------------------------------------
-- S8. IP_ALLOWLIST (per-user/per-tenant IP restrictions)
-- ----------------------------------------------------------------------------
CREATE TABLE platform.ip_allowlist (
    allowlist_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    user_id             UUID REFERENCES platform.users(user_id) ON DELETE CASCADE,
                                                                  -- NULL user = tenant-wide allowlist
    cidr_range          CIDR NOT NULL,
    label               VARCHAR(100),                             -- 'Office network', 'Doctor home VPN'
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ,
    CHECK (tenant_id IS NOT NULL OR user_id IS NOT NULL)
);

CREATE INDEX idx_ip_allowlist_tenant ON platform.ip_allowlist(tenant_id) WHERE is_active = true;
CREATE INDEX idx_ip_allowlist_user ON platform.ip_allowlist(user_id) WHERE is_active = true;

-- ----------------------------------------------------------------------------
-- S9. DEVICE_REGISTRY (known devices per user for anomaly detection)
-- ----------------------------------------------------------------------------
CREATE TABLE platform.user_devices (
    device_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES platform.users(user_id) ON DELETE CASCADE,
    device_fingerprint  VARCHAR(255) NOT NULL,                    -- Hashed combo of user-agent, screen, fonts, etc.
    device_label        VARCHAR(100),                             -- 'Goutam iPhone', 'Office laptop'
    last_ip_address     INET,
    last_user_agent     TEXT,
    last_seen_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    first_seen_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    trust_level         VARCHAR(20) NOT NULL DEFAULT 'new'
        CHECK (trust_level IN ('new', 'recognized', 'trusted', 'suspicious', 'blocked')),
    trusted_by_user_at  TIMESTAMPTZ,                              -- User confirmed "this is my device"
    UNIQUE(user_id, device_fingerprint)
);

CREATE INDEX idx_devices_user ON platform.user_devices(user_id, trust_level);

-- ----------------------------------------------------------------------------
-- S10. ANOMALY_DETECTION_EVENTS (suspicious activity flagged)
-- ----------------------------------------------------------------------------
CREATE TABLE platform.anomaly_events (
    anomaly_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID REFERENCES platform.users(user_id),
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),
    anomaly_type        VARCHAR(50) NOT NULL CHECK (anomaly_type IN (
        'login_from_new_country',
        'login_from_new_device',
        'impossible_travel',                                      -- Login from 2 countries within travel-impossible window
        'mass_data_access',                                       -- Reading 100+ patient records in short period
        'unusual_export',                                         -- Large data export
        'after_hours_access',                                     -- Access outside business hours
        'failed_login_burst',
        'permission_escalation_attempt',
        'consent_violation',                                      -- Accessing data without valid consent
        'rate_limit_exceeded',
        'sql_injection_pattern',
        'broken_audit_chain'
    )),
    severity            VARCHAR(20) NOT NULL CHECK (severity IN ('low', 'medium', 'high', 'critical')),
    detected_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    description         TEXT NOT NULL,
    evidence            JSONB NOT NULL DEFAULT '{}',
    ip_address          INET,
    device_id           UUID REFERENCES platform.user_devices(device_id),

    -- Response
    auto_action_taken   VARCHAR(50),                              -- 'session_revoked', 'mfa_challenge', 'account_locked', 'alert_only'
    requires_review     BOOLEAN NOT NULL DEFAULT true,
    reviewed_at         TIMESTAMPTZ,
    reviewed_by_user_id UUID REFERENCES platform.users(user_id),
    review_outcome      VARCHAR(30) CHECK (review_outcome IS NULL OR review_outcome IN ('false_positive', 'confirmed_threat', 'unclear', 'user_verified'))
);

CREATE INDEX idx_anomalies_unreviewed ON platform.anomaly_events(severity, detected_at DESC) WHERE requires_review = true AND reviewed_at IS NULL;
CREATE INDEX idx_anomalies_user ON platform.anomaly_events(user_id, detected_at DESC);

-- ============================================================================
-- LAYER 4: DATA SUBJECT RIGHTS (DPDP Sections 11-13)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- S11. DATA_EXPORT_REQUESTS (right to portability — Section 11)
-- ----------------------------------------------------------------------------
CREATE TABLE platform.data_export_requests (
    request_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Requester (the data principal)
    requester_user_id   UUID REFERENCES platform.users(user_id),
    requester_email     CITEXT,                                   -- For patient requests
    requester_phone     VARCHAR(15),

    -- Subject (whose data — usually same as requester)
    subject_phone       VARCHAR(15) NOT NULL,                     -- Patient phone

    -- Scope
    scope_product_keys  VARCHAR(50)[] NOT NULL DEFAULT ARRAY['docslot'],
    scope_data_classes  VARCHAR(50)[] DEFAULT ARRAY['medical_history', 'bookings', 'prescriptions', 'reports'],
    scope_date_from     TIMESTAMPTZ,
    scope_date_to       TIMESTAMPTZ,

    -- Format
    export_format       VARCHAR(20) NOT NULL DEFAULT 'fhir_r4_bundle' CHECK (export_format IN ('json', 'pdf', 'fhir_r4_bundle', 'csv_zip')),

    -- Verification (patient must prove identity)
    verification_method VARCHAR(30) CHECK (verification_method IS NULL OR verification_method IN ('aadhaar_otp', 'abha_oauth', 'manual_kyc', 'email_link', 'whatsapp_otp')),
    verified_at         TIMESTAMPTZ,

    -- Processing
    status              VARCHAR(20) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'verified', 'processing', 'ready', 'downloaded', 'expired', 'rejected')),
    processing_started_at TIMESTAMPTZ,
    processing_completed_at TIMESTAMPTZ,

    -- Delivery
    download_url        TEXT,                                     -- Signed URL, time-limited
    download_expires_at TIMESTAMPTZ,
    downloaded_at       TIMESTAMPTZ,
    download_ip         INET,

    -- Encryption (the export itself is encrypted with a key only the patient has)
    encryption_key_hint VARCHAR(200),                             -- Hint for patient to derive decryption key
    file_size_bytes     BIGINT,
    file_checksum       VARCHAR(64),

    rejection_reason    TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_export_pending ON platform.data_export_requests(status, created_at) WHERE status IN ('pending', 'verified', 'processing');

-- ----------------------------------------------------------------------------
-- S12. DELETION_CERTIFICATES (cryptographic proof of erasure — Section 12)
-- ----------------------------------------------------------------------------
-- When a patient requests deletion, we destroy the encryption key for their
-- data. The data becomes cryptographically unrecoverable even by us.
-- This table stores the certificate of that destruction.
CREATE TABLE platform.deletion_certificates (
    certificate_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    deletion_request_id UUID NOT NULL REFERENCES platform.data_deletion_requests(request_id),
    subject_phone       VARCHAR(15) NOT NULL,

    -- What was deleted
    deleted_record_counts JSONB NOT NULL,                         -- {"bookings": 12, "prescriptions": 5, ...}
    affected_tenant_ids UUID[],

    -- Cryptographic erasure
    destroyed_key_ids   UUID[] NOT NULL,                           -- References platform.encryption_keys
    destruction_method  VARCHAR(30) NOT NULL CHECK (destruction_method IN ('key_destruction', 'overwrite', 'physical_destruction', 'data_anonymization')),

    -- Verification
    pre_deletion_hash   VARCHAR(64),                              -- Hash of records before deletion
    post_deletion_hash  VARCHAR(64),                              -- Should differ — proves change happened
    verification_query  TEXT,                                      -- SQL query to verify deletion completeness

    -- Certification
    certified_by_user_id UUID NOT NULL REFERENCES platform.users(user_id),
    certified_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    certificate_pdf_url TEXT,                                      -- Signed PDF given to patient
    signature_algorithm VARCHAR(30) DEFAULT 'ECDSA_P256_SHA256',
    digital_signature   TEXT,                                      -- Platform's signature on the cert

    -- Compliance
    dpb_report_filed_at TIMESTAMPTZ,                              -- If required by DPB
    retained_for_compliance_until DATE,                            -- Some metadata must be retained for X years

    metadata            JSONB DEFAULT '{}'
);

CREATE INDEX idx_deletion_certs_subject ON platform.deletion_certificates(subject_phone);
CREATE INDEX idx_deletion_certs_date ON platform.deletion_certificates(certified_at DESC);

-- ----------------------------------------------------------------------------
-- S13. CONSENT_EVENT_LOG (every consent change, immutable)
-- ----------------------------------------------------------------------------
-- DPDP requires proof of consent. This is the immutable log of every
-- consent action: grant, modify, revoke. Feeds webhook_deliveries to
-- notify downstream consumers when patients revoke consent.
CREATE TABLE platform.consent_event_log (
    event_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    consent_id          UUID,                                      -- References docslot.abdm_consents or similar
    patient_phone       VARCHAR(15) NOT NULL,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),

    event_type          VARCHAR(30) NOT NULL CHECK (event_type IN (
        'consent_requested',
        'consent_granted',
        'consent_modified',
        'consent_revoked',
        'consent_expired',
        'consent_used',                                            -- Actually used to access data
        'consent_denied'
    )),

    -- Snapshot of consent at time of event (so we can prove what was consented to)
    consent_scope       JSONB NOT NULL,
    legal_basis         VARCHAR(50),
    legal_basis_details TEXT,

    -- Channel
    channel             VARCHAR(30) CHECK (channel IS NULL OR channel IN ('whatsapp', 'app', 'web', 'voice_call', 'paper_form', 'api')),
    channel_message_id  VARCHAR(100),                              -- WhatsApp message ID, etc.

    -- Actor
    actor_user_id       UUID REFERENCES platform.users(user_id),
    actor_api_client_id UUID REFERENCES platform_api.api_clients(client_id),
    ip_address          INET,
    user_agent          TEXT,

    occurred_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Webhook delivery to downstream consumers (CRITICAL for revocation propagation)
    downstream_notified_at TIMESTAMPTZ,
    downstream_notification_status VARCHAR(20)
);

CREATE INDEX idx_consent_events_patient ON platform.consent_event_log(patient_phone, occurred_at DESC);
CREATE INDEX idx_consent_events_pending_notify ON platform.consent_event_log(occurred_at) WHERE downstream_notified_at IS NULL AND event_type = 'consent_revoked';

-- ============================================================================
-- LAYER 5: ROW-LEVEL SECURITY (defense-in-depth at DB layer)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- RLS prevents accidental cross-tenant queries even if application code fails.
-- Application sets session variable: SET LOCAL app.tenant_id = '...';
-- Policies enforce that queries only see rows matching that tenant.
-- ----------------------------------------------------------------------------

-- Helper function: extract current tenant_id from session
CREATE OR REPLACE FUNCTION platform.current_tenant_id() RETURNS UUID AS $$
    SELECT NULLIF(current_setting('app.tenant_id', true), '')::UUID;
$$ LANGUAGE SQL STABLE;

-- Helper: is the current session a super_admin?
-- NULLIF guards the empty string: a SET LOCAL custom GUC reverts to '' (not unset)
-- on a pooled connection after the first transaction, and ''::BOOLEAN errors (22P02).
-- Mirrors current_tenant_id()'s NULLIF pattern above.
-- SCOPE: this global flag is honored ONLY by the RBAC/entitlement policies in
-- 11_rbac_hardening.sql (platform administration of roles/permissions/menus). It is
-- DELIBERATELY NOT honored by the PHI policies below — cross-tenant access to medical
-- data must go through a scoped, time-boxed, AUDITED impersonation session
-- (platform.begin_impersonation → app.impersonated_tenant), never a blanket god-flag.
-- See audit Finding 1/2 (PR #2): a raw super_admin context must not silently read PHI.
CREATE OR REPLACE FUNCTION platform.current_is_super_admin() RETURNS BOOLEAN AS $$
    SELECT COALESCE(NULLIF(current_setting('app.is_super_admin', true), '')::BOOLEAN, false);
$$ LANGUAGE SQL STABLE;

-- Helper: the tenant the current session is actively impersonating (R6), if any.
-- BOOTSTRAP DEFINITION ONLY. This pure GUC reader exists so the PHI policies below can
-- reference the function — but impersonation_sessions does not exist yet at this point
-- in the run order, so the audited-by-construction validation cannot live here.
-- 11_rbac_hardening.sql REDEFINES this function (CREATE OR REPLACE, SECURITY DEFINER) to
-- only honor app.impersonated_tenant when it is backed by a live, non-expired
-- impersonation_sessions row for app.user_id — see issue #3. 11 is REQUIRED for real
-- patient data (the standard bundle always runs it last). Even this bootstrap form is
-- fail-closed: with no GUC set, current_impersonated_tenant() is NULL ⇒ PHI stays
-- confined to the active tenant.
CREATE OR REPLACE FUNCTION platform.current_impersonated_tenant() RETURNS UUID AS $$
    SELECT NULLIF(current_setting('app.impersonated_tenant', true), '')::UUID;
$$ LANGUAGE SQL STABLE;

-- Enable RLS on the most sensitive tables (medical data)
ALTER TABLE docslot.patient_medical_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.prescriptions ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.lab_reports ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.abdm_health_records ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.drug_alerts ENABLE ROW LEVEL SECURITY;

-- PHI cross-tenant rule (audit Finding 1/2): a row is visible ONLY for the active
-- tenant OR the tenant of an ACTIVE scoped impersonation session (app.impersonated_tenant,
-- set after platform.begin_impersonation audits + time-boxes the access). The raw
-- platform.current_is_super_admin() god-flag is intentionally absent here — a super_admin
-- with no open impersonation session can read PHI in their OWN tenant only, exactly like
-- anyone else. No session ⇒ current_impersonated_tenant() is NULL ⇒ no cross-tenant row
-- ever matches (NULL = NULL is never true), so this fails closed.

-- Policy: tenant isolation on patient_medical_history
CREATE POLICY tenant_isolation_medical_history ON docslot.patient_medical_history
    FOR ALL
    USING (
        tenant_id = platform.current_tenant_id()
        OR tenant_id = platform.current_impersonated_tenant()
    );

-- Policy: tenant isolation on prescriptions
CREATE POLICY tenant_isolation_prescriptions ON docslot.prescriptions
    FOR ALL
    USING (
        tenant_id = platform.current_tenant_id()
        OR tenant_id = platform.current_impersonated_tenant()
    );

-- Policy: tenant isolation on lab_reports
CREATE POLICY tenant_isolation_reports ON docslot.lab_reports
    FOR ALL
    USING (
        tenant_id = platform.current_tenant_id()
        OR tenant_id = platform.current_impersonated_tenant()
    );

-- Policy: tenant isolation on abdm_health_records
CREATE POLICY tenant_isolation_abdm ON docslot.abdm_health_records
    FOR ALL
    USING (
        tenant_id = platform.current_tenant_id()
        OR tenant_id = platform.current_impersonated_tenant()
    );

-- Policy: drug_alerts (joined with prescriptions which has tenant_id)
CREATE POLICY tenant_isolation_drug_alerts ON docslot.drug_alerts
    FOR ALL
    USING (
        EXISTS (
            SELECT 1 FROM docslot.prescriptions p
            WHERE p.prescription_id = drug_alerts.prescription_id
              AND (p.tenant_id = platform.current_tenant_id()
                   OR p.tenant_id = platform.current_impersonated_tenant())
        )
    );

COMMENT ON POLICY tenant_isolation_medical_history ON docslot.patient_medical_history IS 'Defense-in-depth: blocks cross-tenant queries even if app code forgets WHERE tenant_id = ...';

-- ----------------------------------------------------------------------------
-- BOOKING DATA-PLANE RLS (Phase-0 gap fix) — tenant isolation on the operational
-- booking tables, mirroring the PHI pattern above (active tenant OR scoped
-- impersonation; the super_admin god-flag is intentionally NOT honored).
-- ----------------------------------------------------------------------------
-- App writes run in the per-request UnitOfWork transaction that SET LOCAL
-- app.tenant_id (authenticated requests from the JWT; the WhatsApp webhook via
-- ITenantScopeOverride). Slot materialization runs through the SECURITY DEFINER
-- docslot.generate_time_slots (bypasses RLS), so the nightly worker — which has no
-- request tenant context — is unaffected.
-- EXCLUDED: docslot.patients (cross-tenant by design — no tenant_id; isolation is
-- via patient_tenant_links) and docslot.doctors (non-PHI directory data, already
-- tenant-filtered in code; RLS there would block the worker's context-less doctor
-- scan during materialization).
ALTER TABLE docslot.bookings              ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.time_slots            ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.slot_holds            ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.opd_tokens            ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.booking_status_history ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_bookings ON docslot.bookings
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_time_slots ON docslot.time_slots
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_slot_holds ON docslot.slot_holds
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_opd_tokens ON docslot.opd_tokens
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

-- booking_status_history has no tenant_id; gate via the parent booking (the status
-- trigger inserts during a booking UPDATE inside the same tenant-scoped transaction).
CREATE POLICY tenant_isolation_booking_status_history ON docslot.booking_status_history
    FOR ALL
    USING (
        EXISTS (
            SELECT 1 FROM docslot.bookings b
            WHERE b.booking_id = booking_status_history.booking_id
              AND (b.tenant_id = platform.current_tenant_id()
                   OR b.tenant_id = platform.current_impersonated_tenant())
        )
    );

-- WhatsApp message journal + outbound queue carry tenant_id and PHI-adjacent content
-- (patient/booker/doctor/slot in message text; behalf-consent OTP transits the outbox).
-- Tenant-isolate both. The conversation read + enqueue run with a tenant context; the
-- cross-tenant DRAIN worker has none, so it goes through SECURITY DEFINER functions
-- (docslot.claim_due_outbox / mark_outbox_sent / mark_outbox_failed / requeue_stranded_outbox)
-- that legitimately bypass RLS — never a plain app-role cross-tenant query.
ALTER TABLE docslot.wa_message_log   ENABLE ROW LEVEL SECURITY;
ALTER TABLE docslot.outbox_messages  ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_wa_message_log ON docslot.wa_message_log
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_outbox_messages ON docslot.outbox_messages
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

-- ============================================================================
-- LAYER 6: PII MASKING FUNCTIONS (for unauthorized access display)
-- ============================================================================

-- Mask phone: +91 9876543210 → +91 ******3210
CREATE OR REPLACE FUNCTION platform.mask_phone(p_phone VARCHAR) RETURNS VARCHAR AS $$
    SELECT CASE
        WHEN p_phone IS NULL OR length(p_phone) < 4 THEN p_phone
        ELSE repeat('*', length(p_phone) - 4) || right(p_phone, 4)
    END;
$$ LANGUAGE SQL IMMUTABLE;

-- Mask email: goutam@josh.com → g****@josh.com
CREATE OR REPLACE FUNCTION platform.mask_email(p_email VARCHAR) RETURNS VARCHAR AS $$
    SELECT CASE
        WHEN p_email IS NULL OR position('@' in p_email) < 2 THEN p_email
        ELSE left(p_email, 1) || repeat('*', position('@' in p_email) - 2) || substring(p_email from position('@' in p_email))
    END;
$$ LANGUAGE SQL IMMUTABLE;

-- Mask name: Goutam Roy → G****m R**
CREATE OR REPLACE FUNCTION platform.mask_name(p_name VARCHAR) RETURNS VARCHAR AS $$
    SELECT string_agg(
        CASE
            WHEN length(word) <= 1 THEN word
            WHEN length(word) <= 3 THEN left(word, 1) || repeat('*', length(word) - 1)
            ELSE left(word, 1) || repeat('*', length(word) - 2) || right(word, 1)
        END,
        ' '
    )
    FROM regexp_split_to_table(p_name, '\s+') word;
$$ LANGUAGE SQL IMMUTABLE;

-- ============================================================================
-- LAYER 7: SECURITY PERMISSIONS
-- ============================================================================

-- Register security-related permissions
INSERT INTO platform.permissions (permission_key, resource, action, scope, description, is_dangerous) VALUES
-- Encryption / KMS
('platform.encryption_keys.read', 'encryption_keys', 'read', 'platform', 'View encryption key metadata', false),
('platform.encryption_keys.rotate', 'encryption_keys', 'update', 'platform', 'Rotate encryption keys', true),
('platform.encryption_keys.destroy', 'encryption_keys', 'delete', 'platform', 'Cryptographically destroy keys', true),

-- Audit chain
('platform.audit.verify_chain', 'audit_chain', 'read', 'platform', 'Verify audit chain integrity', false),
('platform.audit.anchor', 'audit_anchors', 'create', 'platform', 'Anchor chain to external store', true),

-- Anomaly response
('platform.anomalies.review', 'anomaly_events', 'update', 'platform', 'Review anomaly alerts', false),
('platform.anomalies.respond', 'anomaly_events', 'update', 'platform', 'Take action on anomalies (lock accounts, revoke sessions)', true),

-- Data subject rights
('platform.export_requests.process', 'data_export_requests', 'update', 'platform', 'Process data export requests', false),
('platform.deletion.certify', 'deletion_certificates', 'create', 'platform', 'Issue deletion certificates', true),

-- Purpose declaration
('docslot.medical_access.declare_purpose', 'purpose_log', 'create', 'tenant', 'Declare purpose when accessing medical records', false),
('docslot.medical_access.break_glass', 'purpose_log', 'create', 'tenant', 'Use emergency break-glass access', true),

-- IP allowlist
('platform.ip_allowlist.manage', 'ip_allowlist', 'update', 'tenant', 'Manage IP allowlist', false);

-- Assign security perms to super_admin (gets everything anyway, but explicit)
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'super_admin'
  AND p.permission_key LIKE 'platform.%'
  AND NOT EXISTS (
    SELECT 1 FROM platform.role_permissions rp
    WHERE rp.role_id = r.role_id AND rp.permission_id = p.permission_id
  );

-- Break-glass available to doctors (with extra auditing)
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'doctor'
  AND p.permission_key IN ('docslot.medical_access.declare_purpose', 'docslot.medical_access.break_glass');

-- ============================================================================
-- VIEWS for security monitoring dashboards
-- ============================================================================

-- Pending security reviews (anomalies + break-glass + consent revocations)
CREATE OR REPLACE VIEW platform.v_security_review_queue AS
SELECT
    'anomaly'::TEXT AS source,
    anomaly_id AS item_id,
    severity,
    detected_at AS occurred_at,
    description,
    user_id,
    tenant_id
FROM platform.anomaly_events
WHERE requires_review = true AND reviewed_at IS NULL

UNION ALL

SELECT
    'break_glass'::TEXT AS source,
    log_id AS item_id,
    'high'::VARCHAR AS severity,
    accessed_at AS occurred_at,
    'Break-glass medical record access: ' || COALESCE(break_glass_reason, 'No reason given')::TEXT AS description,
    user_id,
    tenant_id
FROM platform.purpose_of_use_log
WHERE is_break_glass = true AND reviewed_at IS NULL

UNION ALL

SELECT
    'consent_revocation'::TEXT AS source,
    event_id AS item_id,
    'medium'::VARCHAR AS severity,
    occurred_at,
    'Consent revoked, downstream not yet notified'::TEXT AS description,
    actor_user_id AS user_id,
    tenant_id
FROM platform.consent_event_log
WHERE event_type = 'consent_revoked' AND downstream_notified_at IS NULL

ORDER BY occurred_at DESC;

-- Encryption key health (which keys need rotation)
CREATE OR REPLACE VIEW platform.v_key_rotation_status AS
SELECT
    ek.key_id,
    ek.tenant_id,
    t.display_name AS tenant_name,
    ek.data_class,
    ek.activated_at,
    ek.next_rotation_due_at,
    CASE
        WHEN ek.next_rotation_due_at < NOW() THEN 'overdue'
        WHEN ek.next_rotation_due_at < NOW() + INTERVAL '7 days' THEN 'due_soon'
        ELSE 'ok'
    END AS rotation_status,
    EXTRACT(DAY FROM (ek.next_rotation_due_at - NOW()))::INT AS days_until_rotation,
    (SELECT COUNT(*) FROM platform.key_usage_log WHERE key_id = ek.key_id) AS usage_count
FROM platform.encryption_keys ek
LEFT JOIN platform.tenants t ON t.tenant_id = ek.tenant_id
WHERE ek.status = 'active';

-- ============================================================================
-- END OF SECURITY HARDENING
-- ============================================================================
-- Tables added: 13 (S1-S13)
-- Functions added: 8 (audit chain, RLS helpers, masking)
-- Policies added: 5 (RLS on sensitive tables)
-- Permissions added: 12 (security operations)
--
-- TOTAL DATABASE TABLES: 87 (was 74)
--
-- PRODUCTION CHECKLIST:
-- [ ] Configure KMS provider, populate encryption_keys for each data_class
-- [ ] Verify pgcrypto extension installed (for digest() function)
-- [ ] Schedule daily job: SELECT * FROM platform.verify_audit_chain();
-- [ ] Schedule daily anchoring job to external transparency log
-- [ ] Set up anomaly detection workers (consume audit_log → anomaly_events)
-- [ ] Configure application to set app.tenant_id session variable on every request
-- [ ] Test RLS policies with multi-tenant queries
-- [ ] Document break-glass procedure for clinical staff
-- ============================================================================
