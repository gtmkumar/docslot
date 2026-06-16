-- ============================================================================
-- DocSlot Platform Database — Production-Ready Schema
-- ============================================================================
-- Version: 1.0
-- Date: April 2026
-- Database: PostgreSQL 16+
-- Architecture: Multi-product platform with shared core + product schemas
--
-- DESIGN PRINCIPLES:
-- 1. RBAC-first — All access control via permissions → roles → users
-- 2. Multi-tenant isolation — Every row scoped by tenant_id
-- 3. Platform-as-a-Service — Third-party apps consume via scoped API tokens
-- 4. Future-proof — New products plug in as new schemas without core changes
-- 5. Audit everything — Immutable trail for DPDP/HIPAA-style compliance
-- 6. UUID primary keys — Globally unique, no sequence contention
-- 7. JSONB for extensibility — Settings, metadata flexible without migrations
-- 8. Soft deletes — deleted_at timestamps, never DELETE for audit reasons
--
-- SCHEMA LAYOUT:
--   platform.*       — Shared core (identity, RBAC, billing, audit)
--   platform_api.*   — Platform-as-a-Service layer (API clients, tokens, webhooks)
--   docslot.*        — DocSlot product (appointment booking)
--   ruralreach.*     — Future: Mobile diagnostic logistics
--   safeher.*        — Future: Women's healthcare access
--   genericfirst.*   — Future: Prescription decision support
--
-- EXECUTION ORDER:
--   1. 01_platform_core.sql      (THIS FILE — must run first)
--   2. 02_platform_api.sql       (Platform-as-a-Service layer)
--   3. 03_docslot.sql            (DocSlot product tables)
--   4. 04_ruralreach.sql         (Optional — future product)
--   5. 05_safeher.sql            (Optional — future product)
--   6. 06_genericfirst.sql       (Optional — future product)
-- ============================================================================

-- ============================================================================
-- EXTENSIONS
-- ============================================================================
CREATE EXTENSION IF NOT EXISTS "pgcrypto";          -- gen_random_uuid()
CREATE EXTENSION IF NOT EXISTS "pg_trgm";           -- Fuzzy text search
CREATE EXTENSION IF NOT EXISTS "btree_gin";         -- Composite indexes
CREATE EXTENSION IF NOT EXISTS "citext";            -- Case-insensitive email

-- ============================================================================
-- SCHEMAS
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS platform;
CREATE SCHEMA IF NOT EXISTS platform_api;

COMMENT ON SCHEMA platform IS 'Shared core tables used by all products: identity, RBAC, billing, audit';
COMMENT ON SCHEMA platform_api IS 'Platform-as-a-Service layer: third-party API clients, tokens, webhooks';

-- ============================================================================
-- REUSABLE FUNCTIONS
-- ============================================================================

-- Updated_at trigger — applied to every table with updated_at column
CREATE OR REPLACE FUNCTION platform.set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Soft delete helper (sets deleted_at instead of DELETE)
CREATE OR REPLACE FUNCTION platform.soft_delete(table_name TEXT, id_column TEXT, id_value UUID)
RETURNS BOOLEAN AS $$
DECLARE
    affected INT;
BEGIN
    EXECUTE format('UPDATE %I SET deleted_at = NOW() WHERE %I = $1 AND deleted_at IS NULL', table_name, id_column)
    USING id_value;
    GET DIAGNOSTICS affected = ROW_COUNT;
    RETURN affected > 0;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- TABLE 1: PRODUCTS REGISTRY (which products exist in this platform)
