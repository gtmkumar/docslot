-- ============================================================================
-- DocSlot Platform Database — Part 2: Platform-as-a-Service API Layer
-- ============================================================================
-- This layer enables third-party applications (other healthcare apps, HMS
-- systems, insurance platforms) to consume DocSlot/RuralReach/SafeHer/etc
-- as a service via REST/GraphQL APIs.
--
-- Architecture: OAuth 2.0 client credentials + JWT with scoped permissions
-- ============================================================================

-- ============================================================================
-- TABLE 19: API_CLIENTS (third-party applications registered with platform)
-- ============================================================================
CREATE TABLE platform_api.api_clients (
    client_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_code         VARCHAR(50) NOT NULL UNIQUE,         -- 'apollo-hms', 'star-insurance', 'pharmeasy'
    client_name         VARCHAR(200) NOT NULL,
    client_secret_hash  VARCHAR(255) NOT NULL,                -- bcrypt hash, never store plain secret
    client_type         VARCHAR(30) NOT NULL                  -- 'first_party', 'partner', 'public'
        CHECK (client_type IN ('first_party', 'partner', 'public')),

    -- Owner
    owner_tenant_id     UUID REFERENCES platform.tenants(tenant_id),  -- NULL for platform-owned clients
    owner_email         CITEXT NOT NULL,
    owner_organization  VARCHAR(200),

    -- OAuth config
    grant_types         VARCHAR(50)[] NOT NULL DEFAULT ARRAY['client_credentials'],
                                                              -- 'client_credentials', 'authorization_code', 'refresh_token'
    redirect_uris       VARCHAR(500)[],                       -- For authorization_code flow
    allowed_origins     VARCHAR(200)[],                       -- CORS allow list

    -- Rate limiting
    rate_limit_per_minute INT NOT NULL DEFAULT 60,
    rate_limit_per_day  INT NOT NULL DEFAULT 10000,
    burst_limit         INT NOT NULL DEFAULT 100,

    -- Webhooks
    webhook_signing_secret VARCHAR(255),                       -- For HMAC signing of outbound webhooks

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_verified         BOOLEAN NOT NULL DEFAULT false,       -- Partner verification status
    verified_at         TIMESTAMPTZ,
    verified_by         UUID REFERENCES platform.users(user_id),

    -- Compliance
    purpose             TEXT NOT NULL,                         -- Why does this client need access?
    data_protection_agreement_url VARCHAR(500),
    data_protection_signed_at TIMESTAMPTZ,

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_used_at        TIMESTAMPTZ,
    deleted_at          TIMESTAMPTZ
);

CREATE INDEX idx_api_clients_active ON platform_api.api_clients(client_code)
    WHERE deleted_at IS NULL AND is_active = true;
CREATE INDEX idx_api_clients_tenant ON platform_api.api_clients(owner_tenant_id)
    WHERE owner_tenant_id IS NOT NULL;