-- ============================================================================
-- Every product (DocSlot, RuralReach, etc.) registers itself here.
-- Allows platform to enable/disable products per tenant.
CREATE TABLE platform.products (
    product_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    product_key         VARCHAR(50) NOT NULL UNIQUE,        -- 'docslot', 'ruralreach', 'safeher', 'genericfirst'
    name                VARCHAR(100) NOT NULL,
    description         TEXT,
    schema_name         VARCHAR(50) NOT NULL,                -- DB schema name
    is_active           BOOLEAN NOT NULL DEFAULT true,
    requires_features   VARCHAR(100)[],                      -- Dependencies on other products
    metadata            JSONB NOT NULL DEFAULT '{}',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_products_active ON platform.products(product_key) WHERE is_active = true;

-- Seed products
INSERT INTO platform.products (product_key, name, description, schema_name) VALUES
('docslot', 'DocSlot', 'WhatsApp-first appointment booking for healthcare', 'docslot'),
('ruralreach', 'RuralReach', 'Mobile diagnostic van + home collection logistics', 'ruralreach'),
('safeher', 'SafeHer', 'Women-focused healthcare access platform', 'safeher'),
('genericfirst', 'GenericFirst', 'Generic drug prescription decision support', 'genericfirst');

CREATE TRIGGER trg_products_updated_at BEFORE UPDATE ON platform.products
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE 2: TENANTS (the multi-tenant root — was "organizations" in DocSlot)
-- ============================================================================
-- Renamed to "tenants" because non-DocSlot products may have different
-- terminology: RuralReach has "operators", SafeHer has "clinics", etc.
-- Each tenant can subscribe to multiple products.
CREATE TABLE platform.tenants (
    tenant_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_code         VARCHAR(20) NOT NULL UNIQUE,         -- Human-friendly: 'apollo-pune-001'
    legal_name          VARCHAR(200) NOT NULL,
    display_name        VARCHAR(200) NOT NULL,
    tenant_type         VARCHAR(30) NOT NULL,                -- 'individual_doctor', 'hospital', 'pathology_lab', 'mobile_lab_operator', 'pharmacy', etc.

    -- Contact
    primary_email       CITEXT NOT NULL,
    primary_phone       VARCHAR(15) NOT NULL,
    website             VARCHAR(200),

    -- Address (structured for analytics)
    address_line1       VARCHAR(200),
    address_line2       VARCHAR(200),
    city                VARCHAR(100),
    state               VARCHAR(100),
    country             VARCHAR(2) NOT NULL DEFAULT 'IN',    -- ISO 3166-1
    pin_code            VARCHAR(10),
    timezone            VARCHAR(50) NOT NULL DEFAULT 'Asia/Kolkata',

    -- Regulatory (India-specific, extensible via JSONB)
    gstin               VARCHAR(15),                          -- GST number
    pan                 VARCHAR(10),                          -- Permanent Account Number
    cin                 VARCHAR(21),                          -- Corporate Identification Number
    regulatory_metadata JSONB NOT NULL DEFAULT '{}',         -- HFR ID, ABDM IDs, license numbers per country

    -- Status
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'onboarding', 'trial', 'active', 'suspended', 'cancelled', 'archived')),
    trial_ends_at       TIMESTAMPTZ,
    suspended_reason    TEXT,

    -- Settings (per-tenant config, replaces tenant.settings JSONB blob)
    settings            JSONB NOT NULL DEFAULT '{}',         -- Tenant-level config

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at          TIMESTAMPTZ,                          -- Soft delete

    CONSTRAINT chk_tenant_country CHECK (country ~ '^[A-Z]{2}$'),
    CONSTRAINT chk_tenant_gstin CHECK (gstin IS NULL OR length(gstin) = 15)
);

CREATE INDEX idx_tenants_active ON platform.tenants(status) WHERE deleted_at IS NULL AND status = 'active';
CREATE INDEX idx_tenants_code ON platform.tenants(tenant_code) WHERE deleted_at IS NULL;
CREATE INDEX idx_tenants_country_city ON platform.tenants(country, city) WHERE deleted_at IS NULL;
CREATE INDEX idx_tenants_search ON platform.tenants USING gin(display_name gin_trgm_ops) WHERE deleted_at IS NULL;

CREATE TRIGGER trg_tenants_updated_at BEFORE UPDATE ON platform.tenants
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

COMMENT ON TABLE platform.tenants IS 'Multi-tenant root. Every product row is scoped by tenant_id.';

-- ============================================================================
-- TABLE 3: TENANT_PRODUCT_SUBSCRIPTIONS (which products each tenant uses)
-- ============================================================================
CREATE TABLE platform.tenant_product_subscriptions (
    subscription_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    product_id          UUID NOT NULL REFERENCES platform.products(product_id),
    status              VARCHAR(20) NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'paused', 'cancelled')),
    activated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    cancelled_at        TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, product_id)
);

CREATE INDEX idx_tps_tenant ON platform.tenant_product_subscriptions(tenant_id) WHERE status = 'active';
CREATE INDEX idx_tps_product ON platform.tenant_product_subscriptions(product_id) WHERE status = 'active';

-- ============================================================================
-- TABLE 4: PERMISSIONS (granular permission registry — per product)
-- ============================================================================
-- This is the foundation of the entire RBAC system.
-- Permissions follow naming: <product>.<resource>.<action>
-- e.g., 'docslot.booking.approve', 'ruralreach.van.dispatch'
CREATE TABLE platform.permissions (
    permission_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    permission_key      VARCHAR(150) NOT NULL UNIQUE,        -- 'docslot.booking.approve'
    product_id          UUID REFERENCES platform.products(product_id),  -- NULL for platform-level permissions
    resource            VARCHAR(50) NOT NULL,                -- 'booking', 'patient', 'van'
    action              VARCHAR(30) NOT NULL,                -- 'create', 'read', 'update', 'delete', 'approve', 'export'
    scope               VARCHAR(20) NOT NULL DEFAULT 'tenant'
        CHECK (scope IN ('platform', 'tenant', 'self')),
    description         TEXT NOT NULL,
    is_system           BOOLEAN NOT NULL DEFAULT true,       -- System permissions cannot be deleted
    is_dangerous        BOOLEAN NOT NULL DEFAULT false,      -- Requires extra confirmation in UI
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_permissions_product ON platform.permissions(product_id);
CREATE INDEX idx_permissions_resource ON platform.permissions(resource, action);

-- Seed platform-level permissions
INSERT INTO platform.permissions (permission_key, resource, action, scope, description, is_dangerous) VALUES
('platform.tenants.create', 'tenants', 'create', 'platform', 'Create new tenants', false),
('platform.tenants.read', 'tenants', 'read', 'platform', 'View all tenants', false),
('platform.tenants.update', 'tenants', 'update', 'platform', 'Update tenant details', false),
('platform.tenants.suspend', 'tenants', 'update', 'platform', 'Suspend tenants', true),
('platform.tenants.delete', 'tenants', 'delete', 'platform', 'Soft-delete tenants', true),
('platform.users.create', 'users', 'create', 'platform', 'Create platform users', false),
('platform.users.impersonate', 'users', 'update', 'platform', 'Impersonate any user for support', true),
('platform.permissions.manage', 'permissions', 'update', 'platform', 'Manage RBAC permissions', true),
('platform.roles.manage', 'roles', 'update', 'platform', 'Create custom roles', false),
('platform.settings.read', 'settings', 'read', 'platform', 'View platform settings', false),
('platform.settings.update', 'settings', 'update', 'platform', 'Modify platform settings', true),
('platform.billing.read', 'billing', 'read', 'platform', 'View all billing data', false),
('platform.billing.refund', 'billing', 'update', 'platform', 'Process refunds', true),
('platform.audit.read', 'audit', 'read', 'platform', 'View audit logs', false),
('platform.breach.read', 'breach', 'read', 'platform', 'View breach reports', false),
('platform.api_clients.manage', 'api_clients', 'update', 'platform', 'Manage third-party API clients', true);

-- Tenant-level permissions (these are reused across products)
INSERT INTO platform.permissions (permission_key, resource, action, scope, description) VALUES
('tenant.settings.read', 'tenant_settings', 'read', 'tenant', 'View tenant settings'),
('tenant.settings.update', 'tenant_settings', 'update', 'tenant', 'Update tenant settings'),
('tenant.users.create', 'tenant_users', 'create', 'tenant', 'Invite users to tenant'),
('tenant.users.read', 'tenant_users', 'read', 'tenant', 'View tenant users'),
('tenant.users.update', 'tenant_users', 'update', 'tenant', 'Edit tenant users'),
('tenant.users.remove', 'tenant_users', 'delete', 'tenant', 'Remove users from tenant'),
('tenant.roles.assign', 'tenant_users', 'update', 'tenant', 'Assign roles to users'),
('tenant.billing.read', 'tenant_billing', 'read', 'tenant', 'View own tenant billing'),
('tenant.audit.read', 'tenant_audit', 'read', 'tenant', 'View own tenant audit log'),
('tenant.api_keys.manage', 'tenant_api_keys', 'update', 'tenant', 'Manage tenant API keys for integrations');

-- ============================================================================
-- TABLE 5: ROLES (job-specific permission bundles)
-- ============================================================================
-- Roles bundle permissions together for assignment to users.
-- System roles cannot be deleted. Custom roles can be created per tenant.
CREATE TABLE platform.roles (
    role_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    role_key            VARCHAR(50) NOT NULL,                -- 'super_admin', 'doctor', 'van_driver'
    name                VARCHAR(100) NOT NULL,
    description         TEXT,
    product_id          UUID REFERENCES platform.products(product_id),  -- NULL for platform-level roles
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,  -- NULL for system roles
    scope               VARCHAR(20) NOT NULL DEFAULT 'tenant'
        CHECK (scope IN ('platform', 'tenant')),
    is_system           BOOLEAN NOT NULL DEFAULT false,      -- System roles cannot be deleted
    is_default          BOOLEAN NOT NULL DEFAULT false,      -- Auto-assigned to new users
    metadata            JSONB NOT NULL DEFAULT '{}',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at          TIMESTAMPTZ,
    UNIQUE(role_key, tenant_id),                              -- Role keys unique per tenant (or globally for system)
    CONSTRAINT chk_system_role_no_tenant CHECK (
        (is_system = true AND tenant_id IS NULL) OR
        (is_system = false)
    )
);

CREATE INDEX idx_roles_tenant ON platform.roles(tenant_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_roles_product ON platform.roles(product_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_roles_system ON platform.roles(role_key) WHERE is_system = true AND deleted_at IS NULL;

CREATE TRIGGER trg_roles_updated_at BEFORE UPDATE ON platform.roles
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- Seed system roles (platform-level, work across all products)
INSERT INTO platform.roles (role_key, name, description, scope, is_system) VALUES
('super_admin', 'Platform Super Admin', 'Full access to entire platform — all tenants, all products', 'platform', true),
('platform_support', 'Platform Support Engineer', 'Read-only access for customer support, can impersonate', 'platform', true),
('platform_billing', 'Platform Billing Admin', 'Manage billing across all tenants', 'platform', true),
('tenant_owner', 'Tenant Owner', 'Full access within own tenant', 'tenant', true),
('tenant_admin', 'Tenant Admin', 'Administrative access within tenant (no user deletion)', 'tenant', true),
('tenant_staff', 'Tenant Staff', 'Day-to-day operational access', 'tenant', true),
('tenant_viewer', 'Tenant Viewer', 'Read-only access', 'tenant', true);

-- ============================================================================
-- TABLE 6: ROLE_PERMISSIONS (the matrix as data, not code)
-- ============================================================================
CREATE TABLE platform.role_permissions (
    role_id             UUID NOT NULL REFERENCES platform.roles(role_id) ON DELETE CASCADE,
    permission_id       UUID NOT NULL REFERENCES platform.permissions(permission_id) ON DELETE CASCADE,
    granted_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    granted_by          UUID,                                 -- References users(user_id), nullable
    PRIMARY KEY (role_id, permission_id)
);

CREATE INDEX idx_role_perms_role ON platform.role_permissions(role_id);
CREATE INDEX idx_role_perms_permission ON platform.role_permissions(permission_id);

-- Seed: Super Admin gets ALL permissions
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'super_admin';

-- Seed: Platform Support gets read + impersonate
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'platform_support'
AND (p.action = 'read' OR p.permission_key = 'platform.users.impersonate');

-- Seed: Tenant Owner gets all tenant-scoped permissions
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'tenant_owner' AND p.scope IN ('tenant', 'self');

-- Seed: Tenant Admin (no destructive permissions)
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'tenant_admin'
AND p.scope IN ('tenant', 'self')
AND p.is_dangerous = false;

-- Seed: Tenant Viewer (read only)
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'tenant_viewer'
AND p.scope = 'tenant'
AND p.action = 'read';

-- ============================================================================
-- TABLE 7: USERS (platform identity — works across all products and tenants)
-- ============================================================================
-- A user can belong to multiple tenants with different roles in each.
-- Authentication is centralized here; authorization happens via user_tenant_roles.
CREATE TABLE platform.users (
    user_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email               CITEXT NOT NULL UNIQUE,
    phone               VARCHAR(15),
    password_hash       VARCHAR(255),                         -- bcrypt/argon2; nullable for SSO-only users
    full_name           VARCHAR(200) NOT NULL,

    -- Auth state
    email_verified      BOOLEAN NOT NULL DEFAULT false,
    phone_verified      BOOLEAN NOT NULL DEFAULT false,
    mfa_enabled         BOOLEAN NOT NULL DEFAULT false,
    mfa_secret          TEXT,                                 -- ENCRYPTED TOTP secret (envelope); registered in encrypted_fields_registry → TEXT, not VARCHAR(255)
    sso_provider        VARCHAR(50),                          -- 'google', 'microsoft', 'meta' (for WhatsApp embedded signup)
    sso_subject         VARCHAR(200),                         -- Provider's user ID

    -- Security
    last_login_at       TIMESTAMPTZ,
    last_login_ip       INET,
    failed_login_count  SMALLINT NOT NULL DEFAULT 0,
    locked_until        TIMESTAMPTZ,
    password_changed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    must_change_password BOOLEAN NOT NULL DEFAULT false,

    -- Preferences
    preferred_language  VARCHAR(10) NOT NULL DEFAULT 'en',
    timezone            VARCHAR(50) NOT NULL DEFAULT 'Asia/Kolkata',

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_platform_user    BOOLEAN NOT NULL DEFAULT false,      -- Platform-level user (Anthropic employees, super admins)

    -- Compliance
    accepted_terms_at   TIMESTAMPTZ,
    accepted_terms_version VARCHAR(20),

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at          TIMESTAMPTZ,

    CONSTRAINT chk_user_has_auth CHECK (
        password_hash IS NOT NULL OR sso_provider IS NOT NULL
    )
);

CREATE INDEX idx_users_email ON platform.users(email) WHERE deleted_at IS NULL;
CREATE INDEX idx_users_phone ON platform.users(phone) WHERE deleted_at IS NULL AND phone IS NOT NULL;
CREATE INDEX idx_users_active ON platform.users(is_active) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX idx_users_sso ON platform.users(sso_provider, sso_subject)
    WHERE sso_provider IS NOT NULL AND deleted_at IS NULL;

CREATE TRIGGER trg_users_updated_at BEFORE UPDATE ON platform.users
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- Now that users table exists, add FK to role_permissions.granted_by
ALTER TABLE platform.role_permissions
    ADD CONSTRAINT fk_role_perms_granted_by
    FOREIGN KEY (granted_by) REFERENCES platform.users(user_id);

-- ============================================================================
-- TABLE 8: USER_TENANT_ROLES (the bridge — assigns roles to users per tenant)
-- ============================================================================
-- This is where access control is actually enforced.
-- A user has zero or more roles in each tenant they belong to.
-- For platform-level users (super_admin), tenant_id is NULL.
CREATE TABLE platform.user_tenant_roles (
    user_tenant_role_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES platform.users(user_id) ON DELETE CASCADE,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    role_id             UUID NOT NULL REFERENCES platform.roles(role_id) ON DELETE CASCADE,
    is_primary          BOOLEAN NOT NULL DEFAULT false,     -- Primary tenant for the user
    granted_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    granted_by          UUID REFERENCES platform.users(user_id),
    expires_at          TIMESTAMPTZ,                          -- For time-bound access (consultants, support)
    revoked_at          TIMESTAMPTZ,
    revoked_by          UUID REFERENCES platform.users(user_id),
    revoked_reason      VARCHAR(200),
    UNIQUE(user_id, tenant_id, role_id)
);

-- NOTE: predicate cannot use NOW() since PostgreSQL requires IMMUTABLE functions in index predicates.
-- The index filters on revoked_at only; queries needing "active right now" must add
-- "AND (expires_at IS NULL OR expires_at > NOW())" themselves at runtime.
CREATE INDEX idx_utr_user_active ON platform.user_tenant_roles(user_id)
    WHERE revoked_at IS NULL;
CREATE INDEX idx_utr_tenant_active ON platform.user_tenant_roles(tenant_id)
    WHERE revoked_at IS NULL;
CREATE INDEX idx_utr_role ON platform.user_tenant_roles(role_id);

COMMENT ON TABLE platform.user_tenant_roles IS 'Bridge table connecting users → tenants → roles. The heart of access control.';

-- ============================================================================
-- TABLE 9: USER_SESSIONS (JWT token tracking for revocation)
-- ============================================================================
CREATE TABLE platform.user_sessions (
    session_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES platform.users(user_id) ON DELETE CASCADE,
    token_hash          VARCHAR(64) NOT NULL,                -- SHA-256 of JWT
    refresh_token_hash  VARCHAR(64),
    active_tenant_id    UUID REFERENCES platform.tenants(tenant_id),  -- Currently selected tenant
    device_info         VARCHAR(500),                         -- User agent
    ip_address          INET,
    issued_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL,
    refresh_expires_at  TIMESTAMPTZ,
    last_activity_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at          TIMESTAMPTZ,
    revoked_reason      VARCHAR(100)
);

CREATE INDEX idx_sessions_user ON platform.user_sessions(user_id) WHERE revoked_at IS NULL;
CREATE INDEX idx_sessions_token ON platform.user_sessions(token_hash) WHERE revoked_at IS NULL;
CREATE INDEX idx_sessions_expired ON platform.user_sessions(expires_at) WHERE revoked_at IS NULL;

-- ============================================================================
-- TABLE 10: LOGIN_ATTEMPTS (rate limiting + lockout)
-- ============================================================================
CREATE TABLE platform.login_attempts (
    attempt_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email               CITEXT NOT NULL,
    ip_address          INET NOT NULL,
    user_agent          TEXT,
    success             BOOLEAN NOT NULL,
    failure_reason      VARCHAR(100),
    attempted_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_login_attempts_email ON platform.login_attempts(email, attempted_at DESC);
CREATE INDEX idx_login_attempts_ip ON platform.login_attempts(ip_address, attempted_at DESC);

-- ============================================================================
-- TABLE 11: PASSWORD_RESET_TOKENS
-- ============================================================================
CREATE TABLE platform.password_reset_tokens (
    token_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES platform.users(user_id) ON DELETE CASCADE,
    token_hash          VARCHAR(64) NOT NULL,
    requested_ip        INET,
    expires_at          TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '1 hour'),
    used_at             TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_password_reset_token ON platform.password_reset_tokens(token_hash) WHERE used_at IS NULL;

-- ============================================================================
-- TABLE 12: PLATFORM_SETTINGS (super admin runtime config)
-- ============================================================================
CREATE TABLE platform.platform_settings (
    setting_key         VARCHAR(150) PRIMARY KEY,
    setting_value       TEXT NOT NULL,
    value_type          VARCHAR(20) NOT NULL DEFAULT 'string'
        CHECK (value_type IN ('string', 'number', 'boolean', 'json', 'secret')),
    category            VARCHAR(50) NOT NULL,
    is_encrypted        BOOLEAN NOT NULL DEFAULT false,
    description         TEXT,
    updated_by          UUID REFERENCES platform.users(user_id),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_platform_settings_category ON platform.platform_settings(category);

-- ============================================================================
-- TABLE 13: AUDIT_LOG (immutable audit trail for ALL data access)
-- ============================================================================
-- Captures every read/write to sensitive data. Required for DPDP/HIPAA.
CREATE TABLE platform.audit_log (
    audit_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    occurred_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Actor
    user_id             UUID REFERENCES platform.users(user_id),
    api_client_id       UUID,                                 -- Set if action via API
    impersonator_user_id UUID REFERENCES platform.users(user_id), -- Support staff acting as another user
    ip_address          INET,
    user_agent          TEXT,
    correlation_id      VARCHAR(100),                         -- For tracing across services

    -- Context
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),
    product_id          UUID REFERENCES platform.products(product_id),

    -- Action
    action              VARCHAR(50) NOT NULL,                 -- 'view', 'create', 'update', 'delete', 'export', 'login'
    resource_type       VARCHAR(50) NOT NULL,                 -- 'patient', 'booking', 'prescription'
    resource_id         UUID,
    resource_label      VARCHAR(200),                         -- Human-readable: "Patient: Goutam Roy"

    -- Change tracking (for updates)
    before_data         JSONB,
    after_data          JSONB,
    change_summary      TEXT,                                 -- e.g., "Changed status from 'pending' to 'confirmed'"

    -- Compliance
    purpose             VARCHAR(200),                         -- Why was this action taken?
    legal_basis         VARCHAR(50),                          -- DPDP: 'consent', 'contract', 'legal_obligation'

    -- Result
    success             BOOLEAN NOT NULL DEFAULT true,
    error_code          VARCHAR(50),
    error_message       TEXT
);

CREATE INDEX idx_audit_resource ON platform.audit_log(resource_type, resource_id, occurred_at DESC);
CREATE INDEX idx_audit_tenant ON platform.audit_log(tenant_id, occurred_at DESC);
CREATE INDEX idx_audit_user ON platform.audit_log(user_id, occurred_at DESC);
CREATE INDEX idx_audit_action ON platform.audit_log(action, occurred_at DESC);

-- Partition audit_log by month for performance (uncomment to enable partitioning)
-- ALTER TABLE platform.audit_log SET (autovacuum_enabled = true);
-- CREATE TABLE platform.audit_log_y2026m04 PARTITION OF platform.audit_log
--     FOR VALUES FROM ('2026-04-01') TO ('2026-05-01');

COMMENT ON TABLE platform.audit_log IS 'Immutable audit trail. NEVER DELETE rows directly — use retention jobs.';

-- ----------------------------------------------------------------------------
-- AUDIT_LOG APPEND-ONLY ENFORCEMENT (substrate-level tamper-evidence)
-- ----------------------------------------------------------------------------
-- The application is INSERT-only and the hash chain (05_security_hardening.sql)
-- detects tampering, but a guard trigger enforces append-only at the DATABASE
-- layer so even a misbehaving app role (or a bug) cannot mutate/erase history.
-- This blocks UPDATE/DELETE regardless of grants (a guard trigger fires even for
-- table owners/superusers, unlike REVOKE which a superuser bypasses).
-- Retention/partition drops must be done by a privileged maintenance role that
-- explicitly sets `app.allow_audit_maintenance` for the session.
--
-- ESCAPE-HATCH CONFINEMENT (slice 03b): the least-privilege application role
-- `docslot_app` must NOT be able to bypass append-only by setting the GUC. The
-- guard ignores `app.allow_audit_maintenance` whenever the current role is the
-- app role, so for the app there is NO path to UPDATE/DELETE audit_log. Only a
-- separate privileged maintenance role can opt in.
CREATE OR REPLACE FUNCTION platform.block_audit_log_mutation()
RETURNS TRIGGER AS $$
BEGIN
    IF current_user <> 'docslot_app'
       AND COALESCE(current_setting('app.allow_audit_maintenance', true), 'off') = 'on' THEN
        RETURN COALESCE(NEW, OLD);   -- privileged retention job (NOT the app role) explicitly opted in
    END IF;
    RAISE EXCEPTION 'platform.audit_log is append-only: % is not permitted', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_audit_log_append_only
    BEFORE UPDATE OR DELETE ON platform.audit_log
    FOR EACH ROW EXECUTE FUNCTION platform.block_audit_log_mutation();

-- ============================================================================
-- TABLE 14: BREACH_LOG (security incident reporting)
-- ============================================================================
CREATE TABLE platform.breach_log (
    breach_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    breach_type         VARCHAR(50) NOT NULL,
    severity            VARCHAR(20) NOT NULL CHECK (severity IN ('low', 'medium', 'high', 'critical')),
    description         TEXT NOT NULL,

    -- Scope
    affected_tenant_ids UUID[],
    affected_user_ids   UUID[],
    affected_record_count INT,
    affected_data_categories VARCHAR(100)[],                   -- ['phone', 'medical_history', 'payment_info']

    -- Discovery
    detected_at         TIMESTAMPTZ NOT NULL,
    detected_by         VARCHAR(100),
    detection_method    VARCHAR(100),

    -- Response (DPDP Act requires within 72 hours)
    reported_to_dpb_at  TIMESTAMPTZ,
    reported_to_users_at TIMESTAMPTZ,
    containment_actions TEXT,
    root_cause          TEXT,
    remediation         TEXT,

    -- Resolution
    resolved_at         TIMESTAMPTZ,
    resolved_by         UUID REFERENCES platform.users(user_id),

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by          UUID REFERENCES platform.users(user_id)
);

CREATE INDEX idx_breach_unresolved ON platform.breach_log(severity, detected_at DESC) WHERE resolved_at IS NULL;

-- ============================================================================
-- TABLE 15: ALERTS (system alert deduplication and history)
-- ============================================================================
CREATE TABLE platform.alerts (
    alert_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code                VARCHAR(50) NOT NULL,
    severity            VARCHAR(20) NOT NULL DEFAULT 'warning'
        CHECK (severity IN ('info', 'warning', 'critical')),
    message             TEXT NOT NULL,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),
    product_id          UUID REFERENCES platform.products(product_id),
    metadata            JSONB NOT NULL DEFAULT '{}',
    sent_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_channels       VARCHAR(100),                         -- 'email,slack,whatsapp'
    acknowledged_at     TIMESTAMPTZ,
    acknowledged_by     UUID REFERENCES platform.users(user_id),
    resolved_at         TIMESTAMPTZ
);

CREATE INDEX idx_alerts_code_recent ON platform.alerts(code, sent_at DESC);
CREATE INDEX idx_alerts_unresolved ON platform.alerts(severity, sent_at DESC) WHERE resolved_at IS NULL;
CREATE INDEX idx_alerts_tenant ON platform.alerts(tenant_id, sent_at DESC) WHERE tenant_id IS NOT NULL;

-- ============================================================================
-- TABLE 16: NOTIFICATIONS (in-app dashboard notifications)
-- ============================================================================
CREATE TABLE platform.notifications (
    notification_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES platform.users(user_id) ON DELETE CASCADE,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id),
    product_id          UUID REFERENCES platform.products(product_id),
    type                VARCHAR(50) NOT NULL,
    severity            VARCHAR(20) NOT NULL DEFAULT 'info'
        CHECK (severity IN ('info', 'success', 'warning', 'critical')),
    title               VARCHAR(200) NOT NULL,
    message             TEXT NOT NULL,
    action_url          VARCHAR(500),
    metadata            JSONB NOT NULL DEFAULT '{}',
    is_read             BOOLEAN NOT NULL DEFAULT false,
    read_at             TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ DEFAULT (NOW() + INTERVAL '30 days')
);

CREATE INDEX idx_notifications_user_unread ON platform.notifications(user_id, created_at DESC) WHERE is_read = false;

-- ============================================================================
-- TABLE 17: TENANT_QUOTAS (per-tenant resource limits)
-- ============================================================================
CREATE TABLE platform.tenant_quotas (
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    product_id          UUID NOT NULL REFERENCES platform.products(product_id),
    quota_key           VARCHAR(100) NOT NULL,                -- 'max_doctors', 'max_bookings_monthly', 'max_storage_gb'
    quota_value         BIGINT,                                -- NULL = unlimited
    current_usage       BIGINT NOT NULL DEFAULT 0,
    reset_period        VARCHAR(20)                            -- 'monthly', 'never'
        CHECK (reset_period IS NULL OR reset_period IN ('daily', 'monthly', 'yearly', 'never')),
    last_reset_at       TIMESTAMPTZ,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, product_id, quota_key)
);

CREATE INDEX idx_quotas_exceeded ON platform.tenant_quotas(tenant_id)
    WHERE quota_value IS NOT NULL AND current_usage >= quota_value;

-- ============================================================================
-- TABLE 18: DATA_DELETION_REQUESTS (DPDP right-to-erasure tracking)
-- ============================================================================
CREATE TABLE platform.data_deletion_requests (
    request_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    requester_type      VARCHAR(20) NOT NULL CHECK (requester_type IN ('user', 'patient', 'admin')),
    requester_user_id   UUID REFERENCES platform.users(user_id),
    requester_email     CITEXT,                                -- For non-user requesters (patients)
    requester_phone     VARCHAR(15),
    subject_user_id     UUID REFERENCES platform.users(user_id),  -- Whose data to delete
    subject_phone       VARCHAR(15),                            -- For patient requests
    tenant_ids          UUID[],                                 -- Affected tenants
    scope               VARCHAR(20) NOT NULL DEFAULT 'all'
        CHECK (scope IN ('all', 'specific_tenant', 'specific_products')),
    products_affected   VARCHAR(50)[],
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'verified', 'in_progress', 'completed', 'rejected', 'cancelled')),
    reason              TEXT,
    rejection_reason    TEXT,
    grace_period_ends_at TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '30 days'),
    processed_at        TIMESTAMPTZ,
    processed_by        UUID REFERENCES platform.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_deletion_pending ON platform.data_deletion_requests(grace_period_ends_at)
    WHERE status IN ('pending', 'verified');