CREATE TRIGGER trg_api_clients_updated_at BEFORE UPDATE ON platform_api.api_clients
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE 20: API_SCOPES (granular API permissions, separate from RBAC)
-- ============================================================================
-- Scopes are like permissions but specifically for API access.
-- Examples: 'bookings:read', 'patients:write', 'reports:deliver'
CREATE TABLE platform_api.api_scopes (
    scope_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    scope_key           VARCHAR(100) NOT NULL UNIQUE,        -- 'docslot.bookings.read'
    product_id          UUID REFERENCES platform.products(product_id),
    resource            VARCHAR(50) NOT NULL,
    action              VARCHAR(30) NOT NULL,
    description         TEXT NOT NULL,
    is_dangerous        BOOLEAN NOT NULL DEFAULT false,
    requires_consent    BOOLEAN NOT NULL DEFAULT false,      -- Patient consent required (for PHR access)
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Seed common DocSlot API scopes
INSERT INTO platform_api.api_scopes (scope_key, resource, action, description, is_dangerous, requires_consent) VALUES
-- Read scopes
('docslot.bookings.read', 'bookings', 'read', 'Read booking data', false, false),
('docslot.patients.read', 'patients', 'read', 'Read patient data', true, true),
('docslot.doctors.read', 'doctors', 'read', 'Read doctor profiles', false, false),
('docslot.slots.read', 'slots', 'read', 'Read available slots', false, false),
('docslot.prescriptions.read', 'prescriptions', 'read', 'Read prescriptions', true, true),
('docslot.reports.read', 'reports', 'read', 'Read lab reports', true, true),
-- Write scopes
('docslot.bookings.write', 'bookings', 'create', 'Create/update bookings', false, false),
('docslot.patients.write', 'patients', 'create', 'Create/update patient records', true, false),
('docslot.prescriptions.write', 'prescriptions', 'create', 'Create prescriptions', true, false),
('docslot.reports.upload', 'reports', 'create', 'Upload lab reports', false, false),
-- Action scopes
('docslot.bookings.approve', 'bookings', 'approve', 'Approve pending bookings', true, false),
('docslot.bookings.cancel', 'bookings', 'update', 'Cancel bookings', false, false),
('docslot.reports.deliver', 'reports', 'update', 'Trigger report delivery via WhatsApp', false, false),
-- ABDM scopes
('docslot.abdm.records.fetch', 'abdm_records', 'read', 'Fetch ABDM health records', true, true),
('docslot.abdm.records.push', 'abdm_records', 'create', 'Push records to ABDM PHR', true, true);

CREATE INDEX idx_api_scopes_product ON platform_api.api_scopes(product_id);
CREATE INDEX idx_api_scopes_resource ON platform_api.api_scopes(resource, action);

-- ============================================================================
-- TABLE 21: API_CLIENT_SCOPES (which scopes each API client can request)
-- ============================================================================
CREATE TABLE platform_api.api_client_scopes (
    client_id           UUID NOT NULL REFERENCES platform_api.api_clients(client_id) ON DELETE CASCADE,
    scope_id            UUID NOT NULL REFERENCES platform_api.api_scopes(scope_id) ON DELETE CASCADE,
    granted_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    granted_by          UUID REFERENCES platform.users(user_id),
    expires_at          TIMESTAMPTZ,                          -- Time-bound scope grants
    PRIMARY KEY (client_id, scope_id)
);

-- ============================================================================
-- TABLE 22: API_TOKENS (issued JWT tokens for clients)
-- ============================================================================
CREATE TABLE platform_api.api_tokens (
    token_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id           UUID NOT NULL REFERENCES platform_api.api_clients(client_id) ON DELETE CASCADE,
    token_hash          VARCHAR(64) NOT NULL UNIQUE,         -- SHA-256 of JWT

    -- Token scope
    requested_scopes    VARCHAR(100)[] NOT NULL,
    granted_scopes      VARCHAR(100)[] NOT NULL,             -- May be subset of requested

    -- Tenant context (token is scoped to a specific tenant)
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),

    -- User context (for authorization_code flow)
    user_id             UUID REFERENCES platform.users(user_id),  -- NULL for client_credentials

    -- Validity
    issued_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL,
    last_used_at        TIMESTAMPTZ,
    use_count           BIGINT NOT NULL DEFAULT 0,

    -- Revocation
    revoked_at          TIMESTAMPTZ,
    revoked_by          UUID REFERENCES platform.users(user_id),
    revoked_reason      VARCHAR(200)
);

CREATE INDEX idx_api_tokens_hash ON platform_api.api_tokens(token_hash) WHERE revoked_at IS NULL;
CREATE INDEX idx_api_tokens_client ON platform_api.api_tokens(client_id, issued_at DESC);
CREATE INDEX idx_api_tokens_expired ON platform_api.api_tokens(expires_at) WHERE revoked_at IS NULL;

-- ============================================================================
-- TABLE 23: API_REQUESTS (request log for rate limiting + analytics)
-- ============================================================================
CREATE TABLE platform_api.api_requests (
    request_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id           UUID REFERENCES platform_api.api_clients(client_id),
    token_id            UUID REFERENCES platform_api.api_tokens(token_id),
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),

    -- Request
    method              VARCHAR(10) NOT NULL,
    path                VARCHAR(500) NOT NULL,
    ip_address          INET,
    user_agent          TEXT,

    -- Response
    status_code         INT NOT NULL,
    response_time_ms    INT,
    response_size_bytes INT,

    -- Errors
    error_code          VARCHAR(50),
    error_message       TEXT,

    occurred_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_api_requests_client_time ON platform_api.api_requests(client_id, occurred_at DESC);
CREATE INDEX idx_api_requests_errors ON platform_api.api_requests(client_id, status_code, occurred_at DESC)
    WHERE status_code >= 400;

-- Auto-cleanup old request logs (keep 30 days)
-- Run as scheduled job: DELETE FROM platform_api.api_requests WHERE occurred_at < NOW() - INTERVAL '30 days';