-- ============================================================================
-- PLATFORM INFRA: IDEMPOTENCY_KEYS (durable request de-duplication)
-- ============================================================================
-- Backs the application's Idempotency-Key pipeline behavior for money/booking
-- mutations. A retried POST carrying the same Idempotency-Key returns the first
-- stored response instead of re-executing — durable across restart/scale-out
-- (an in-memory cache cannot de-dup a real double-charge). Keyed per
-- tenant + endpoint + key. (Promoted from an app-owned runtime table in slice 05
-- so the app no longer issues DDL; the substrate owns the schema.)
CREATE TABLE platform.idempotency_keys (
    idempotency_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_scope        VARCHAR(64) NOT NULL,                 -- tenant_id text, or 'platform' for platform-scoped calls
    endpoint            VARCHAR(120) NOT NULL,                -- 'POST /api/v1/bookings/{id}/approve'
    idempotency_key     VARCHAR(200) NOT NULL,                -- client-supplied Idempotency-Key header
    response_payload    TEXT NOT NULL,                        -- serialized first response (replayed verbatim)
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_scope, endpoint, idempotency_key)
);

-- Lead the lookup index with tenant_scope (tenant_id-leading composite, per schema invariant).
CREATE INDEX idx_idempotency_lookup ON platform.idempotency_keys(tenant_scope, endpoint, idempotency_key);
CREATE INDEX idx_idempotency_cleanup ON platform.idempotency_keys(created_at);

COMMENT ON TABLE platform.idempotency_keys IS 'Durable idempotency de-dup for money/booking mutations. Retention: prune rows older than the max retry window (e.g. 7 days) via a scheduled job.';

-- ============================================================================
-- VIEWS for common queries
-- ============================================================================

-- Effective permissions for any user in any tenant
CREATE OR REPLACE VIEW platform.v_user_permissions AS
SELECT
    utr.user_id,
    utr.tenant_id,
    u.email,
    p.permission_key,
    p.resource,
    p.action,
    p.scope,
    r.role_key,
    r.name AS role_name
FROM platform.user_tenant_roles utr
JOIN platform.users u ON u.user_id = utr.user_id
JOIN platform.roles r ON r.role_id = utr.role_id
JOIN platform.role_permissions rp ON rp.role_id = r.role_id
JOIN platform.permissions p ON p.permission_id = rp.permission_id
WHERE utr.revoked_at IS NULL
  AND (utr.expires_at IS NULL OR utr.expires_at > NOW())
  AND u.is_active = true
  AND u.deleted_at IS NULL;

COMMENT ON VIEW platform.v_user_permissions IS 'Effective permissions per user per tenant. Use for authorization checks.';

-- Active tenants with subscription status
CREATE OR REPLACE VIEW platform.v_active_tenants AS
SELECT
    t.tenant_id,
    t.tenant_code,
    t.display_name,
    t.tenant_type,
    t.country,
    t.city,
    t.status,
    array_agg(p.product_key) FILTER (WHERE p.product_key IS NOT NULL) AS subscribed_products