-- ============================================================================
-- TABLE 24: WEBHOOK_SUBSCRIPTIONS (outbound event subscriptions)
-- ============================================================================
CREATE TABLE platform_api.webhook_subscriptions (
    webhook_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id           UUID NOT NULL REFERENCES platform_api.api_clients(client_id) ON DELETE CASCADE,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),    -- NULL = subscribe to all accessible tenants

    name                VARCHAR(100) NOT NULL,
    url                 VARCHAR(500) NOT NULL,
    secret_hash         TEXT NOT NULL,                        -- HMAC signing secret, AES-ENCRYPTED at rest (envelope) → TEXT, not VARCHAR(255)

    -- Event filtering
    event_types         VARCHAR(100)[] NOT NULL,             -- ['booking.created', 'report.uploaded', 'patient.consent_revoked']
    filter_expression   TEXT,                                  -- Optional JSONPath filter

    -- Reliability
    max_retries         SMALLINT NOT NULL DEFAULT 5,
    retry_backoff       VARCHAR(20) NOT NULL DEFAULT 'exponential',
    timeout_seconds     SMALLINT NOT NULL DEFAULT 30,

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,
    last_success_at     TIMESTAMPTZ,
    last_failure_at     TIMESTAMPTZ,
    consecutive_failures SMALLINT NOT NULL DEFAULT 0,
    auto_disabled_at    TIMESTAMPTZ,                          -- Disabled after threshold failures

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_webhooks_active ON platform_api.webhook_subscriptions(client_id)
    WHERE is_active = true AND auto_disabled_at IS NULL;
CREATE INDEX idx_webhooks_events ON platform_api.webhook_subscriptions USING gin(event_types);

CREATE TRIGGER trg_webhooks_updated_at BEFORE UPDATE ON platform_api.webhook_subscriptions
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE 25: WEBHOOK_DELIVERIES (delivery attempts and history)
-- ============================================================================
CREATE TABLE platform_api.webhook_deliveries (
    delivery_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    webhook_id          UUID NOT NULL REFERENCES platform_api.webhook_subscriptions(webhook_id) ON DELETE CASCADE,

    event_type          VARCHAR(100) NOT NULL,
    event_id            UUID NOT NULL,                        -- Idempotency key
    payload             JSONB NOT NULL,
    signature           VARCHAR(255),                          -- HMAC signature sent

    -- Delivery state
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'success', 'failed', 'abandoned')),
    attempt_count       SMALLINT NOT NULL DEFAULT 0,

    -- Response
    response_status_code INT,
    response_headers    JSONB,
    response_body       TEXT,
    response_time_ms    INT,
    error_message       TEXT,

    -- Scheduling
    next_retry_at       TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    delivered_at        TIMESTAMPTZ
);

-- Due-set scan for the drain claim: pending/failed-past-backoff/stranded-processing-past-lease.
-- 'processing' is included so the worker's expired-lease reclaim (status='processing' AND
-- next_retry_at <= now) stays index-covered — parity with idx_integration_outbox_due (TABLE 27).
CREATE INDEX idx_webhook_deliveries_pending ON platform_api.webhook_deliveries(next_retry_at)
    WHERE status IN ('pending', 'failed', 'processing');
CREATE INDEX idx_webhook_deliveries_webhook ON platform_api.webhook_deliveries(webhook_id, created_at DESC);
CREATE INDEX idx_webhook_deliveries_event_dedup ON platform_api.webhook_deliveries(event_id);

-- ============================================================================
-- TABLE 26: API_EVENT_TYPES (registry of all events that can be subscribed to)
-- ============================================================================
CREATE TABLE platform_api.api_event_types (
    event_type          VARCHAR(100) PRIMARY KEY,            -- 'docslot.booking.created'
    product_id          UUID REFERENCES platform.products(product_id),
    resource            VARCHAR(50) NOT NULL,
    action              VARCHAR(30) NOT NULL,
    description         TEXT NOT NULL,
    payload_schema      JSONB,                                -- JSON Schema for payload validation
    is_active           BOOLEAN NOT NULL DEFAULT true,
    requires_scope      VARCHAR(100),                          -- Scope required to subscribe
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Seed DocSlot events
INSERT INTO platform_api.api_event_types (event_type, resource, action, description, requires_scope) VALUES
('docslot.booking.created', 'booking', 'created', 'New booking created', 'docslot.bookings.read'),
('docslot.booking.approved', 'booking', 'approved', 'Booking approved', 'docslot.bookings.read'),
('docslot.booking.checked_in', 'booking', 'checked_in', 'Patient checked in at the front desk', 'docslot.bookings.read'),
('docslot.booking.rescheduled', 'booking', 'rescheduled', 'Booking moved to a new slot', 'docslot.bookings.read'),
('docslot.booking.cancelled', 'booking', 'cancelled', 'Booking cancelled', 'docslot.bookings.read'),
('docslot.booking.completed', 'booking', 'completed', 'Booking marked complete', 'docslot.bookings.read'),
('docslot.booking.no_show', 'booking', 'no_show', 'Patient did not show up', 'docslot.bookings.read'),
('docslot.patient.registered', 'patient', 'created', 'New patient registered', 'docslot.patients.read'),
('docslot.patient.consent_granted', 'patient', 'consent_granted', 'Patient granted data consent', 'docslot.patients.read'),
('docslot.patient.consent_revoked', 'patient', 'consent_revoked', 'Patient revoked data consent', 'docslot.patients.read'),
('docslot.patient.deletion_requested', 'patient', 'deletion_requested', 'Patient requested data deletion', 'docslot.patients.read'),
('docslot.prescription.issued', 'prescription', 'created', 'Prescription issued', 'docslot.prescriptions.read'),
('docslot.report.uploaded', 'report', 'created', 'Lab report uploaded', 'docslot.reports.read'),
('docslot.report.delivered', 'report', 'delivered', 'Report delivered to patient', 'docslot.reports.read'),
('commission.attribution.created', 'attribution', 'created', 'Broker attribution created', 'commission.attribution.read'),
('commission.commission.earned', 'attribution', 'earned', 'Commission earned (booking completed)', 'commission.attribution.read'),
('commission.commission.reversed', 'attribution', 'reversed', 'Commission reversed (cancel/clawback)', 'commission.attribution.read'),
('commission.payout.paid', 'payout', 'paid', 'Payout disbursed', 'commission.payouts.read');

-- ============================================================================
-- TABLE 27: INTEGRATION_EVENT_OUTBOX (durable transactional integration-event outbox)
-- ============================================================================
-- Every integration event raised at the Application boundary is captured here ATOMICALLY with the
-- business write (same UnitOfWork transaction). This closes a real data-loss gap: previously an event
-- was published ONLY through the webhook pipeline (webhook_deliveries, keyed to HTTP subscriptions), so
-- an event with NO matching subscription was silently DISCARDED. The outbox captures EVERY event; a
-- drain worker (IntegrationEventDrainWorker) later publishes due rows to the message broker out-of-band.
--
-- Mirrors webhook_deliveries (status machine, attempt_count, next_retry_at backoff/lease, dedup on the
-- producer-assigned id) but fans to the broker, not per-subscriber — so there is NO webhook_id / no
-- subscription join: one row per event, claimed once, published once.
--
-- ----------------------------------------------------------------------------
-- RLS DECISION: NO ROW LEVEL SECURITY (deliberate, follows the platform_api PaaS convention)
-- ----------------------------------------------------------------------------
-- The platform_api.* tables (api_clients / api_tokens / webhook_subscriptions / webhook_deliveries) carry
-- NO RLS: this is the cross-tenant platform/PaaS layer, and the drain worker runs as the plain docslot_app
-- role under the blanket platform_api grants (NO SECURITY DEFINER needed — same as WebhookDeliveryDrainStore).
-- This table follows that convention. Compensating controls instead of RLS:
--   * CAPTURE happens app-side inside the command's tenant-scoped UnitOfWork transaction (atomic with the
--     business row), so the write itself is already tenant-authorized at the application boundary.
--   * tenant_id is recorded for locality/forensics (and broker routing) but is NOT a security boundary here.
--   * payload is IDs/tokens ONLY — NEVER PHI/PII — so a non-RLS, broker-replayable row leaks no patient data.
--   * append + state-transition ONLY: the app role holds SELECT/INSERT/UPDATE (no DELETE grant; no pruner
--     this slice), so rows are never physically removed.
-- ----------------------------------------------------------------------------
CREATE TABLE platform_api.integration_event_outbox (
    outbox_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_id         UUID NOT NULL UNIQUE,        -- IntegrationEvent.EventId; UNIQUE backs dedup / ON CONFLICT (event_id)
    event_type       VARCHAR(100) NOT NULL,       -- e.g. 'docslot.booking.created'; intentionally NOT FK'd to
                                                  -- api_event_types so capture survives registry drift
    tenant_id        UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,  -- NULLABLE (some events are
                                                  -- tenant-agnostic); recorded for locality/forensics, NOT an RLS boundary
    payload          JSONB NOT NULL,              -- the integration-event envelope JSON — IDs/tokens ONLY, NEVER PHI/PII
    correlation_id   VARCHAR(100),
    occurred_at      TIMESTAMPTZ NOT NULL,
    status           VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'success', 'failed', 'abandoned')),
    attempt_count    SMALLINT NOT NULL DEFAULT 0,
    next_retry_at    TIMESTAMPTZ,                 -- backoff schedule AND the processing-lease watermark
    last_error       TEXT,
    published_at     TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON COLUMN platform_api.integration_event_outbox.payload IS
    'IDs/tokens only — NEVER PHI/PII; not RLS-gated; may be replayed to external consumers.';