FROM platform.tenants t
LEFT JOIN platform.tenant_product_subscriptions tps ON tps.tenant_id = t.tenant_id AND tps.status = 'active'
LEFT JOIN platform.products p ON p.product_id = tps.product_id AND p.is_active = true
WHERE t.deleted_at IS NULL AND t.status = 'active'
GROUP BY t.tenant_id;

-- ============================================================================
-- FUNCTIONS for permission checking (used by application code)
-- ============================================================================

-- Check if user has a specific permission in a tenant
CREATE OR REPLACE FUNCTION platform.user_has_permission(
    p_user_id UUID,
    p_permission_key VARCHAR,
    p_tenant_id UUID DEFAULT NULL
) RETURNS BOOLEAN AS $$
    SELECT EXISTS (
        SELECT 1
        FROM platform.v_user_permissions
        WHERE user_id = p_user_id
          AND permission_key = p_permission_key
          AND (tenant_id = p_tenant_id OR scope = 'platform')
    );
$$ LANGUAGE SQL STABLE;

-- Get all permissions for a user in a specific tenant
CREATE OR REPLACE FUNCTION platform.user_permissions_in_tenant(
    p_user_id UUID,
    p_tenant_id UUID
) RETURNS TABLE(permission_key VARCHAR) AS $$
    SELECT DISTINCT v.permission_key
    FROM platform.v_user_permissions v
    WHERE v.user_id = p_user_id
      AND (v.tenant_id = p_tenant_id OR v.scope = 'platform');
$$ LANGUAGE SQL STABLE;

-- ============================================================================
-- END OF PLATFORM CORE
-- ============================================================================
-- Tables created: 18
-- Next: Run 02_platform_api.sql for Platform-as-a-Service layer
-- ============================================================================