-- (Dedup on event_id is enforced by the inline UNIQUE on the column above — it auto-creates the unique index
--  that ON CONFLICT (event_id) DO NOTHING targets; no separate dedup index is needed.)
-- Due-set scan for the drain claim: pending/failed-past-backoff/stranded-processing-past-lease.
CREATE INDEX idx_integration_outbox_due ON platform_api.integration_event_outbox(next_retry_at)
    WHERE status IN ('pending', 'failed', 'processing');
-- Tenant locality / forensics.
CREATE INDEX idx_integration_outbox_tenant ON platform_api.integration_event_outbox(tenant_id, created_at DESC);

-- ============================================================================
-- VIEWS
-- ============================================================================

-- API client usage summary (rate limit monitoring)
CREATE OR REPLACE VIEW platform_api.v_client_usage AS
SELECT
    c.client_id,
    c.client_code,
    c.client_name,
    COUNT(*) FILTER (WHERE r.occurred_at >= NOW() - INTERVAL '1 minute') AS requests_last_minute,
    COUNT(*) FILTER (WHERE r.occurred_at >= NOW() - INTERVAL '1 hour') AS requests_last_hour,
    COUNT(*) FILTER (WHERE r.occurred_at >= NOW() - INTERVAL '1 day') AS requests_last_day,
    COUNT(*) FILTER (WHERE r.status_code >= 400 AND r.occurred_at >= NOW() - INTERVAL '1 hour') AS errors_last_hour,
    c.rate_limit_per_minute,
    c.rate_limit_per_day
FROM platform_api.api_clients c
LEFT JOIN platform_api.api_requests r ON r.client_id = c.client_id
WHERE c.deleted_at IS NULL AND c.is_active = true
GROUP BY c.client_id;

-- Effective scopes for any token
CREATE OR REPLACE VIEW platform_api.v_token_scopes AS
SELECT
    t.token_id,
    t.client_id,
    c.client_code,
    t.tenant_id,
    unnest(t.granted_scopes) AS scope_key,
    t.expires_at
FROM platform_api.api_tokens t
JOIN platform_api.api_clients c ON c.client_id = t.client_id
WHERE t.revoked_at IS NULL AND t.expires_at > NOW();

-- ============================================================================
-- FUNCTIONS
-- ============================================================================

-- Check if a token has a specific scope
CREATE OR REPLACE FUNCTION platform_api.token_has_scope(
    p_token_hash VARCHAR,
    p_scope_key VARCHAR
) RETURNS BOOLEAN AS $$
    SELECT EXISTS (
        SELECT 1
        FROM platform_api.api_tokens t
        WHERE t.token_hash = p_token_hash
          AND t.revoked_at IS NULL
          AND t.expires_at > NOW()
          AND p_scope_key = ANY(t.granted_scopes)
    );
$$ LANGUAGE SQL STABLE;

-- Get tenant_id from token (for tenant scoping in queries)
CREATE OR REPLACE FUNCTION platform_api.token_tenant_id(
    p_token_hash VARCHAR
) RETURNS UUID AS $$
    SELECT t.tenant_id
    FROM platform_api.api_tokens t
    WHERE t.token_hash = p_token_hash
      AND t.revoked_at IS NULL
      AND t.expires_at > NOW();
$$ LANGUAGE SQL STABLE;

-- ============================================================================
-- END OF PLATFORM API LAYER
-- ============================================================================
-- Tables created: 8 (19-26)
-- Total platform tables so far: 26
-- Next: Run 03_docslot.sql for DocSlot product tables
-- ============================================================================
