-- ============================================================================
-- DocSlot Platform — Complete Schema Bundle (All-in-One)
-- ============================================================================
-- This is the bundled equivalent of running the 9 canonical files in order:
--   01_platform_core.sql -> 02_platform_api.sql -> 03_docslot.sql
--   -> 05_security_hardening.sql -> 06_ai_services.sql -> 07_commission_broker.sql
--   -> 08_rbac_navigation.sql -> 09_chat_identity.sql -> 04_future_products.sql
--
-- Source files remain canonical in database/*.sql. This bundle is REGENERATED
-- from them by database/regenerate_bundle.py — if you change a source file, re-run it.
--
-- USAGE
--   createdb docslot_platform
--   psql -d docslot_platform -f docslot_complete.sql
--
-- NOT IDEMPOTENT — designed for a fresh, empty database. To re-run: drop the DB first.
-- ============================================================================

\set ON_ERROR_STOP on

DO $bundle$
BEGIN
    RAISE NOTICE '';
    RAISE NOTICE 'DocSlot Platform Schema Bundle - Starting installation';
    RAISE NOTICE '';
END $bundle$;



-- ============================================================================
-- PART 1/11: Platform Core
-- Identity, RBAC, audit, billing, tenant model — platform.*
-- Source: database/01_platform_core.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 1/11: Platform Core: % ---', '01_platform_core.sql';
END $section$;

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


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 1/11: Platform Core complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 2/11: Platform API
-- OAuth 2.0, scoped JWT tokens, webhooks — platform_api.*
-- Source: database/02_platform_api.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 2/11: Platform API: % ---', '02_platform_api.sql';
END $section$;

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

CREATE INDEX idx_webhook_deliveries_pending ON platform_api.webhook_deliveries(next_retry_at)
    WHERE status IN ('pending', 'failed');
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


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 2/11: Platform API complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 3/11: DocSlot Product
-- Booking, prescriptions, ABDM, WhatsApp — docslot.*
-- Source: database/03_docslot.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 3/11: DocSlot Product: % ---', '03_docslot.sql';
END $section$;

-- ============================================================================
-- DocSlot Platform Database — Part 3: DocSlot Product Schema
-- ============================================================================
-- This is the DocSlot product (appointment booking for healthcare).
-- All tables scoped by tenant_id (which IS the healthcare facility).
-- Depends on: platform.* and platform_api.* schemas
-- ============================================================================

CREATE SCHEMA IF NOT EXISTS docslot;
COMMENT ON SCHEMA docslot IS 'DocSlot appointment booking product. All tables scoped by tenant_id.';

-- ============================================================================
-- TABLE D1: HEALTHCARE_FACILITIES (extends tenant with healthcare-specific data)
-- ============================================================================
-- Rather than overloading platform.tenants, we extend it with a 1:1 table
-- containing healthcare-specific attributes. Each tenant subscribed to DocSlot
-- gets one row here.
CREATE TABLE docslot.healthcare_facilities (
    facility_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL UNIQUE REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    facility_type       VARCHAR(30) NOT NULL CHECK (facility_type IN ('individual_doctor', 'hospital', 'pathology_lab', 'clinic', 'pharmacy', 'diagnostic_center')),
    specialty_focus     VARCHAR(100),

    -- WhatsApp Business config
    whatsapp_business_phone_id VARCHAR(50),
    whatsapp_access_token TEXT,                                -- Encrypted
    whatsapp_verified_at TIMESTAMPTZ,

    -- ABDM/India regulatory
    hfr_id              VARCHAR(50),                            -- Health Facility Registry ID
    hfr_status          VARCHAR(20) DEFAULT 'not_registered'
        CHECK (hfr_status IN ('not_registered', 'pending', 'verified', 'rejected', 'suspended')),

    -- Operational config
    appointment_settings JSONB NOT NULL DEFAULT '{}',         -- slot_duration_minutes, auto_confirm, etc.
    business_hours      JSONB NOT NULL DEFAULT '{}',         -- Per-day open/close times

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_facilities_type ON docslot.healthcare_facilities(facility_type);
CREATE INDEX idx_facilities_hfr ON docslot.healthcare_facilities(hfr_id) WHERE hfr_id IS NOT NULL;

CREATE TRIGGER trg_facilities_updated_at BEFORE UPDATE ON docslot.healthcare_facilities
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D2: DEPARTMENTS (for hospitals)
-- ============================================================================
CREATE TABLE docslot.departments (
    department_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    name                VARCHAR(100) NOT NULL,
    code                VARCHAR(20),
    description         TEXT,
    icon                VARCHAR(50),
    display_order       SMALLINT NOT NULL DEFAULT 0,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, name)
);

CREATE INDEX idx_departments_tenant ON docslot.departments(tenant_id, display_order) WHERE is_active = true;

-- ============================================================================
-- TABLE D3: DOCTORS (healthcare providers — also covers lab technicians)
-- ============================================================================
CREATE TABLE docslot.doctors (
    doctor_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    user_id             UUID REFERENCES platform.users(user_id),  -- Links to platform user if doctor logs in

    -- Identity
    full_name           VARCHAR(200) NOT NULL,
    display_name        VARCHAR(200),
    gender              VARCHAR(10) CHECK (gender IS NULL OR gender IN ('male', 'female', 'other', 'prefer_not_say')),
    profile_image_url   TEXT,

    -- Professional
    department_id       UUID REFERENCES docslot.departments(department_id),
    role                VARCHAR(30) NOT NULL DEFAULT 'doctor' CHECK (role IN ('doctor', 'technician', 'nurse', 'pharmacist')),
    specialization      VARCHAR(100),
    sub_specialization  VARCHAR(100),
    qualifications      JSONB NOT NULL DEFAULT '[]',              -- [{degree, college, year}]
    experience_years    SMALLINT,
    languages_spoken    VARCHAR(10)[] DEFAULT ARRAY['en'],
    biography           TEXT,

    -- Pricing
    consultation_fee    DECIMAL(10,2),
    follow_up_fee       DECIMAL(10,2),

    -- Contact
    phone               VARCHAR(15),
    email               CITEXT,

    -- Indian regulatory (NMC/HPR)
    nmc_registration_number VARCHAR(50),
    nmc_state_council   VARCHAR(50),
    nmc_registration_year INT,
    nmc_expires_at      DATE,
    hpr_id              VARCHAR(50),
    nmc_verification_status VARCHAR(20) DEFAULT 'not_verified'
        CHECK (nmc_verification_status IN ('not_verified', 'pending', 'verified', 'rejected', 'expired')),
    nmc_verified_at     TIMESTAMPTZ,

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_accepting_new_patients BOOLEAN NOT NULL DEFAULT true,

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at          TIMESTAMPTZ
);

CREATE INDEX idx_doctors_tenant ON docslot.doctors(tenant_id) WHERE deleted_at IS NULL AND is_active = true;
CREATE INDEX idx_doctors_department ON docslot.doctors(department_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_doctors_specialization ON docslot.doctors(tenant_id, specialization) WHERE deleted_at IS NULL;
CREATE INDEX idx_doctors_nmc ON docslot.doctors(nmc_registration_number) WHERE nmc_registration_number IS NOT NULL;
CREATE INDEX idx_doctors_user ON docslot.doctors(user_id) WHERE user_id IS NOT NULL;
CREATE INDEX idx_doctors_search ON docslot.doctors USING gin(full_name gin_trgm_ops) WHERE deleted_at IS NULL;

CREATE TRIGGER trg_doctors_updated_at BEFORE UPDATE ON docslot.doctors
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D4: DOCTOR_SCHEDULES (recurring weekly availability)
-- ============================================================================
CREATE TABLE docslot.doctor_schedules (
    schedule_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id) ON DELETE CASCADE,
    day_of_week         SMALLINT NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
    start_time          TIME NOT NULL,
    end_time            TIME NOT NULL,
    slot_duration_minutes SMALLINT NOT NULL DEFAULT 15,
    max_patients_per_slot SMALLINT NOT NULL DEFAULT 1,
    break_start_time    TIME,
    break_end_time      TIME,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT chk_schedule_time CHECK (end_time > start_time),
    CONSTRAINT chk_break_time CHECK (
        (break_start_time IS NULL AND break_end_time IS NULL) OR
        (break_start_time IS NOT NULL AND break_end_time IS NOT NULL AND break_end_time > break_start_time)
    )
);

CREATE INDEX idx_schedules_doctor ON docslot.doctor_schedules(doctor_id) WHERE is_active = true;

-- ============================================================================
-- TABLE D5: SCHEDULE_OVERRIDES (holidays, leaves, special hours)
-- ============================================================================
CREATE TABLE docslot.schedule_overrides (
    override_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id) ON DELETE CASCADE,
    override_date       DATE NOT NULL,
    is_blocked          BOOLEAN NOT NULL DEFAULT true,
    custom_start_time   TIME,
    custom_end_time     TIME,
    reason              VARCHAR(200),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(doctor_id, override_date)
);

-- ============================================================================
-- TABLE D6: TIME_SLOTS (generated bookable slots)
-- ============================================================================
CREATE TABLE docslot.time_slots (
    slot_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id) ON DELETE CASCADE,
    slot_date           DATE NOT NULL,
    start_time          TIME NOT NULL,
    end_time            TIME NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'available' CHECK (status IN ('available', 'booked', 'blocked')),
    current_count       SMALLINT NOT NULL DEFAULT 0,
    max_count           SMALLINT NOT NULL DEFAULT 1,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(doctor_id, slot_date, start_time)
);

CREATE INDEX idx_slots_available ON docslot.time_slots(doctor_id, slot_date, status) WHERE status = 'available';
CREATE INDEX idx_slots_tenant_date ON docslot.time_slots(tenant_id, slot_date);

-- ----------------------------------------------------------------------------
-- TABLE D6b: SLOT_HOLDS (hold-on-selection with TTL — FR-BOOK-02)
-- ----------------------------------------------------------------------------
-- When a patient/staff selects a slot, it is held for ~5 minutes so a concurrent
-- booker can't double-book while the form is filled. Abandoned holds expire and
-- free the slot. A booking confirmation converts the hold (consumes capacity).
-- (Promoted from an app-owned runtime table in slice 05 so the app no longer
-- issues DDL; the substrate owns the schema.)
CREATE TABLE docslot.slot_holds (
    hold_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    slot_id             UUID NOT NULL REFERENCES docslot.time_slots(slot_id) ON DELETE CASCADE,
    hold_token          VARCHAR(64) NOT NULL,                 -- opaque token returned to the holder
    booking_id          UUID,                                 -- set when the hold is converted to a booking
    status              VARCHAR(20) NOT NULL DEFAULT 'held'
        CHECK (status IN ('held', 'converted', 'released', 'expired')),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL                  -- created_at + TTL (app sets 5 min)
);

-- tenant_id-leading composite; partial index for the live-hold concurrency check.
CREATE INDEX idx_slot_holds_tenant_live ON docslot.slot_holds(tenant_id, slot_id) WHERE status = 'held';
CREATE INDEX idx_slot_holds_live ON docslot.slot_holds(slot_id) WHERE status = 'held';
CREATE INDEX idx_slot_holds_expiry ON docslot.slot_holds(expires_at) WHERE status = 'held';

COMMENT ON TABLE docslot.slot_holds IS 'Transient TTL holds on time_slots (FR-BOOK-02). A sweeper expires stale holds; confirmed bookings convert them.';

-- ============================================================================
-- TABLE D7: PATIENTS (cross-tenant patient identity)
-- ============================================================================
-- Patients exist at platform level (one phone = one patient identity)
-- but visit multiple tenants. ABHA ID enables cross-tenant medical records.
CREATE TABLE docslot.patients (
    patient_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Primary identity
    phone_number        VARCHAR(15) NOT NULL UNIQUE,
    whatsapp_id         VARCHAR(50),

    -- Demographics
    full_name           VARCHAR(200),
    date_of_birth       DATE,
    age                 SMALLINT,                              -- Computed from DOB or self-reported
    gender              VARCHAR(10) CHECK (gender IS NULL OR gender IN ('male', 'female', 'other', 'prefer_not_say')),
    blood_group         VARCHAR(5) CHECK (blood_group IS NULL OR blood_group IN ('A+','A-','B+','B-','AB+','AB-','O+','O-','UNK')),

    -- Contact
    email               CITEXT,
    address_line1       VARCHAR(200),
    city                VARCHAR(100),
    state               VARCHAR(100),
    pin_code            VARCHAR(10),
    country             VARCHAR(2) NOT NULL DEFAULT 'IN',

    -- Emergency contact
    emergency_contact_name VARCHAR(200),
    emergency_contact_phone VARCHAR(15),
    emergency_contact_relationship VARCHAR(50),

    -- Indian identity (DPDP-compliant — store last 4 only). ENCRYPTED at rest (envelope) → TEXT, not VARCHAR(4).
    aadhaar_last_4      TEXT,

    -- Preferences
    preferred_language  VARCHAR(10) NOT NULL DEFAULT 'en',
    preferred_communication VARCHAR(20) DEFAULT 'whatsapp' CHECK (preferred_communication IN ('whatsapp', 'sms', 'email', 'voice_call')),

    -- DPDP consent
    consent_given_at    TIMESTAMPTZ,
    consent_version     VARCHAR(20),
    consent_ip_address  INET,
    data_retention_until TIMESTAMPTZ,
    deletion_requested_at TIMESTAMPTZ,
    last_incoming_message_at TIMESTAMPTZ,                      -- For 24h WhatsApp window tracking

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at          TIMESTAMPTZ
);

CREATE INDEX idx_patients_phone ON docslot.patients(phone_number) WHERE deleted_at IS NULL;
CREATE INDEX idx_patients_whatsapp ON docslot.patients(whatsapp_id) WHERE whatsapp_id IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX idx_patients_name_search ON docslot.patients USING gin(full_name gin_trgm_ops) WHERE deleted_at IS NULL;

CREATE TRIGGER trg_patients_updated_at BEFORE UPDATE ON docslot.patients
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D8: PATIENT_TENANT_LINKS (which tenants has this patient visited)
-- ============================================================================
-- Tracks the relationship between a patient and the healthcare facilities they've visited.
-- This enables cross-tenant features while respecting tenant data isolation.
CREATE TABLE docslot.patient_tenant_links (
    link_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    patient_local_id    VARCHAR(50),                            -- Tenant's internal patient ID (MRN)
    first_visit_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_visit_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    total_visits        INT NOT NULL DEFAULT 0,
    tenant_notes        TEXT,
    UNIQUE(patient_id, tenant_id)
);

CREATE INDEX idx_patient_tenant ON docslot.patient_tenant_links(tenant_id, patient_id);

-- ============================================================================
-- TABLE D9: FAMILY_MEMBERS (multi-patient under one phone)
-- ============================================================================
CREATE TABLE docslot.family_members (
    family_member_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    primary_patient_id  UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    member_patient_id   UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    relationship        VARCHAR(50) NOT NULL CHECK (relationship IN ('spouse','child','parent','sibling','grandparent','grandchild','other')),
    is_primary_decision_maker BOOLEAN NOT NULL DEFAULT false,
    added_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(primary_patient_id, member_patient_id),
    CHECK (primary_patient_id != member_patient_id)
);

CREATE INDEX idx_family_primary ON docslot.family_members(primary_patient_id);

-- ============================================================================
-- TABLE D10: BOOKINGS (the core entity)
-- ============================================================================
CREATE TABLE docslot.bookings (
    booking_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_number      VARCHAR(20) NOT NULL UNIQUE,            -- Human-readable: 'BKG-2026-04-00001'
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    slot_id             UUID NOT NULL REFERENCES docslot.time_slots(slot_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    department_id       UUID REFERENCES docslot.departments(department_id),

    -- Booking details
    booking_type        VARCHAR(20) NOT NULL DEFAULT 'consultation'
        CHECK (booking_type IN ('consultation', 'follow_up', 'test', 'home_collection', 'procedure', 'tele_consultation')),
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'confirmed', 'checked_in', 'cancelled', 'completed', 'no_show', 'rescheduled')),

    -- Patient info snapshot (in case patient data changes later)
    patient_name_at_booking VARCHAR(200),
    patient_phone_at_booking VARCHAR(15),
    patient_age_at_booking SMALLINT,

    -- Booking context
    booked_via          VARCHAR(20) NOT NULL DEFAULT 'whatsapp'
        CHECK (booked_via IN ('whatsapp', 'dashboard', 'api', 'walk_in', 'phone_call', 'broker_portal')),
    booked_for          VARCHAR(20) NOT NULL DEFAULT 'self'
        CHECK (booked_for IN ('self', 'family_member', 'other')),
    booked_for_patient_id UUID REFERENCES docslot.patients(patient_id),

    -- Notes
    chief_complaint     TEXT,                                    -- Why patient wants to visit
    notes               TEXT,
    cancellation_reason TEXT,

    -- Reminders
    reminder_24h_sent   BOOLEAN NOT NULL DEFAULT false,
    reminder_1h_sent    BOOLEAN NOT NULL DEFAULT false,

    -- Timestamps
    booked_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    confirmed_at        TIMESTAMPTZ,
    checked_in_at       TIMESTAMPTZ,                             -- front-desk arrival (confirmed → checked_in)
    cancelled_at        TIMESTAMPTZ,
    completed_at        TIMESTAMPTZ,
    no_show_at          TIMESTAMPTZ,
    rescheduled_at      TIMESTAMPTZ,                             -- when this booking was superseded by a reschedule

    -- Reschedule lineage: a reschedule TERMINATES this row (status 'rescheduled') and mints a NEW booking
    -- on the new slot. The new row points back here so the move is auditable end-to-end.
    rescheduled_from_booking_id UUID REFERENCES docslot.bookings(booking_id),

    -- Audit
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    cancelled_by_user_id UUID REFERENCES platform.users(user_id),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_bookings_tenant_status ON docslot.bookings(tenant_id, status);
CREATE INDEX idx_bookings_patient ON docslot.bookings(patient_id, booked_at DESC);
CREATE INDEX idx_bookings_doctor_date ON docslot.bookings(doctor_id, booked_at);
CREATE INDEX idx_bookings_pending ON docslot.bookings(tenant_id) WHERE status = 'pending';
CREATE INDEX idx_bookings_reminders ON docslot.bookings(status)
    WHERE status = 'confirmed' AND (reminder_24h_sent = false OR reminder_1h_sent = false);
CREATE INDEX idx_bookings_number ON docslot.bookings(booking_number);

CREATE TRIGGER trg_bookings_updated_at BEFORE UPDATE ON docslot.bookings
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D11: BOOKING_STATUS_HISTORY (state transition audit)
-- ============================================================================
CREATE TABLE docslot.booking_status_history (
    history_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id) ON DELETE CASCADE,
    from_status         VARCHAR(20),
    to_status           VARCHAR(20) NOT NULL,
    changed_by_user_id  UUID REFERENCES platform.users(user_id),
    changed_via         VARCHAR(20),
    reason              TEXT,
    metadata            JSONB DEFAULT '{}',
    changed_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_booking_history ON docslot.booking_status_history(booking_id, changed_at DESC);

-- ============================================================================
-- TABLE D12: OPD_TOKENS (queue management for hospital OPD)
-- ============================================================================
CREATE TABLE docslot.opd_tokens (
    token_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id          UUID NOT NULL UNIQUE REFERENCES docslot.bookings(booking_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    token_date          DATE NOT NULL,
    token_number        INT NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'waiting'
        CHECK (status IN ('waiting', 'called', 'in_consultation', 'completed', 'skipped', 'no_show')),
    called_at           TIMESTAMPTZ,
    consultation_started_at TIMESTAMPTZ,
    completed_at        TIMESTAMPTZ,
    estimated_wait_minutes INT,
    UNIQUE(doctor_id, token_date, token_number)
);

CREATE INDEX idx_tokens_active ON docslot.opd_tokens(doctor_id, token_date, token_number)
    WHERE status IN ('waiting', 'called', 'in_consultation');

-- ============================================================================
-- TABLE D13: TEST_CATALOG (for pathology labs)
-- ============================================================================
CREATE TABLE docslot.test_catalog (
    test_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    test_name           VARCHAR(200) NOT NULL,
    test_code           VARCHAR(50),
    category            VARCHAR(50),
    description         TEXT,
    sample_type         VARCHAR(50),                            -- 'blood', 'urine', 'stool'
    preparation_instructions TEXT,
    price               DECIMAL(10,2),
    discount_price      DECIMAL(10,2),
    report_turnaround_hours SMALLINT,
    is_home_collection_available BOOLEAN NOT NULL DEFAULT false,
    home_collection_fee DECIMAL(10,2),
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_test_catalog_tenant ON docslot.test_catalog(tenant_id, category) WHERE is_active = true;
CREATE INDEX idx_test_catalog_search ON docslot.test_catalog USING gin(test_name gin_trgm_ops) WHERE is_active = true;

-- ============================================================================
-- TABLE D14: PROCEDURE_CATALOG (for hospitals — surgical/IPD procedures)
-- ============================================================================
CREATE TABLE docslot.procedure_catalog (
    procedure_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    department_id       UUID REFERENCES docslot.departments(department_id),
    procedure_name      VARCHAR(200) NOT NULL,
    procedure_code      VARCHAR(50),
    category            VARCHAR(50) NOT NULL CHECK (category IN ('surgery', 'admission', 'day_care', 'icu_stay', 'diagnostic_procedure')),
    description         TEXT,
    typical_duration_hours DECIMAL(5,2),

    -- Itemized pricing (transparency requirement)
    base_price          DECIMAL(10,2) NOT NULL,
    doctor_fee          DECIMAL(10,2) NOT NULL DEFAULT 0,
    anesthesia_fee      DECIMAL(10,2) NOT NULL DEFAULT 0,
    consumables_estimate DECIMAL(10,2) NOT NULL DEFAULT 0,
    room_charges_per_day DECIMAL(10,2) NOT NULL DEFAULT 0,
    minimum_stay_days   SMALLINT NOT NULL DEFAULT 0,

    estimated_min_total DECIMAL(10,2) NOT NULL,
    estimated_max_total DECIMAL(10,2) NOT NULL,

    inclusions          JSONB NOT NULL DEFAULT '[]',
    exclusions          JSONB NOT NULL DEFAULT '[]',

    -- Insurance
    cashless_eligible   BOOLEAN NOT NULL DEFAULT false,
    ab_pmjay_covered    BOOLEAN NOT NULL DEFAULT false,
    ab_pmjay_package_code VARCHAR(20),

    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_procedures_tenant ON docslot.procedure_catalog(tenant_id, category) WHERE is_active = true;
CREATE INDEX idx_procedures_pmjay ON docslot.procedure_catalog(ab_pmjay_package_code) WHERE ab_pmjay_covered = true;

-- ============================================================================
-- TABLE D15: PATIENT_MEDICAL_HISTORY (allergies, conditions, medications)
-- ============================================================================
CREATE TABLE docslot.patient_medical_history (
    history_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    record_type         VARCHAR(30) NOT NULL CHECK (record_type IN ('allergy', 'chronic_condition', 'surgery', 'medication', 'vaccination', 'family_history', 'lifestyle')),
    title               TEXT NOT NULL,                          -- ENCRYPTED at rest (envelope); registered in encrypted_fields_registry → must be TEXT, not VARCHAR(200)
    description         TEXT,                                   -- ENCRYPTED at rest (envelope)
    started_date        DATE,
    ended_date          DATE,
    severity            VARCHAR(20) CHECK (severity IS NULL OR severity IN ('mild', 'moderate', 'severe', 'critical')),
    icd10_code          VARCHAR(10),                            -- For standardized coding
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_critical         BOOLEAN NOT NULL DEFAULT false,         -- Flagged for safety alerts
    added_by_user_id    UUID REFERENCES platform.users(user_id),
    added_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata            JSONB DEFAULT '{}'
);

CREATE INDEX idx_medical_history_patient ON docslot.patient_medical_history(patient_id, record_type) WHERE is_active = true;
CREATE INDEX idx_medical_history_critical ON docslot.patient_medical_history(patient_id) WHERE is_critical = true;

-- ============================================================================
-- TABLE D16: PRESCRIPTIONS
-- ============================================================================
CREATE TABLE docslot.prescriptions (
    prescription_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prescription_number VARCHAR(30) NOT NULL UNIQUE,
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),

    -- Clinical content
    chief_complaints    TEXT,
    examination         TEXT,
    diagnosis           TEXT,
    medications         JSONB NOT NULL DEFAULT '[]',
    investigations      JSONB DEFAULT '[]',                     -- Tests ordered
    advice              TEXT,
    follow_up_in_days   INT,

    -- Generated artifacts
    pdf_url             TEXT,
    file_name           VARCHAR(200),

    -- Delivery
    status              VARCHAR(20) NOT NULL DEFAULT 'draft'
        CHECK (status IN ('draft', 'finalized', 'delivered', 'amended')),
    delivered_at        TIMESTAMPTZ,
    delivery_message_id VARCHAR(100),

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_prescriptions_patient ON docslot.prescriptions(patient_id, created_at DESC);
CREATE INDEX idx_prescriptions_booking ON docslot.prescriptions(booking_id);

-- ============================================================================
-- TABLE D17: DRUG_ALERTS (allergies/interactions flagged at prescription time)
-- ============================================================================
CREATE TABLE docslot.drug_alerts (
    alert_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prescription_id     UUID NOT NULL REFERENCES docslot.prescriptions(prescription_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    alert_type          VARCHAR(30) NOT NULL CHECK (alert_type IN ('allergy', 'interaction', 'contraindication', 'duplicate', 'pregnancy_warning', 'dosage')),
    severity            VARCHAR(20) NOT NULL CHECK (severity IN ('low', 'moderate', 'high', 'critical')),
    medication_name     VARCHAR(200) NOT NULL,
    conflicting_record_id UUID,
    description         TEXT NOT NULL,
    overridden          BOOLEAN NOT NULL DEFAULT false,
    overridden_by_user_id UUID REFERENCES platform.users(user_id),
    override_reason     TEXT,
    overridden_at       TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_drug_alerts_critical ON docslot.drug_alerts(patient_id) WHERE severity = 'critical' AND NOT overridden;

-- ============================================================================
-- TABLE D18: LAB_REPORTS
-- ============================================================================
CREATE TABLE docslot.lab_reports (
    report_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    report_number       VARCHAR(30) NOT NULL UNIQUE,
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    test_id             UUID REFERENCES docslot.test_catalog(test_id),

    file_url            TEXT,
    file_name           VARCHAR(200),
    file_size_bytes     BIGINT,
    file_mime_type      VARCHAR(100),

    -- Structured results (in addition to PDF)
    structured_results  JSONB,                                   -- {parameter: value, normal_range, status}

    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'ready', 'delivered', 'cancelled')),
    uploaded_at         TIMESTAMPTZ,
    uploaded_by_user_id UUID REFERENCES platform.users(user_id),
    delivered_at        TIMESTAMPTZ,
    delivery_message_id VARCHAR(100),

    -- Critical findings flag
    has_critical_findings BOOLEAN NOT NULL DEFAULT false,
    critical_findings_notified_at TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_reports_patient ON docslot.lab_reports(patient_id, created_at DESC);
CREATE INDEX idx_reports_ready ON docslot.lab_reports(tenant_id, status) WHERE status = 'ready';
CREATE INDEX idx_reports_critical ON docslot.lab_reports(tenant_id) WHERE has_critical_findings = true AND critical_findings_notified_at IS NULL;


-- ============================================================================
-- TABLE D19: CONVERSATIONS (WhatsApp conversation state)
-- ============================================================================
CREATE TABLE docslot.conversations (
    conversation_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID REFERENCES docslot.patients(patient_id),
    whatsapp_phone      VARCHAR(15) NOT NULL,
    current_step        VARCHAR(50) NOT NULL DEFAULT 'greeting',
    context             JSONB NOT NULL DEFAULT '{}',
    detected_language   VARCHAR(10),
    last_message_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '30 minutes'),
    is_active           BOOLEAN NOT NULL DEFAULT true
);

CREATE INDEX idx_conversations_active ON docslot.conversations(whatsapp_phone, tenant_id) WHERE is_active = true;
CREATE INDEX idx_conversations_expired ON docslot.conversations(expires_at) WHERE is_active = true;

-- ============================================================================
-- TABLE D20: WA_MESSAGE_LOG (WhatsApp message tracking)
-- ============================================================================
CREATE TABLE docslot.wa_message_log (
    log_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID REFERENCES docslot.patients(patient_id),
    conversation_id     UUID REFERENCES docslot.conversations(conversation_id),
    whatsapp_message_id VARCHAR(100),
    direction           VARCHAR(10) NOT NULL CHECK (direction IN ('inbound', 'outbound')),
    message_type        VARCHAR(20) NOT NULL,                  -- 'text', 'template', 'interactive', 'audio', 'image', 'document'
    template_name       VARCHAR(100),
    content             JSONB,
    -- Canonical message-log status vocabulary across BOTH legs:
    --   inbound  : 'received'
    --   outbound : 'queued' → 'sent' → 'delivered' → 'read'  (or 'failed' on any leg)
    status              VARCHAR(20)
        CHECK (status IS NULL OR status IN ('received', 'queued', 'sent', 'delivered', 'read', 'failed')),
    error_code          VARCHAR(50),
    cost_usd            DECIMAL(10,4),
    sent_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    delivered_at        TIMESTAMPTZ,
    read_at             TIMESTAMPTZ,
    failed_at           TIMESTAMPTZ
);

CREATE INDEX idx_wa_log_tenant ON docslot.wa_message_log(tenant_id, sent_at DESC);
CREATE INDEX idx_wa_log_failed ON docslot.wa_message_log(tenant_id) WHERE status = 'failed';
CREATE INDEX idx_wa_log_message_id ON docslot.wa_message_log(whatsapp_message_id) WHERE whatsapp_message_id IS NOT NULL;

-- ============================================================================
-- TABLE D21: OUTBOX_MESSAGES (reliable WhatsApp delivery queue)
-- ============================================================================
CREATE TABLE docslot.outbox_messages (
    outbox_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID REFERENCES docslot.patients(patient_id),
    message_intent      VARCHAR(50) NOT NULL,
    payload             JSONB NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'sent', 'failed', 'abandoned')),
    attempt_count       SMALLINT NOT NULL DEFAULT 0,
    max_attempts        SMALLINT NOT NULL DEFAULT 5,
    last_error          TEXT,
    next_retry_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at             TIMESTAMPTZ,
    whatsapp_message_id VARCHAR(100),
    correlation_id      VARCHAR(100),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_outbox_pending ON docslot.outbox_messages(next_retry_at, status) WHERE status IN ('pending', 'failed');

-- ============================================================================
-- TABLE D22: PROCESSED_MESSAGES (webhook idempotency)
-- ============================================================================
CREATE TABLE docslot.processed_messages (
    whatsapp_message_id VARCHAR(100) PRIMARY KEY,
    processed_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_processed_recent ON docslot.processed_messages(processed_at DESC);

-- ============================================================================
-- TABLE D23: WAITLIST
-- ============================================================================
CREATE TABLE docslot.waitlist (
    waitlist_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    requested_date      DATE NOT NULL,
    requested_time_range VARCHAR(20),
    status              VARCHAR(20) NOT NULL DEFAULT 'waiting'
        CHECK (status IN ('waiting', 'notified', 'booked', 'expired', 'cancelled')),
    notified_at         TIMESTAMPTZ,
    expires_at          TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '7 days'),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_waitlist_active ON docslot.waitlist(doctor_id, requested_date) WHERE status = 'waiting';

-- ============================================================================
-- TABLE D24: REVIEWS (patient ratings of doctors)
-- ============================================================================
CREATE TABLE docslot.reviews (
    review_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id          UUID NOT NULL UNIQUE REFERENCES docslot.bookings(booking_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    rating              SMALLINT NOT NULL CHECK (rating BETWEEN 1 AND 5),
    comment             TEXT,
    aspects             JSONB DEFAULT '{}',
    is_anonymous        BOOLEAN NOT NULL DEFAULT false,
    is_verified         BOOLEAN NOT NULL DEFAULT true,
    is_published        BOOLEAN NOT NULL DEFAULT true,
    response_from_doctor TEXT,
    responded_at        TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_reviews_doctor_published ON docslot.reviews(doctor_id, created_at DESC) WHERE is_published = true;

-- ============================================================================
-- TABLE D25: ABDM_HEALTH_RECORDS (FHIR R4 health records)
-- ============================================================================
CREATE TABLE docslot.abdm_health_records (
    record_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    booking_id          UUID REFERENCES docslot.bookings(booking_id),
    abha_number         VARCHAR(20) NOT NULL,
    record_type         VARCHAR(50) NOT NULL CHECK (record_type IN ('OPConsultation', 'DischargeSummary', 'Prescription', 'DiagnosticReport', 'ImmunizationRecord', 'WellnessRecord', 'HealthDocumentRecord')),
    fhir_bundle         JSONB NOT NULL,
    care_context_id     VARCHAR(100),
    is_linked_to_phr    BOOLEAN NOT NULL DEFAULT false,
    linked_at           TIMESTAMPTZ,
    consent_id          VARCHAR(100),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_abdm_records_patient ON docslot.abdm_health_records(patient_id, created_at DESC);
CREATE INDEX idx_abdm_records_abha ON docslot.abdm_health_records(abha_number);

-- ============================================================================
-- TABLE D26: ABDM_CONSENTS
-- ============================================================================
CREATE TABLE docslot.abdm_consents (
    consent_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    requesting_tenant_id UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    abdm_consent_request_id VARCHAR(100) NOT NULL,
    abdm_consent_artifact_id VARCHAR(100),
    purpose             VARCHAR(50) NOT NULL,
    health_info_types   VARCHAR(50)[],
    date_range_from     DATE,
    date_range_to       DATE,
    expires_at          TIMESTAMPTZ NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'requested'
        CHECK (status IN ('requested', 'granted', 'denied', 'revoked', 'expired')),
    granted_at          TIMESTAMPTZ,
    revoked_at          TIMESTAMPTZ,
    metadata            JSONB DEFAULT '{}'
);

CREATE INDEX idx_consents_patient ON docslot.abdm_consents(patient_id, status);
CREATE INDEX idx_consents_active ON docslot.abdm_consents(expires_at) WHERE status = 'granted';

-- ============================================================================
-- DOCSLOT-SPECIFIC PERMISSIONS (registered with platform.permissions)
-- ============================================================================
DO $$
DECLARE
    docslot_product_id UUID;
BEGIN
    SELECT product_id INTO docslot_product_id FROM platform.products WHERE product_key = 'docslot';

    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    -- Doctors
    ('docslot.doctor.create', docslot_product_id, 'doctor', 'create', 'tenant', 'Add doctors', false),
    ('docslot.doctor.read', docslot_product_id, 'doctor', 'read', 'tenant', 'View doctors', false),
    ('docslot.doctor.update', docslot_product_id, 'doctor', 'update', 'tenant', 'Edit doctors', false),
    ('docslot.doctor.delete', docslot_product_id, 'doctor', 'delete', 'tenant', 'Delete doctors', true),
    ('docslot.doctor.update_self', docslot_product_id, 'doctor', 'update', 'self', 'Edit own profile', false),

    -- Schedules
    ('docslot.schedule.update', docslot_product_id, 'schedule', 'update', 'tenant', 'Set any doctor schedule', false),
    ('docslot.schedule.update_self', docslot_product_id, 'schedule', 'update', 'self', 'Set own schedule', false),

    -- Slots
    ('docslot.slot.read', docslot_product_id, 'slot', 'read', 'tenant', 'View slots', false),
    ('docslot.slot.block', docslot_product_id, 'slot', 'update', 'tenant', 'Block/unblock slots', false),

    -- Bookings
    ('docslot.booking.create', docslot_product_id, 'booking', 'create', 'tenant', 'Create bookings', false),
    ('docslot.booking.read', docslot_product_id, 'booking', 'read', 'tenant', 'View all bookings', false),
    ('docslot.booking.read_self', docslot_product_id, 'booking', 'read', 'self', 'View own bookings (doctor)', false),
    ('docslot.booking.approve', docslot_product_id, 'booking', 'approve', 'tenant', 'Approve pending bookings', false),
    ('docslot.booking.cancel', docslot_product_id, 'booking', 'update', 'tenant', 'Cancel bookings', false),
    ('docslot.booking.reschedule', docslot_product_id, 'booking', 'update', 'tenant', 'Reschedule bookings', false),
    ('docslot.booking.complete', docslot_product_id, 'booking', 'update', 'tenant', 'Mark complete', false),

    -- Patients
    ('docslot.patient.read', docslot_product_id, 'patient', 'read', 'tenant', 'View patients', true),
    ('docslot.patient.update', docslot_product_id, 'patient', 'update', 'tenant', 'Update patient records', true),

    -- Prescriptions (clinical PHI — slice 03b)
    ('docslot.prescription.create', docslot_product_id, 'prescription', 'create', 'tenant', 'Issue prescriptions', true),
    ('docslot.prescription.read', docslot_product_id, 'prescription', 'read', 'tenant', 'Read prescriptions (PHI)', true),

    -- Pathology
    ('docslot.test.manage', docslot_product_id, 'test', 'update', 'tenant', 'Manage test catalog', false),
    ('docslot.report.upload', docslot_product_id, 'report', 'create', 'tenant', 'Upload lab reports', false),
    ('docslot.report.read', docslot_product_id, 'report', 'read', 'tenant', 'Read lab reports (PHI)', true),
    ('docslot.report.deliver', docslot_product_id, 'report', 'update', 'tenant', 'Deliver reports', false),

    -- Medical history (clinical PHI — slice 03b)
    ('docslot.medical_history.read', docslot_product_id, 'medical_history', 'read', 'tenant', 'Read patient medical history (PHI)', true),

    -- Procedures
    ('docslot.procedure.manage', docslot_product_id, 'procedure', 'update', 'tenant', 'Manage procedure catalog', false),

    -- ABDM
    ('docslot.abdm.records.read', docslot_product_id, 'abdm_records', 'read', 'tenant', 'Access ABDM records', true),
    ('docslot.abdm.records.create', docslot_product_id, 'abdm_records', 'create', 'tenant', 'Push records to ABDM', false),
    ('docslot.abdm.consents.manage', docslot_product_id, 'abdm_consents', 'update', 'tenant', 'Manage ABDM consents', true),

    -- Analytics
    ('docslot.analytics.read', docslot_product_id, 'analytics', 'read', 'tenant', 'View analytics', false);

    -- Assign DocSlot permissions to default tenant roles
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_owner'
      AND p.product_id = docslot_product_id
      AND p.scope IN ('tenant', 'self');

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_admin'
      AND p.product_id = docslot_product_id
      AND p.scope IN ('tenant', 'self')
      AND p.is_dangerous = false;

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_staff'
      AND p.product_id = docslot_product_id
      AND p.permission_key IN (
        'docslot.booking.create', 'docslot.booking.read', 'docslot.booking.approve',
        'docslot.booking.cancel', 'docslot.booking.reschedule', 'docslot.booking.complete',
        'docslot.patient.read', 'docslot.patient.update',
        'docslot.report.upload', 'docslot.report.deliver',
        'docslot.slot.read', 'docslot.doctor.read'
      );
END $$;

-- Create DocSlot-specific roles
INSERT INTO platform.roles (role_key, name, description, product_id, scope, is_system)
SELECT 'doctor', 'Doctor', 'Healthcare provider — own data only', product_id, 'tenant', true
FROM platform.products WHERE product_key = 'docslot';

INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'doctor'
  AND p.permission_key IN (
    'docslot.doctor.read_self', 'docslot.doctor.update_self',
    'docslot.schedule.update_self',
    'docslot.booking.read_self', 'docslot.booking.complete',
    'docslot.patient.read'
  );

-- ============================================================================
-- VIEWS
-- ============================================================================

-- Doctor average ratings
CREATE MATERIALIZED VIEW docslot.v_doctor_ratings AS
SELECT
    doctor_id,
    AVG(rating)::DECIMAL(3,2) AS avg_rating,
    COUNT(*) AS review_count,
    COUNT(*) FILTER (WHERE rating = 5) AS five_star_count
FROM docslot.reviews
WHERE is_published = true
GROUP BY doctor_id;

CREATE UNIQUE INDEX idx_doctor_ratings ON docslot.v_doctor_ratings(doctor_id);

-- Today's bookings overview per tenant
CREATE OR REPLACE VIEW docslot.v_tenant_today_overview AS
SELECT
    t.tenant_id,
    COUNT(*) FILTER (WHERE b.booked_at::DATE = CURRENT_DATE) AS bookings_today,
    COUNT(*) FILTER (WHERE b.status = 'pending') AS pending_approvals,
    COUNT(*) FILTER (WHERE b.completed_at::DATE = CURRENT_DATE) AS completed_today,
    COUNT(*) FILTER (WHERE b.no_show_at::DATE = CURRENT_DATE) AS no_shows_today
FROM platform.tenants t
LEFT JOIN docslot.bookings b ON b.tenant_id = t.tenant_id
WHERE t.deleted_at IS NULL AND t.status = 'active'
GROUP BY t.tenant_id;

-- ============================================================================
-- AUTO-NUMBER GENERATION
-- ============================================================================

-- Auto-generate booking numbers like BKG-2026-04-00001
CREATE SEQUENCE docslot.booking_number_seq;

CREATE OR REPLACE FUNCTION docslot.generate_booking_number()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.booking_number IS NULL THEN
        NEW.booking_number := 'BKG-' || TO_CHAR(NOW(), 'YYYY-MM') || '-' ||
                              LPAD(nextval('docslot.booking_number_seq')::TEXT, 5, '0');
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_booking_number BEFORE INSERT ON docslot.bookings
    FOR EACH ROW EXECUTE FUNCTION docslot.generate_booking_number();

-- Similar for prescription and report numbers
CREATE SEQUENCE docslot.prescription_number_seq;

CREATE OR REPLACE FUNCTION docslot.generate_prescription_number()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.prescription_number IS NULL THEN
        NEW.prescription_number := 'PRX-' || TO_CHAR(NOW(), 'YYYY-MM') || '-' ||
                                   LPAD(nextval('docslot.prescription_number_seq')::TEXT, 5, '0');
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_prescription_number BEFORE INSERT ON docslot.prescriptions
    FOR EACH ROW EXECUTE FUNCTION docslot.generate_prescription_number();

CREATE SEQUENCE docslot.report_number_seq;

CREATE OR REPLACE FUNCTION docslot.generate_report_number()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.report_number IS NULL THEN
        NEW.report_number := 'RPT-' || TO_CHAR(NOW(), 'YYYY-MM') || '-' ||
                             LPAD(nextval('docslot.report_number_seq')::TEXT, 5, '0');
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_report_number BEFORE INSERT ON docslot.lab_reports
    FOR EACH ROW EXECUTE FUNCTION docslot.generate_report_number();

-- ============================================================================
-- BOOKING STATUS HISTORY AUTO-LOGGING
-- ============================================================================
CREATE OR REPLACE FUNCTION docslot.log_booking_status_change()
RETURNS TRIGGER AS $$
BEGIN
    IF OLD.status IS DISTINCT FROM NEW.status THEN
        INSERT INTO docslot.booking_status_history (booking_id, from_status, to_status, changed_by_user_id)
        VALUES (NEW.booking_id, OLD.status, NEW.status, NEW.cancelled_by_user_id);
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_booking_status_log AFTER UPDATE ON docslot.bookings
    FOR EACH ROW EXECUTE FUNCTION docslot.log_booking_status_change();

-- ============================================================================
-- SLOT GENERATION (FR-BOOK-02) — materialize bookable time_slots from the
-- doctor's recurring weekly schedule, honouring schedule_overrides (blocked
-- dates skip; custom hours replace the window) and the per-block break window.
-- ============================================================================
-- SECURITY DEFINER so the nightly materializer + the staff "generate" endpoint
-- can write slots regardless of RLS; tenant_id is derived from the doctor row
-- (authoritative — never a caller-supplied value), and the calling endpoint is
-- tenant-scoped + permission-gated. Idempotent: ON CONFLICT DO NOTHING never
-- clobbers an existing (possibly already-booked) slot. Returns rows created.
CREATE OR REPLACE FUNCTION docslot.generate_time_slots(
    p_doctor_id UUID,
    p_from_date DATE,
    p_to_date   DATE
) RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_tenant       UUID;
    v_date         DATE;
    v_dow          SMALLINT;
    v_sched        RECORD;
    v_ovr          docslot.schedule_overrides%ROWTYPE;
    v_has_override BOOLEAN;
    v_win_start    TIME;
    v_win_end      TIME;
    v_slot_start   TIME;
    v_slot_end     TIME;
    v_created      INT := 0;
    v_n            INT;
BEGIN
    IF p_from_date IS NULL OR p_to_date IS NULL OR p_to_date < p_from_date THEN
        RAISE EXCEPTION 'invalid date range';
    END IF;
    IF p_to_date - p_from_date > 92 THEN
        RAISE EXCEPTION 'date range too large (max 92 days)';
    END IF;

    SELECT tenant_id INTO v_tenant
    FROM docslot.doctors
    WHERE doctor_id = p_doctor_id AND deleted_at IS NULL AND is_active = true;
    IF v_tenant IS NULL THEN
        RETURN 0;   -- unknown / inactive / deleted doctor: nothing to generate
    END IF;

    v_date := p_from_date;
    WHILE v_date <= p_to_date LOOP
        v_dow := EXTRACT(DOW FROM v_date)::SMALLINT;   -- 0=Sunday .. 6=Saturday

        SELECT * INTO v_ovr FROM docslot.schedule_overrides
            WHERE doctor_id = p_doctor_id AND override_date = v_date;
        v_has_override := FOUND;

        -- A blocked override (holiday/leave) skips the whole day.
        IF v_has_override AND v_ovr.is_blocked THEN
            v_date := v_date + 1;
            CONTINUE;
        END IF;

        FOR v_sched IN
            SELECT * FROM docslot.doctor_schedules
            WHERE doctor_id = p_doctor_id AND day_of_week = v_dow AND is_active = true
        LOOP
            -- Custom override hours (if provided) replace the schedule's window for this date.
            IF v_has_override AND v_ovr.custom_start_time IS NOT NULL THEN
                v_win_start := v_ovr.custom_start_time;
                v_win_end   := COALESCE(v_ovr.custom_end_time, v_sched.end_time);
            ELSE
                v_win_start := v_sched.start_time;
                v_win_end   := v_sched.end_time;
            END IF;

            v_slot_start := v_win_start;
            WHILE v_slot_start + make_interval(mins => v_sched.slot_duration_minutes) <= v_win_end LOOP
                v_slot_end := v_slot_start + make_interval(mins => v_sched.slot_duration_minutes);

                -- Skip any slot overlapping the break window.
                IF v_sched.break_start_time IS NULL
                   OR NOT (v_slot_start < v_sched.break_end_time AND v_slot_end > v_sched.break_start_time) THEN
                    INSERT INTO docslot.time_slots
                        (tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count)
                    VALUES
                        (v_tenant, p_doctor_id, v_date, v_slot_start, v_slot_end, 'available', 0, v_sched.max_patients_per_slot)
                    ON CONFLICT (doctor_id, slot_date, start_time) DO NOTHING;
                    GET DIAGNOSTICS v_n = ROW_COUNT;
                    v_created := v_created + v_n;
                END IF;

                v_slot_start := v_slot_end;
            END LOOP;
        END LOOP;

        v_date := v_date + 1;
    END LOOP;

    RETURN v_created;
END;
$$;
COMMENT ON FUNCTION docslot.generate_time_slots IS
    'Materialize bookable time_slots for a doctor over [from,to] from doctor_schedules, honouring schedule_overrides + breaks. Idempotent (ON CONFLICT DO NOTHING). tenant_id derived from the doctor. Returns rows created.';

-- Sweep stale live slot holds to 'expired'. SECURITY DEFINER so the maintenance worker
-- (which has no per-request tenant context) can run it under RLS on slot_holds — a plain
-- app-role UPDATE would match zero rows once app.tenant_id is unset. Returns rows swept.
CREATE OR REPLACE FUNCTION docslot.expire_stale_slot_holds()
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    UPDATE docslot.slot_holds
    SET status = 'expired'
    WHERE status = 'held' AND expires_at < NOW();
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION docslot.expire_stale_slot_holds IS
    'Mark stale held slot_holds (expires_at < now) as expired. SECURITY DEFINER so the RLS-less maintenance worker can sweep across all tenants. Returns rows swept.';

-- ----------------------------------------------------------------------------
-- requeue_stranded_outbox(p_older_than) — outbox 'processing' reaper
-- ----------------------------------------------------------------------------
-- If the drain worker dies mid-send (or a clean shutdown interrupts a send), the
-- claimed row is left in 'processing' and the claim query (status='pending') will
-- never pick it up again → silent message loss. This reaper requeues any row stuck
-- in 'processing' beyond p_older_than back to 'pending' (due now), so the next
-- drain re-attempts it. SECURITY DEFINER: the maintenance worker has no per-request
-- tenant context, so a plain app-role UPDATE would match zero rows under outbox RLS.
CREATE OR REPLACE FUNCTION docslot.requeue_stranded_outbox(p_older_than INTERVAL DEFAULT INTERVAL '5 minutes')
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    UPDATE docslot.outbox_messages
    SET status = 'pending', next_retry_at = NOW()
    WHERE status = 'processing'
      AND created_at < NOW() - p_older_than;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION docslot.requeue_stranded_outbox IS
    'Requeue outbox rows stuck in processing (worker died mid-send) back to pending. SECURITY DEFINER for the RLS-less maintenance worker. Returns rows requeued.';

-- ----------------------------------------------------------------------------
-- expire_stale_consent_otps() — behalf-booking consent OTP expiry
-- ----------------------------------------------------------------------------
-- A behalf booking is created in 'pending' with patient_consent_status='pending'
-- and an OTP sent to the patient. If the patient never approves before the OTP
-- expires, the OTP is marked 'expired', the booking's consent is flipped to
-- 'expired', the booking is cancelled, and the slot capacity it held is freed
-- (decrement current_count, re-open a 'booked' slot) so it can be rebooked.
-- SECURITY DEFINER: runs in the RLS-less maintenance worker across all tenants.
CREATE OR REPLACE FUNCTION docslot.expire_stale_consent_otps()
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    WITH expired AS (
        UPDATE docslot.booking_consent_otps o
        SET status = 'expired'
        WHERE o.status = 'pending' AND o.expires_at < NOW()
        RETURNING o.booking_id
    ),
    cancelled AS (
        UPDATE docslot.bookings b
        SET status = 'cancelled',
            patient_consent_status = 'expired',
            cancellation_reason = COALESCE(b.cancellation_reason, 'Patient consent OTP expired'),
            cancelled_at = NOW()
        FROM expired e
        WHERE b.booking_id = e.booking_id
          AND b.status IN ('pending', 'confirmed')
          AND b.patient_consent_status = 'pending'
        RETURNING b.booking_id, b.slot_id
    ),
    freed AS (
        UPDATE docslot.time_slots s
        SET current_count = GREATEST(s.current_count - 1, 0),
            status = CASE WHEN s.status = 'booked' THEN 'available' ELSE s.status END
        FROM cancelled c
        WHERE s.slot_id = c.slot_id
        RETURNING 1
    ),
    -- A cancelled booking earns no commission: reverse any broker attribution on it (a broker-portal booking)
    -- and debit the wallet from the bucket it sat in (+ lifetime_reversed) — same math as ApplyReversedAsync,
    -- so the consent-expiry worker path matches the in-request consent-deny path. No-op for ordinary behalf
    -- bookings (no attribution).
    to_reverse AS (
        SELECT a.attribution_id, a.broker_id, COALESCE(a.commission_amount_inr, 0) AS amt, a.commission_status AS prev
        FROM commission.attributions a
        JOIN cancelled c ON c.booking_id = a.booking_id
        WHERE a.commission_status IN ('pending', 'earned', 'ready_to_pay')
    ),
    reversed AS (
        UPDATE commission.attributions a
        SET commission_status = 'reversed', updated_at = NOW()
        FROM to_reverse t WHERE a.attribution_id = t.attribution_id
        RETURNING t.broker_id, t.amt, t.prev
    ),
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
    SELECT COUNT(*)::int INTO v_n FROM cancelled;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION docslot.expire_stale_consent_otps IS
    'Expire behalf-booking consent OTPs past expiry: mark OTP expired, cancel the awaiting booking, free its slot capacity, and reverse any broker attribution on it (+ wallet debit). SECURITY DEFINER for the RLS-less maintenance worker. Returns bookings cancelled.';

-- ----------------------------------------------------------------------------
-- Outbox drain functions (SECURITY DEFINER) — RLS bypass for the context-less worker
-- ----------------------------------------------------------------------------
-- outbox_messages is RLS-protected (file 05), but the drain worker is cross-tenant
-- by design and runs with NO app.tenant_id set — a plain app-role query would match
-- zero rows. These definer functions let the worker claim/mark across all tenants
-- (the same pattern as expire_stale_slot_holds / requeue_stranded_outbox). Enqueue
-- and the conversation read still go through RLS with a tenant context.
CREATE OR REPLACE FUNCTION docslot.claim_due_outbox(p_batch INT)
RETURNS TABLE(outbox_id UUID, tenant_id UUID, patient_id UUID, message_intent TEXT,
              to_phone TEXT, body TEXT, correlation_id TEXT, attempt_count INT, max_attempts INT)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
BEGIN
    RETURN QUERY
    WITH due AS (
        SELECT o.outbox_id
        FROM docslot.outbox_messages o
        WHERE o.status = 'pending'
          AND (o.next_retry_at IS NULL OR o.next_retry_at <= now())
        ORDER BY o.created_at
        FOR UPDATE SKIP LOCKED
        LIMIT p_batch
    )
    UPDATE docslot.outbox_messages o
    SET status = 'processing'
    FROM due
    WHERE o.outbox_id = due.outbox_id
    RETURNING o.outbox_id, o.tenant_id, o.patient_id, o.message_intent::text,
              o.payload->>'to', o.payload->>'text', o.correlation_id::text,
              o.attempt_count::int, o.max_attempts::int;
END;
$$;
COMMENT ON FUNCTION docslot.claim_due_outbox IS 'Atomically claim due pending outbox rows (→processing) across tenants. SECURITY DEFINER for the RLS-less drain worker.';

CREATE OR REPLACE FUNCTION docslot.mark_outbox_sent(p_outbox_id UUID, p_provider_id TEXT, p_now TIMESTAMPTZ)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
BEGIN
    -- Mark delivered AND scrub the message body for one-time-code intents (consent + attribution-claim OTPs)
    -- so the live code does not linger in the queue after delivery (DPDP — the code is a one-time secret; the
    -- OTP row keeps only a salted hash).
    UPDATE docslot.outbox_messages
    SET status = 'sent',
        sent_at = p_now,
        whatsapp_message_id = p_provider_id,
        last_error = NULL,
        payload = CASE WHEN message_intent IN ('consent_otp', 'claim_otp')
                       THEN jsonb_set(payload, '{text}', '"[redacted after send]"'::jsonb)
                       ELSE payload END
    WHERE outbox_id = p_outbox_id;
END;
$$;
COMMENT ON FUNCTION docslot.mark_outbox_sent IS 'Mark an outbox row sent (scrubs consent-OTP + claim-OTP bodies post-delivery). SECURITY DEFINER for the RLS-less drain worker.';

CREATE OR REPLACE FUNCTION docslot.mark_outbox_failed(p_outbox_id UUID, p_error TEXT, p_next_retry_at TIMESTAMPTZ)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
BEGIN
    -- Increment attempt; terminal 'abandoned' at max_attempts, else back to 'pending' with the backoff. On
    -- TERMINAL abandon, scrub the one-time-code body (consent + attribution-claim OTPs) so the code doesn't
    -- linger in a dead-lettered row (auditor F4). A retry (→pending) must KEEP the real text — it is the
    -- message still being delivered.
    UPDATE docslot.outbox_messages
    SET attempt_count = attempt_count + 1,
        last_error = p_error,
        status = CASE WHEN attempt_count + 1 >= max_attempts THEN 'abandoned' ELSE 'pending' END,
        next_retry_at = CASE WHEN attempt_count + 1 >= max_attempts THEN next_retry_at ELSE p_next_retry_at END,
        payload = CASE WHEN attempt_count + 1 >= max_attempts AND message_intent IN ('consent_otp', 'claim_otp')
                       THEN jsonb_set(payload, '{text}', '"[redacted after send]"'::jsonb)
                       ELSE payload END
    WHERE outbox_id = p_outbox_id;
END;
$$;
COMMENT ON FUNCTION docslot.mark_outbox_failed IS 'Record a failed outbox send (retry/backoff or abandon; scrubs consent-OTP + claim-OTP body on terminal abandon). SECURITY DEFINER for the RLS-less drain worker.';

-- ============================================================================
-- END OF DOCSLOT SCHEMA
-- ============================================================================
-- Tables: 26 (D1-D26)
-- Total platform + docslot tables: 52
-- Next: 04_ruralreach.sql, 05_safeher.sql, 06_genericfirst.sql (optional)
-- ============================================================================


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 3/11: DocSlot Product complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 4/11: Security Hardening
-- Encryption, audit chain, RLS, anomaly detection — platform.*
-- Source: database/05_security_hardening.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 4/11: Security Hardening: % ---', '05_security_hardening.sql';
END $section$;

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


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 4/11: Security Hardening complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 5/11: AI Services
-- LangGraph, embeddings, OCR, predictions — ai.*
-- Source: database/06_ai_services.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 5/11: AI Services: % ---', '06_ai_services.sql';
END $section$;

-- ============================================================================
-- DocSlot Platform Database — Part 6: AI Services Schema
-- ============================================================================
-- This adds tables for AI-powered features. Owned by the Python AI service
-- (DocSlot.AI) but accessible to .NET via shared schema.
--
-- WHAT GOES IN THE PYTHON SERVICE (not in this SQL file):
-- - LangGraph workflows (multi-agent triage, prescription review, RAG)
-- - LangChain RAG pipelines (medical history Q&A)
-- - pandas / scikit-learn for analytics (no-show prediction, demand forecasting)
-- - Embeddings generation (Claude, OpenAI, Gemini, local models)
-- - OCR pipelines (PaddleOCR, Tesseract for lab reports, prescriptions)
-- - Speech-to-text (Whisper for voice notes in WhatsApp)
--
-- WHAT GOES IN THIS SQL FILE:
-- - Tables that PERSIST AI state and outputs (workflows, agent runs, embeddings)
-- - Tables that RBAC-control AI access (AI permissions, model configs)
-- - Tables that AUDIT AI actions (every AI inference logged)
-- - Tables that link AI outputs back to DocSlot domain (e.g., AI-generated
--   prescription suggestions linked to docslot.prescriptions)
--
-- DESIGN PRINCIPLES:
-- 1. AI is a service, not a feature — has its own schema (ai.*)
-- 2. Every AI inference is audited like any other data access
-- 3. AI outputs are advisory by default — humans approve before action
-- 4. Patient data sent to AI requires explicit consent (DPDP Section 6)
-- 5. AI models are configurable per-tenant (some prefer Claude, some prefer
--    Azure OpenAI, some prefer on-prem models for data sovereignty)
-- 6. Embeddings stored encrypted (they can be reverse-engineered to leak PII)
--
-- EXECUTION:
--   psql -d docslot_platform -f 06_ai_services.sql
--
-- Runs after 05_security_hardening.sql.
-- ============================================================================

CREATE SCHEMA IF NOT EXISTS ai;
COMMENT ON SCHEMA ai IS 'AI/ML services schema — owned by DocSlot.AI Python service. RBAC and audit through platform.*';

-- Enable pgvector extension for embeddings storage and similarity search
-- (Available in PostgreSQL 16+ with the pgvector extension installed)
-- If pgvector is not available, fall back to BYTEA storage and external vector DB.
DO $$
BEGIN
    BEGIN
        CREATE EXTENSION IF NOT EXISTS vector;
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'pgvector extension not available — falling back to BYTEA storage. Install pgvector for native vector ops.';
    END;
END $$;

-- ============================================================================
-- TABLE AI1: AI_MODEL_CONFIGS (which AI providers each tenant uses)
-- ============================================================================
-- Tenants may have data sovereignty requirements: some hospitals require
-- on-premise models, some allow Claude/OpenAI cloud, some specifically
-- want Azure OpenAI for HIPAA-compliant zones. This table is the central
-- registry of model configurations.
CREATE TABLE ai.ai_model_configs (
    config_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform default config
    use_case            VARCHAR(50) NOT NULL,                     -- 'triage', 'rag_medical', 'prescription_review', 'no_show_prediction', 'ocr_lab_report'
    provider            VARCHAR(30) NOT NULL CHECK (provider IN ('anthropic', 'openai', 'azure_openai', 'google_vertex', 'aws_bedrock', 'local_ollama', 'on_prem_llama', 'cohere')),
    model_name          VARCHAR(100) NOT NULL,                    -- 'claude-opus-4-7', 'gpt-4o', 'gemini-2-pro', 'llama-3-70b'
    endpoint_url        VARCHAR(500),                              -- For self-hosted models
    credential_reference VARCHAR(500),                              -- KMS reference, not the key itself
    max_tokens          INT DEFAULT 4000,
    temperature         DECIMAL(3,2) DEFAULT 0.0,
    system_prompt_template TEXT,
    cost_per_1k_input_tokens DECIMAL(10,6),                       -- For cost tracking
    cost_per_1k_output_tokens DECIMAL(10,6),

    -- Compliance flags
    data_residency_region VARCHAR(10),                            -- 'IN', 'US', 'EU' — where inference runs
    bao_signed          BOOLEAN NOT NULL DEFAULT false,           -- Business Associate Agreement signed?
    allows_phi          BOOLEAN NOT NULL DEFAULT false,           -- Can this model receive PHI?
    requires_consent    BOOLEAN NOT NULL DEFAULT true,            -- Patient consent required for use

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_default_for_use_case BOOLEAN NOT NULL DEFAULT false,

    -- Audit
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata            JSONB NOT NULL DEFAULT '{}',

    UNIQUE(tenant_id, use_case, provider, model_name)
);

CREATE INDEX idx_ai_configs_use_case ON ai.ai_model_configs(use_case, tenant_id) WHERE is_active = true;
CREATE INDEX idx_ai_configs_default ON ai.ai_model_configs(use_case) WHERE is_default_for_use_case = true AND is_active = true;

CREATE TRIGGER trg_ai_configs_updated_at BEFORE UPDATE ON ai.ai_model_configs
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE AI2: AI_WORKFLOWS (LangGraph workflow definitions)
-- ============================================================================
-- A LangGraph workflow is a stateful directed graph of LLM/tool calls.
-- This table stores workflow definitions versioned and tenant-scoped.
-- Examples:
-- - Triage workflow: WhatsApp message → intent classify → severity assess → route
-- - Prescription review: prescription → drug interaction check → allergy check → safety report
-- - RAG over medical history: question → retrieve consents → fetch records → answer
CREATE TABLE ai.ai_workflows (
    workflow_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_key        VARCHAR(100) NOT NULL,                    -- 'symptom_triage_v2', 'prescription_safety_check'
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform-wide workflow
    name                VARCHAR(200) NOT NULL,
    description         TEXT,
    version             INT NOT NULL DEFAULT 1,
    use_case            VARCHAR(50) NOT NULL,                     -- Maps to ai_model_configs.use_case

    -- LangGraph workflow definition (serialized)
    graph_definition    JSONB NOT NULL,                           -- Nodes, edges, conditions
    initial_state_schema JSONB,                                    -- Pydantic schema for initial state
    output_schema       JSONB,                                    -- Expected output structure

    -- Execution config
    timeout_seconds     INT NOT NULL DEFAULT 30,
    max_iterations      INT NOT NULL DEFAULT 10,                  -- For agent loops
    requires_human_approval BOOLEAN NOT NULL DEFAULT false,       -- Output needs human review before action

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT false,           -- Workflows start inactive until tested
    activated_at        TIMESTAMPTZ,
    activated_by_user_id UUID REFERENCES platform.users(user_id),
    deprecated_at       TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(workflow_key, version, tenant_id)
);

CREATE INDEX idx_workflows_active ON ai.ai_workflows(workflow_key, tenant_id) WHERE is_active = true;
CREATE INDEX idx_workflows_use_case ON ai.ai_workflows(use_case) WHERE is_active = true;

CREATE TRIGGER trg_workflows_updated_at BEFORE UPDATE ON ai.ai_workflows
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE AI3: AI_AGENT_RUNS (every workflow execution logged)
-- ============================================================================
-- Each time a LangGraph workflow executes, we log the run for audit, debugging,
-- and cost tracking. Includes full input/output for replay and offline eval.
CREATE TABLE ai.ai_agent_runs (
    run_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_id         UUID NOT NULL REFERENCES ai.ai_workflows(workflow_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),

    -- Context (who/what triggered this)
    triggered_by_user_id UUID REFERENCES platform.users(user_id),
    triggered_by_event  VARCHAR(100),                              -- 'whatsapp.message.received', 'booking.created'
    related_resource_type VARCHAR(50),                              -- 'booking', 'patient', 'prescription'
    related_resource_id UUID,
    correlation_id      VARCHAR(100),                              -- For tracing across services

    -- Input
    input_data          JSONB NOT NULL,                            -- Initial state passed to workflow
    input_token_count   INT,

    -- Execution
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'running', 'awaiting_human', 'success', 'failed', 'timeout', 'cancelled')),
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    duration_ms         INT,
    iterations_used     INT,

    -- Output
    output_data         JSONB,
    output_token_count  INT,
    confidence_score    DECIMAL(4,3),                              -- 0.000 to 1.000

    -- Cost tracking
    total_cost_usd      DECIMAL(10,6),
    models_used         JSONB,                                     -- [{model, input_tokens, output_tokens, cost}]

    -- Human approval (when required)
    human_approval_required BOOLEAN NOT NULL DEFAULT false,
    approved_at         TIMESTAMPTZ,
    approved_by_user_id UUID REFERENCES platform.users(user_id),
    approval_notes      TEXT,
    rejected_at         TIMESTAMPTZ,
    rejected_by_user_id UUID REFERENCES platform.users(user_id),
    rejection_reason    TEXT,

    -- Errors
    error_code          VARCHAR(50),
    error_message       TEXT,
    failed_at_node      VARCHAR(100),                              -- Which workflow node failed

    -- Compliance (DPDP)
    patient_consent_id  UUID REFERENCES docslot.abdm_consents(consent_id),  -- If PHI was used
    data_classes_accessed VARCHAR(50)[]                            -- What data classes the AI saw
);

CREATE INDEX idx_runs_workflow ON ai.ai_agent_runs(workflow_id, started_at DESC);
CREATE INDEX idx_runs_tenant ON ai.ai_agent_runs(tenant_id, started_at DESC);
CREATE INDEX idx_runs_pending_approval ON ai.ai_agent_runs(tenant_id, started_at) WHERE status = 'awaiting_human';
CREATE INDEX idx_runs_failed ON ai.ai_agent_runs(workflow_id, started_at DESC) WHERE status = 'failed';
CREATE INDEX idx_runs_resource ON ai.ai_agent_runs(related_resource_type, related_resource_id);
CREATE INDEX idx_runs_correlation ON ai.ai_agent_runs(correlation_id) WHERE correlation_id IS NOT NULL;

COMMENT ON TABLE ai.ai_agent_runs IS 'Every LangGraph workflow run logged. Partition by month at scale.';

-- ============================================================================
-- TABLE AI4: AI_AGENT_STEPS (individual node executions within a workflow run)
-- ============================================================================
-- For debugging and observability — each node in a LangGraph workflow logs here.
CREATE TABLE ai.ai_agent_steps (
    step_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id              UUID NOT NULL REFERENCES ai.ai_agent_runs(run_id) ON DELETE CASCADE,
    step_number         INT NOT NULL,
    node_name           VARCHAR(100) NOT NULL,                    -- LangGraph node identifier
    step_type           VARCHAR(30) NOT NULL CHECK (step_type IN ('llm_call', 'tool_call', 'condition', 'human_input', 'parallel', 'aggregator')),

    -- LLM call details (if step_type = 'llm_call')
    model_used          VARCHAR(100),
    prompt              TEXT,                                      -- Full prompt sent
    response            TEXT,                                      -- Full response received
    input_tokens        INT,
    output_tokens       INT,
    cost_usd            DECIMAL(10,6),

    -- Tool call details (if step_type = 'tool_call')
    tool_name           VARCHAR(100),
    tool_input          JSONB,
    tool_output         JSONB,

    -- Timing
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    duration_ms         INT,

    -- Outcome
    success             BOOLEAN NOT NULL DEFAULT true,
    error_message       TEXT,

    -- State snapshot after step
    state_snapshot      JSONB,                                     -- LangGraph state after this node
    UNIQUE(run_id, step_number)
);

CREATE INDEX idx_steps_run ON ai.ai_agent_steps(run_id, step_number);
CREATE INDEX idx_steps_failed ON ai.ai_agent_steps(run_id) WHERE success = false;
CREATE INDEX idx_steps_expensive ON ai.ai_agent_steps(cost_usd DESC, started_at DESC) WHERE cost_usd IS NOT NULL;

-- ============================================================================
-- TABLE AI5: AI_PROMPTS (versioned prompt library)
-- ============================================================================
-- Centralized prompt management. Treats prompts as code: versioned, tested,
-- with rollback capability. Critical for medical applications where prompt
-- changes can affect patient safety.
CREATE TABLE ai.ai_prompts (
    prompt_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prompt_key          VARCHAR(100) NOT NULL,                    -- 'symptom_triage_system', 'prescription_safety_check'
    version             INT NOT NULL DEFAULT 1,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform-wide prompt

    -- Content
    system_prompt       TEXT NOT NULL,
    user_prompt_template TEXT,                                     -- With {variables}
    expected_variables  VARCHAR(100)[],

    -- Metadata
    use_case            VARCHAR(50) NOT NULL,
    description         TEXT,
    tested_on_models    VARCHAR(100)[],                            -- Models this was tested with

    -- Safety
    pii_handling_notes  TEXT,                                       -- How PII is masked in this prompt
    medical_safety_review_required BOOLEAN NOT NULL DEFAULT false,
    medical_review_by_user_id UUID REFERENCES platform.users(user_id),
    medical_review_at   TIMESTAMPTZ,

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT false,
    activated_at        TIMESTAMPTZ,
    deprecated_at       TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    UNIQUE(prompt_key, version, tenant_id)
);

CREATE INDEX idx_prompts_active ON ai.ai_prompts(prompt_key, tenant_id) WHERE is_active = true;

-- ============================================================================
-- TABLE AI6: EMBEDDINGS (vector store for RAG over medical history)
-- ============================================================================
-- Stores document embeddings for retrieval-augmented generation.
-- Embeddings encrypted at rest because they can leak PII through inversion attacks.
--
-- pgvector: if available, uses vector type natively. Otherwise BYTEA fallback.
-- For large deployments, consider external vector DB (Pinecone, Weaviate, Qdrant)
-- and use this table only as a registry/metadata layer.
CREATE TABLE ai.embeddings (
    embedding_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    -- Source document
    source_type         VARCHAR(50) NOT NULL,                     -- 'prescription', 'lab_report', 'medical_history_note', 'patient_query'
    source_id           UUID NOT NULL,                             -- Reference to the source row
    chunk_index         INT NOT NULL DEFAULT 0,                   -- For chunked documents
    chunk_text          TEXT,                                      -- The text that was embedded (encrypted)
    chunk_text_hash     VARCHAR(64),                               -- For deduplication

    -- Embedding
    embedding_model     VARCHAR(100) NOT NULL,                     -- 'voyage-2', 'text-embedding-3-large', 'cohere-embed-v3'
    embedding_dimensions INT NOT NULL,                             -- 1024, 1536, 3072
    embedding_vector    BYTEA,                                     -- Encrypted vector (use vector type if pgvector available)

    -- Metadata for filtering during retrieval
    metadata            JSONB NOT NULL DEFAULT '{}',              -- {patient_id, date, doctor_id, category}
    patient_id          UUID REFERENCES docslot.patients(patient_id),
                                                                   -- Denormalized for fast filtering

    -- Encryption
    encryption_key_id   UUID REFERENCES platform.encryption_keys(key_id),

    -- Lifecycle
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ,                               -- For TTL on patient-query embeddings
    deleted_at          TIMESTAMPTZ                                -- For DPDP erasure
);

CREATE INDEX idx_embeddings_source ON ai.embeddings(source_type, source_id);
CREATE INDEX idx_embeddings_patient ON ai.embeddings(patient_id) WHERE patient_id IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX idx_embeddings_tenant ON ai.embeddings(tenant_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_embeddings_dedup ON ai.embeddings(chunk_text_hash);
CREATE INDEX idx_embeddings_expires ON ai.embeddings(expires_at) WHERE expires_at IS NOT NULL AND deleted_at IS NULL;

-- If pgvector available, add vector similarity index
-- CREATE INDEX idx_embeddings_vector ON ai.embeddings USING ivfflat (embedding_vector vector_cosine_ops);

COMMENT ON TABLE ai.embeddings IS 'Vector embeddings for RAG. Encrypted at rest. Consider external vector DB for >10M vectors.';

-- ============================================================================
-- TABLE AI7: AI_KNOWLEDGE_BASES (registry of RAG knowledge sources)
-- ============================================================================
-- A knowledge base is a collection of documents indexed for retrieval.
-- Examples: medical drug interaction database, hospital protocols, FAQ.
CREATE TABLE ai.ai_knowledge_bases (
    kb_id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    kb_key              VARCHAR(100) NOT NULL,                    -- 'drug_interactions', 'hospital_protocols', 'patient_history'
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform-wide KB
    name                VARCHAR(200) NOT NULL,
    description         TEXT,
    source_type         VARCHAR(50) NOT NULL,                     -- 'medical_database', 'tenant_documents', 'patient_records'
    embedding_model     VARCHAR(100) NOT NULL,
    document_count      INT NOT NULL DEFAULT 0,
    last_indexed_at     TIMESTAMPTZ,

    -- Access control
    requires_consent    BOOLEAN NOT NULL DEFAULT false,           -- e.g., patient KB needs patient consent
    permission_required VARCHAR(150),                              -- RBAC permission to query this KB

    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(kb_key, tenant_id)
);

-- ============================================================================
-- TABLE AI8: AI_FEEDBACK (user feedback on AI outputs for improvement)
-- ============================================================================
-- Critical for medical AI: doctors need to flag wrong AI outputs.
-- This feeds back into model evaluation and prompt tuning.
CREATE TABLE ai.ai_feedback (
    feedback_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id              UUID NOT NULL REFERENCES ai.ai_agent_runs(run_id) ON DELETE CASCADE,
    user_id             UUID REFERENCES platform.users(user_id),
    feedback_type       VARCHAR(30) NOT NULL CHECK (feedback_type IN ('correct', 'incorrect', 'partial', 'irrelevant', 'unsafe', 'biased')),
    severity            VARCHAR(20) CHECK (severity IS NULL OR severity IN ('low', 'medium', 'high', 'critical')),
    notes               TEXT,
    suggested_correct_output JSONB,                                -- What it should have said
    requires_immediate_action BOOLEAN NOT NULL DEFAULT false,
    triggered_workflow_rollback BOOLEAN NOT NULL DEFAULT false,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_feedback_run ON ai.ai_feedback(run_id);
CREATE INDEX idx_feedback_critical ON ai.ai_feedback(severity, created_at DESC) WHERE severity IN ('high', 'critical');
CREATE INDEX idx_feedback_unsafe ON ai.ai_feedback(created_at DESC) WHERE feedback_type IN ('unsafe', 'biased');

-- ============================================================================
-- TABLE AI9: AI_DOCUMENT_EXTRACTIONS (OCR + structured extraction results)
-- ============================================================================
-- For lab reports, prescriptions (paper), insurance cards.
-- Python service runs OCR + LLM extraction; results stored here for review.
CREATE TABLE ai.ai_document_extractions (
    extraction_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    -- Source document
    source_type         VARCHAR(50) NOT NULL,                     -- 'lab_report_pdf', 'paper_prescription', 'insurance_card'
    source_url          TEXT NOT NULL,
    source_mime_type    VARCHAR(100),
    source_size_bytes   BIGINT,

    -- Related domain entity (if known)
    related_booking_id  UUID REFERENCES docslot.bookings(booking_id),
    related_patient_id  UUID REFERENCES docslot.patients(patient_id),

    -- Extraction pipeline
    ocr_engine          VARCHAR(50),                               -- 'paddleocr', 'tesseract', 'google_vision', 'azure_document_intelligence'
    raw_ocr_text        TEXT,                                       -- Encrypted
    extraction_model    VARCHAR(100),                              -- LLM used for structured extraction
    extracted_data      JSONB,                                     -- Structured output

    -- Confidence and review
    overall_confidence  DECIMAL(4,3),                              -- 0.000 to 1.000
    requires_human_review BOOLEAN NOT NULL DEFAULT true,
    reviewed_by_user_id UUID REFERENCES platform.users(user_id),
    reviewed_at         TIMESTAMPTZ,
    review_status       VARCHAR(20) CHECK (review_status IS NULL OR review_status IN ('approved', 'rejected', 'corrected')),
    corrected_data      JSONB,                                     -- Human-corrected output (training data)

    -- Status
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'extracted', 'reviewed', 'approved', 'failed')),

    -- Encryption
    encryption_key_id   UUID REFERENCES platform.encryption_keys(key_id),

    -- Cost
    cost_usd            DECIMAL(10,6),

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_extractions_pending_review ON ai.ai_document_extractions(tenant_id, created_at)
    WHERE requires_human_review = true AND reviewed_at IS NULL;
CREATE INDEX idx_extractions_patient ON ai.ai_document_extractions(related_patient_id) WHERE related_patient_id IS NOT NULL;
CREATE INDEX idx_extractions_booking ON ai.ai_document_extractions(related_booking_id) WHERE related_booking_id IS NOT NULL;

CREATE TRIGGER trg_extractions_updated_at BEFORE UPDATE ON ai.ai_document_extractions
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE AI10: AI_PREDICTIONS (no-show, demand forecasting, risk scores)
-- ============================================================================
-- For non-LLM ML models: scikit-learn classifiers, XGBoost, etc.
-- These predictions feed dashboards and operational decisions.
CREATE TABLE ai.ai_predictions (
    prediction_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    model_name          VARCHAR(100) NOT NULL,                    -- 'no_show_classifier_v3', 'demand_forecast_lstm_v2'
    model_version       VARCHAR(50) NOT NULL,
    prediction_type     VARCHAR(50) NOT NULL CHECK (prediction_type IN ('no_show_probability', 'demand_forecast', 'patient_risk_score', 'churn_probability', 'fraud_score', 'cancellation_risk')),

    -- Subject
    related_resource_type VARCHAR(50),                              -- 'booking', 'patient', 'doctor'
    related_resource_id UUID,

    -- Prediction
    predicted_value     DECIMAL(10,4) NOT NULL,                    -- Probability or value
    confidence_interval JSONB,                                     -- {lower, upper}
    features_used       JSONB,                                     -- Feature snapshot at prediction time

    -- Outcome (filled in later for model evaluation)
    actual_outcome      DECIMAL(10,4),
    outcome_observed_at TIMESTAMPTZ,

    -- Validity
    valid_until         TIMESTAMPTZ,                               -- Predictions expire (booking-specific predictions)
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_predictions_resource ON ai.ai_predictions(related_resource_type, related_resource_id);
CREATE INDEX idx_predictions_type ON ai.ai_predictions(prediction_type, created_at DESC);
-- NOTE: predicate cannot use NOW() (must be IMMUTABLE). Queries should add
-- "AND valid_until < NOW()" themselves to find truly expired-but-unevaluated rows.
CREATE INDEX idx_predictions_unevaluated ON ai.ai_predictions(prediction_type) WHERE actual_outcome IS NULL;

-- ============================================================================
-- AI-SPECIFIC PERMISSIONS (registered with platform.permissions)
-- ============================================================================
DO $$
DECLARE
    ai_product_id UUID;
BEGIN
    -- Register AI as a "product" (it's a meta-product cutting across all)
    INSERT INTO platform.products (product_key, name, description, schema_name)
    VALUES ('ai', 'AI Services', 'AI/ML capabilities across all products', 'ai')
    ON CONFLICT (product_key) DO NOTHING
    RETURNING product_id INTO ai_product_id;

    IF ai_product_id IS NULL THEN
        SELECT product_id INTO ai_product_id FROM platform.products WHERE product_key = 'ai';
    END IF;

    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    -- Model configuration
    ('ai.models.read', ai_product_id, 'ai_model_configs', 'read', 'tenant', 'View AI model configurations', false),
    ('ai.models.manage', ai_product_id, 'ai_model_configs', 'update', 'tenant', 'Configure AI models for tenant', true),

    -- Workflows
    ('ai.workflows.read', ai_product_id, 'ai_workflows', 'read', 'tenant', 'View AI workflows', false),
    ('ai.workflows.create', ai_product_id, 'ai_workflows', 'create', 'tenant', 'Create custom workflows', true),
    ('ai.workflows.activate', ai_product_id, 'ai_workflows', 'update', 'tenant', 'Activate workflows', true),
    ('ai.workflows.execute', ai_product_id, 'ai_workflows', 'create', 'tenant', 'Trigger workflow execution', false),

    -- Runs and observability
    ('ai.runs.read', ai_product_id, 'ai_agent_runs', 'read', 'tenant', 'View workflow runs', false),
    ('ai.runs.approve', ai_product_id, 'ai_agent_runs', 'update', 'tenant', 'Approve runs requiring human approval', false),

    -- Prompts (sensitive — prompt changes affect patient safety)
    ('ai.prompts.read', ai_product_id, 'ai_prompts', 'read', 'tenant', 'View prompts', false),
    ('ai.prompts.create', ai_product_id, 'ai_prompts', 'create', 'tenant', 'Create new prompts', true),
    ('ai.prompts.medical_review', ai_product_id, 'ai_prompts', 'update', 'platform', 'Medically review prompts (qualified medical reviewer)', true),
    ('ai.prompts.activate', ai_product_id, 'ai_prompts', 'update', 'tenant', 'Activate reviewed prompts', true),

    -- Embeddings and KBs
    ('ai.embeddings.read', ai_product_id, 'embeddings', 'read', 'tenant', 'Query embeddings (search)', false),
    ('ai.kb.manage', ai_product_id, 'ai_knowledge_bases', 'update', 'tenant', 'Manage knowledge bases', false),

    -- Document extraction
    ('ai.documents.extract', ai_product_id, 'ai_document_extractions', 'create', 'tenant', 'Extract data from documents', false),
    ('ai.documents.review', ai_product_id, 'ai_document_extractions', 'update', 'tenant', 'Review AI extractions', false),

    -- Predictions
    ('ai.predictions.read', ai_product_id, 'ai_predictions', 'read', 'tenant', 'View ML predictions', false),

    -- Feedback
    ('ai.feedback.create', ai_product_id, 'ai_feedback', 'create', 'tenant', 'Submit feedback on AI outputs', false);

    -- Assign AI permissions to tenant roles
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_owner'
      AND p.product_id = ai_product_id
      AND p.is_dangerous = false;

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'doctor'
      AND p.permission_key IN ('ai.workflows.execute', 'ai.runs.read', 'ai.documents.extract', 'ai.documents.review', 'ai.feedback.create', 'ai.predictions.read', 'ai.embeddings.read');
END $$;

-- ============================================================================
-- VIEWS for AI observability
-- ============================================================================

-- Recent AI activity per tenant
CREATE OR REPLACE VIEW ai.v_recent_activity AS
SELECT
    r.tenant_id,
    t.display_name AS tenant_name,
    w.workflow_key,
    r.status,
    r.started_at,
    r.duration_ms,
    r.total_cost_usd,
    r.confidence_score
FROM ai.ai_agent_runs r
JOIN ai.ai_workflows w ON w.workflow_id = r.workflow_id
JOIN platform.tenants t ON t.tenant_id = r.tenant_id
WHERE r.started_at >= NOW() - INTERVAL '24 hours'
ORDER BY r.started_at DESC;

-- Cost summary per tenant per use case
CREATE OR REPLACE VIEW ai.v_cost_summary AS
SELECT
    r.tenant_id,
    w.use_case,
    DATE_TRUNC('day', r.started_at) AS day,
    COUNT(*) AS run_count,
    SUM(r.total_cost_usd) AS total_cost_usd,
    AVG(r.duration_ms) AS avg_duration_ms,
    COUNT(*) FILTER (WHERE r.status = 'success') AS success_count,
    COUNT(*) FILTER (WHERE r.status = 'failed') AS failed_count
FROM ai.ai_agent_runs r
JOIN ai.ai_workflows w ON w.workflow_id = r.workflow_id
WHERE r.started_at >= NOW() - INTERVAL '30 days'
GROUP BY r.tenant_id, w.use_case, DATE_TRUNC('day', r.started_at);

-- Workflows needing human attention
CREATE OR REPLACE VIEW ai.v_human_review_queue AS
SELECT
    'workflow_approval'::TEXT AS source,
    r.run_id AS item_id,
    r.tenant_id,
    w.workflow_key,
    r.started_at AS created_at,
    r.input_data,
    r.output_data,
    'Workflow awaiting approval'::TEXT AS reason
FROM ai.ai_agent_runs r
JOIN ai.ai_workflows w ON w.workflow_id = r.workflow_id
WHERE r.status = 'awaiting_human'

UNION ALL

SELECT
    'document_extraction'::TEXT AS source,
    de.extraction_id AS item_id,
    de.tenant_id,
    de.source_type AS workflow_key,
    de.created_at,
    NULL::JSONB AS input_data,
    de.extracted_data AS output_data,
    ('Document extraction confidence: ' || COALESCE(de.overall_confidence::TEXT, 'n/a'))::TEXT AS reason
FROM ai.ai_document_extractions de
WHERE de.requires_human_review = true AND de.reviewed_at IS NULL

ORDER BY created_at DESC;

-- ============================================================================
-- SEED: Default AI workflows (platform-wide, can be overridden per tenant)
-- ============================================================================

INSERT INTO ai.ai_workflows (workflow_key, name, description, use_case, graph_definition, requires_human_approval) VALUES
('symptom_triage_v1', 'Patient Symptom Triage', 'Classifies WhatsApp symptom messages into urgency tiers and routes to appropriate doctor specialization', 'triage',
'{"nodes": ["classify_intent", "assess_severity", "check_red_flags", "recommend_specialty", "format_response"], "edges": [["classify_intent", "assess_severity"], ["assess_severity", "check_red_flags"], ["check_red_flags", "recommend_specialty"], ["recommend_specialty", "format_response"]], "conditional_edges": [{"from": "check_red_flags", "condition": "is_emergency", "to": "emergency_escalation"}]}',
false),

('prescription_safety_check_v1', 'Prescription Safety Review', 'Cross-checks new prescription against patient allergies, current medications, and drug interactions', 'prescription_review',
'{"nodes": ["fetch_patient_history", "check_allergies", "check_interactions", "check_dosing", "generate_warnings"], "edges": [["fetch_patient_history", "check_allergies"], ["check_allergies", "check_interactions"], ["check_interactions", "check_dosing"], ["check_dosing", "generate_warnings"]]}',
true),

('medical_history_rag_v1', 'Patient Medical History Q&A', 'Doctor queries patient history in natural language; RAG retrieves relevant records with consent check', 'rag_medical',
'{"nodes": ["verify_consent", "retrieve_relevant_records", "decrypt_chunks", "synthesize_answer", "cite_sources"], "edges": [["verify_consent", "retrieve_relevant_records"], ["retrieve_relevant_records", "decrypt_chunks"], ["decrypt_chunks", "synthesize_answer"], ["synthesize_answer", "cite_sources"]]}',
false),

('lab_report_extraction_v1', 'Lab Report OCR & Extraction', 'Extracts structured test values from uploaded PDF/image lab reports', 'ocr_lab_report',
'{"nodes": ["ocr_pages", "detect_test_panel", "extract_values", "normalize_units", "flag_abnormal"], "edges": [["ocr_pages", "detect_test_panel"], ["detect_test_panel", "extract_values"], ["extract_values", "normalize_units"], ["normalize_units", "flag_abnormal"]]}',
true);

-- ============================================================================
-- AI DATA-PLANE RLS (Phase-0 gap fix) — tenant isolation on the PHI-bearing ai.*
-- tables, mirroring the PHI pattern in 05 (active tenant OR scoped impersonation;
-- the super_admin god-flag is intentionally NOT honored). Defense-in-depth:
-- TODAY the Python AI service connects as the DB owner (RLS-bypassing) and filters
-- by tenant in code, so these policies are a BACKSTOP for the .NET app role
-- (docslot_app, NOBYPASSRLS) and for the intended future state where the AI service
-- connects as a least-privilege role and SET LOCAL app.tenant_id per request
-- (see db/rls.py in the architecture note below).
-- ============================================================================
ALTER TABLE ai.embeddings              ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_predictions          ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_agent_runs           ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_document_extractions ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_agent_steps          ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_embeddings ON ai.embeddings
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_ai_predictions ON ai.ai_predictions
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_ai_agent_runs ON ai.ai_agent_runs
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_ai_document_extractions ON ai.ai_document_extractions
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

-- ai_agent_steps has no tenant_id; gate via the parent run.
CREATE POLICY tenant_isolation_ai_agent_steps ON ai.ai_agent_steps
    FOR ALL
    USING (
        EXISTS (
            SELECT 1 FROM ai.ai_agent_runs r
            WHERE r.run_id = ai_agent_steps.run_id
              AND (r.tenant_id = platform.current_tenant_id()
                   OR r.tenant_id = platform.current_impersonated_tenant())
        )
    );

-- ============================================================================
-- END OF AI SERVICES SCHEMA
-- ============================================================================
-- Tables: 10 (AI1-AI10)
-- Indexes: ~25
-- Views: 3
-- Permissions: 17 (registered in platform.permissions)
-- Workflows seeded: 4 (symptom_triage, prescription_safety, medical_history_rag, lab_report_extraction)
--
-- TOTAL DATABASE TABLES: 97 (was 87)
--
-- WHAT THE PYTHON SERVICE LOOKS LIKE (architecture, not in this SQL):
--
-- docslot-ai-service/
--   pyproject.toml          # FastAPI + LangGraph + LangChain + pandas
--   src/
--     api/
--       fastapi_app.py      # FastAPI with JWT auth (validates platform.user_sessions)
--       routes/             # /triage, /prescription-review, /rag-query, /extract-document
--     workflows/            # LangGraph workflow implementations
--       triage_graph.py     # Loaded from ai.ai_workflows table
--       prescription_safety.py
--       medical_rag.py
--     agents/               # Multi-agent orchestrators
--     embeddings/           # Embedding generation + retrieval
--     ocr/                  # PaddleOCR / Tesseract / Vision API pipelines
--     analytics/            # pandas + scikit-learn for predictions
--       no_show_model.py
--       demand_forecast.py
--     db/
--       session.py          # SQLAlchemy async sessions, same DB as .NET
--       audit.py            # Writes to platform.audit_log
--       rls.py              # Sets SET LOCAL app.tenant_id on every request
--     security/
--       kms.py              # Same KMS provider as .NET, shared encryption_keys
--   tests/
--   docker/
--     Dockerfile            # python:3.12-slim
--   k8s/
--     deployment.yaml       # Separate scaling from .NET
-- ============================================================================


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 5/11: AI Services complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 6/11: Commission & Broker
-- Broker referrals, commission, attribution, payouts — commission.*
-- Source: database/07_commission_broker.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 6/11: Commission & Broker: % ---', '07_commission_broker.sql';
END $section$;

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
    ('commission.broker.create_booking_self', commission_product_id, 'bookings', 'create', 'self', 'Broker self-service: book on behalf of a referred patient (patient consent OTP required)', false),

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
        'commission.broker.generate_link_self',
        'commission.broker.create_booking_self'
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


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 6/11: Commission & Broker complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 7/11: RBAC Enhancements
-- Backend-driven menus, overrides, fast permission resolver — platform.*
-- Source: database/08_rbac_navigation.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 7/11: RBAC Enhancements: % ---', '08_rbac_navigation.sql';
END $section$;

-- ============================================================================
-- DocSlot Platform Database — Part 8: RBAC Enhancements (Navigation + Overrides)
-- ============================================================================
-- Adopts the best ideas from the backend-driven authorization model:
--   1. Backend-driven navigation menus (frontend is a dumb consumer)
--   2. Menu-to-permission mapping (menus appear only if user has the permission)
--   3. Per-user permission overrides (grant/deny on top of role — the escape hatch)
--   4. ResourceTypes + ActionTypes lookup tables (optional, enables admin dropdowns)
--
-- WHAT THIS ADDS TO DOCSLOT'S EXISTING RBAC:
-- DocSlot already has: users, user_tenant_roles, roles, role_permissions,
-- permissions — and in a richer multi-tenant, product-namespaced form than
-- the classic model. This file fills the 3 genuine gaps + 2 optional lookups.
--
-- ADAPTED FOR DOCSLOT'S MULTI-TENANT MODEL:
-- - Menus are tenant_type-aware (individual_doctor sees different menus than
--   hospital than pathology_lab) AND product-scoped
-- - Overrides are per-tenant (a user has different roles per tenant, so their
--   override must also be scoped to a tenant)
-- - The permission resolution function now accounts for overrides:
--   effective = (role grants) MINUS (deny overrides) PLUS (grant overrides)
--
-- DEPENDENCIES:
-- Run AFTER 01_platform_core.sql (needs platform.permissions, roles, products)
--
-- EXECUTION:
-- psql -d docslot_platform -f 08_rbac_navigation.sql
-- ============================================================================

-- ============================================================================
-- TABLE R1: RESOURCE_TYPES (lookup — what can be acted upon)
-- ============================================================================
-- Optional normalization of the permissions.resource VARCHAR column.
-- Enables a dynamic admin UI: "pick a Resource from dropdown" instead of typing.
-- The VARCHAR column on permissions stays as the fast-path; this is the
-- canonical registry that populates UI and prevents typos.
CREATE TABLE platform.resource_types (
    resource_type_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    resource_key        VARCHAR(50) NOT NULL UNIQUE,         -- 'booking', 'patient', 'prescription', 'broker'
    resource_name       VARCHAR(100) NOT NULL,               -- 'Booking', 'Patient Record'
    product_id          UUID REFERENCES platform.products(product_id),
                                                              -- NULL = cross-product resource
    description         TEXT,
    display_order       INT NOT NULL DEFAULT 0,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_resource_types_product ON platform.resource_types(product_id) WHERE is_active = true;

COMMENT ON TABLE platform.resource_types IS 'Registry of resources (the "what" in permissions). Populates admin dropdowns; permissions.resource VARCHAR remains the fast-path.';

-- ============================================================================
-- TABLE R2: ACTION_TYPES (lookup — what can be done)
-- ============================================================================
CREATE TABLE platform.action_types (
    action_type_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    action_key          VARCHAR(30) NOT NULL UNIQUE,         -- 'create', 'read', 'update', 'delete', 'approve', 'export'
    action_name         VARCHAR(100) NOT NULL,               -- 'Create', 'View', 'Approve'
    description         TEXT,
    is_dangerous        BOOLEAN NOT NULL DEFAULT false,      -- 'delete', 'approve' tend to be dangerous
    display_order       INT NOT NULL DEFAULT 0,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE platform.action_types IS 'Registry of actions (the "what can be done" in permissions). Populates admin dropdowns.';

-- Optional FK columns on permissions (backward compatible — existing VARCHAR columns stay)
ALTER TABLE platform.permissions
    ADD COLUMN IF NOT EXISTS resource_type_id UUID REFERENCES platform.resource_types(resource_type_id),
    ADD COLUMN IF NOT EXISTS action_type_id UUID REFERENCES platform.action_types(action_type_id);

-- ============================================================================
-- TABLE R3: NAVIGATION_MENUS (backend-driven menu tree)
-- ============================================================================
-- The application's menu structure lives in the database, not hardcoded in the
-- frontend. When a user logs in, the backend returns the menu tree filtered to
-- what they're allowed to see. Add a feature = add menu rows, no frontend deploy.
--
-- Self-referencing for parent→child hierarchy (Dashboard > Bookings > Today).
CREATE TABLE platform.navigation_menus (
    menu_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_menu_id      UUID REFERENCES platform.navigation_menus(menu_id) ON DELETE CASCADE,
                                                              -- NULL = top-level menu

    -- Identity
    menu_key            VARCHAR(80) NOT NULL,                -- 'bookings', 'bookings.today', 'settings.brokers'
    menu_label          VARCHAR(120) NOT NULL,               -- 'Bookings' (default English)
    menu_label_hi       VARCHAR(120),                        -- Hindi label (bilingual UI)
    menu_icon           VARCHAR(80),                         -- Icon identifier for frontend
    menu_url            VARCHAR(255),                        -- Route path: '/bookings/today'

    -- DocSlot multi-tenant adaptations
    product_key         VARCHAR(50) NOT NULL DEFAULT 'docslot',  -- Which product this menu belongs to
    applies_to_tenant_types VARCHAR(30)[],                   -- ['hospital','pathology_lab'] or NULL = all
                                                              -- An individual_doctor sees fewer menus than a hospital

    -- Display
    display_order       INT NOT NULL DEFAULT 0,
    is_section_header   BOOLEAN NOT NULL DEFAULT false,      -- Group label, not clickable
    badge_source        VARCHAR(50),                         -- e.g., 'pending_bookings_count' for a notification badge
    opens_in_new_tab    BOOLEAN NOT NULL DEFAULT false,

    -- Lifecycle
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_system           BOOLEAN NOT NULL DEFAULT true,       -- System menus protected from tenant deletion
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                              -- NULL = global menu; set = tenant-custom menu

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(menu_key, tenant_id)
);

CREATE INDEX idx_nav_parent ON platform.navigation_menus(parent_menu_id) WHERE is_active = true;
CREATE INDEX idx_nav_product ON platform.navigation_menus(product_key, display_order) WHERE is_active = true;
CREATE INDEX idx_nav_tenant ON platform.navigation_menus(tenant_id) WHERE tenant_id IS NOT NULL;

CREATE TRIGGER trg_nav_updated_at BEFORE UPDATE ON platform.navigation_menus
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

COMMENT ON TABLE platform.navigation_menus IS 'Backend-driven menu tree. Frontend renders whatever the backend returns — no hardcoded menu logic.';

-- ============================================================================
-- TABLE R4: MENU_PERMISSIONS (which permission gates which menu item)
-- ============================================================================
-- A menu item is shown to a user only if they hold at least one of the
-- permissions mapped here. A menu with no mappings is visible to everyone
-- (e.g., "Dashboard" home). is_required AND semantics supported via require_all.
CREATE TABLE platform.menu_permissions (
    menu_permission_id  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    menu_id             UUID NOT NULL REFERENCES platform.navigation_menus(menu_id) ON DELETE CASCADE,
    permission_id       UUID NOT NULL REFERENCES platform.permissions(permission_id) ON DELETE CASCADE,
    require_all         BOOLEAN NOT NULL DEFAULT false,      -- false = ANY of mapped perms shows menu; true = ALL required
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(menu_id, permission_id)
);

CREATE INDEX idx_menu_perms_menu ON platform.menu_permissions(menu_id);
CREATE INDEX idx_menu_perms_permission ON platform.menu_permissions(permission_id);

COMMENT ON TABLE platform.menu_permissions IS 'Maps menus to the permissions that gate them. No mapping = visible to all authenticated users.';

-- ============================================================================
-- TABLE R5: USER_PERMISSION_OVERRIDES (per-user grant/deny escape hatch)
-- ============================================================================
-- The "exceptional case" mechanism. Instead of creating a custom role for every
-- one-off need, grant or deny a single permission to a single user.
--
-- Examples:
--   - Dr. Sharma (role: doctor) ALSO needs to approve refunds → GRANT override
--   - Receptionist Priya abused cancel-booking → DENY override (keeps rest of role)
--
-- DENY ALWAYS WINS: if a user has a permission via role but also has a deny
-- override, they do NOT have the permission. This is the safe default.
CREATE TABLE platform.user_permission_overrides (
    override_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID NOT NULL REFERENCES platform.users(user_id) ON DELETE CASCADE,
    permission_id       UUID NOT NULL REFERENCES platform.permissions(permission_id) ON DELETE CASCADE,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                              -- NULL = applies across all tenants (rare; platform-level)
                                                              -- Set = override only within this tenant (usual case)

    is_allowed          BOOLEAN NOT NULL,                    -- true = GRANT, false = DENY (deny wins over role grant)

    -- Why (audit trail — overrides are exceptional, must be justified)
    reason              TEXT NOT NULL,
    granted_by_user_id  UUID REFERENCES platform.users(user_id),

    -- Time-boxed overrides (e.g., temporary access during a colleague's leave)
    effective_from      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ,                         -- NULL = permanent until revoked

    -- Lifecycle
    is_active           BOOLEAN NOT NULL DEFAULT true,
    revoked_at          TIMESTAMPTZ,
    revoked_by_user_id  UUID REFERENCES platform.users(user_id),

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(user_id, permission_id, tenant_id)
);

CREATE INDEX idx_overrides_user ON platform.user_permission_overrides(user_id, tenant_id)
    WHERE is_active = true;
CREATE INDEX idx_overrides_permission ON platform.user_permission_overrides(permission_id)
    WHERE is_active = true;
CREATE INDEX idx_overrides_expiring ON platform.user_permission_overrides(expires_at)
    WHERE expires_at IS NOT NULL AND is_active = true;

CREATE TRIGGER trg_overrides_updated_at BEFORE UPDATE ON platform.user_permission_overrides
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

COMMENT ON TABLE platform.user_permission_overrides IS 'Per-user grant/deny on top of role permissions. DENY wins. Time-boxable. Every override requires a reason.';

-- ============================================================================
-- UPDATED PERMISSION RESOLUTION (now accounts for overrides)
-- ============================================================================
-- The new effective-permission logic:
--   1. Start with permissions granted via the user's roles (in the tenant)
--   2. ADD any grant-overrides (is_allowed = true)
--   3. REMOVE any deny-overrides (is_allowed = false) — deny always wins
-- Expired or revoked overrides are ignored.

CREATE OR REPLACE FUNCTION platform.user_has_permission(
    p_user_id UUID,
    p_permission_key VARCHAR,
    p_tenant_id UUID DEFAULT NULL
) RETURNS BOOLEAN AS $$
    -- Single-query resolution. An override (if any) wins; deny beats grant when
    -- both a tenant-scoped and a global override exist (ORDER BY is_allowed ASC
    -- puts false/deny first). With no override, fall back to role grant.
    -- Written as SQL (not PL/pgSQL) so the planner can inline it.
    WITH ovr AS (
        SELECT o.is_allowed
        FROM platform.user_permission_overrides o
        JOIN platform.permissions p ON p.permission_id = o.permission_id
        WHERE o.user_id = p_user_id
          AND p.permission_key = p_permission_key
          AND o.is_active = true
          AND (o.tenant_id = p_tenant_id OR o.tenant_id IS NULL)
          AND o.effective_from <= NOW()
          AND (o.expires_at IS NULL OR o.expires_at > NOW())
        ORDER BY o.is_allowed ASC   -- deny (false) wins over grant (true)
        LIMIT 1
    )
    SELECT CASE
        WHEN EXISTS (SELECT 1 FROM ovr)
            THEN (SELECT is_allowed FROM ovr)
        ELSE EXISTS (
            SELECT 1 FROM platform.v_user_permissions
            WHERE user_id = p_user_id
              AND permission_key = p_permission_key
              AND (tenant_id = p_tenant_id OR scope = 'platform')
        )
    END;
$$ LANGUAGE SQL STABLE;

COMMENT ON FUNCTION platform.user_has_permission IS 'Effective permission check: deny-override > grant-override > role grant. Time-boxed overrides respected.';

-- ============================================================================
-- FUNCTION: resolve_user_permissions (single-query effective permission set)
-- ============================================================================
-- PERFORMANCE-CRITICAL. Resolves a user's COMPLETE effective permission set in
-- ONE query: role grants, minus deny-overrides, plus grant-overrides.
--
-- Use this instead of calling user_has_permission() in a loop. The backend
-- should call this ONCE per request (or cache per session) and check the
-- returned set in memory, rather than hitting the DB per permission check.
CREATE OR REPLACE FUNCTION platform.resolve_user_permissions(
    p_user_id UUID,
    p_tenant_id UUID DEFAULT NULL
) RETURNS TABLE(permission_key VARCHAR) AS $$
    WITH active_overrides AS (
        -- All currently-effective overrides for this user/tenant, resolved once
        SELECT p.permission_key, o.is_allowed
        FROM platform.user_permission_overrides o
        JOIN platform.permissions p ON p.permission_id = o.permission_id
        WHERE o.user_id = p_user_id
          AND o.is_active = true
          AND (o.tenant_id = p_tenant_id OR o.tenant_id IS NULL)
          AND o.effective_from <= NOW()
          AND (o.expires_at IS NULL OR o.expires_at > NOW())
    ),
    denies AS (
        SELECT permission_key FROM active_overrides WHERE is_allowed = false
    ),
    role_grants AS (
        SELECT DISTINCT vup.permission_key
        FROM platform.v_user_permissions vup
        WHERE vup.user_id = p_user_id
          AND (vup.tenant_id = p_tenant_id OR vup.scope = 'platform')
    )
    -- Role grants not denied by an override
    SELECT rg.permission_key
    FROM role_grants rg
    WHERE rg.permission_key NOT IN (SELECT permission_key FROM denies)
    UNION
    -- Plus explicit grant-overrides (deny can't co-exist: UNIQUE per user/perm/tenant)
    SELECT ao.permission_key
    FROM active_overrides ao
    WHERE ao.is_allowed = true
      AND ao.permission_key NOT IN (SELECT permission_key FROM denies);
$$ LANGUAGE SQL STABLE;

COMMENT ON FUNCTION platform.resolve_user_permissions IS 'Single-query effective permission set (role grants - deny-overrides + grant-overrides). Call once per request; check in memory. Performant basis for get_user_menus and API authz.';

-- ============================================================================
-- FUNCTION: get_user_menus (returns the menu tree a user is allowed to see)
-- ============================================================================
-- The backend calls this on login. Returns only menus the user can access,
-- filtered by their permissions AND the tenant's tenant_type.
-- Frontend renders whatever comes back — zero hardcoded menu logic.
--
-- PERFORMANT: resolves the user's permission set ONCE (CTE) then joins menus
-- against it — no per-menu function calls.
CREATE OR REPLACE FUNCTION platform.get_user_menus(
    p_user_id UUID,
    p_tenant_id UUID,
    p_tenant_type VARCHAR DEFAULT NULL,
    p_product_key VARCHAR DEFAULT 'docslot'
) RETURNS TABLE(
    menu_id UUID,
    parent_menu_id UUID,
    menu_key VARCHAR,
    menu_label VARCHAR,
    menu_label_hi VARCHAR,
    menu_icon VARCHAR,
    menu_url VARCHAR,
    display_order INT,
    is_section_header BOOLEAN,
    badge_source VARCHAR
) AS $$
    WITH user_perms AS (
        -- Resolve the full effective permission set ONCE
        SELECT rp.permission_key
        FROM platform.resolve_user_permissions(p_user_id, p_tenant_id) rp
    )
    SELECT
        m.menu_id, m.parent_menu_id, m.menu_key, m.menu_label, m.menu_label_hi,
        m.menu_icon, m.menu_url, m.display_order, m.is_section_header, m.badge_source
    FROM platform.navigation_menus m
    WHERE m.is_active = true
      AND m.product_key = p_product_key
      -- Tenant-type filter: menu applies if it targets all types OR this tenant's type
      AND (m.applies_to_tenant_types IS NULL
           OR p_tenant_type IS NULL
           OR p_tenant_type = ANY(m.applies_to_tenant_types))
      -- Tenant filter: global menus OR this tenant's custom menus
      AND (m.tenant_id IS NULL OR m.tenant_id = p_tenant_id)
      -- Permission filter: menu has no gating perms, OR user holds a gating perm.
      -- Joins against the pre-resolved set — no per-menu function calls.
      AND (
          NOT EXISTS (SELECT 1 FROM platform.menu_permissions mp WHERE mp.menu_id = m.menu_id)
          OR EXISTS (
              SELECT 1 FROM platform.menu_permissions mp
              JOIN platform.permissions p ON p.permission_id = mp.permission_id
              WHERE mp.menu_id = m.menu_id
                AND p.permission_key IN (SELECT permission_key FROM user_perms)
          )
      )
    ORDER BY m.display_order, m.menu_label;
$$ LANGUAGE SQL STABLE;

COMMENT ON FUNCTION platform.get_user_menus IS 'Returns the menu tree a user can see, filtered by permissions + tenant_type. Backend calls on login; frontend just renders.';

-- ============================================================================
-- SEED: ResourceTypes and ActionTypes from existing permissions
-- ============================================================================
-- Backfill the lookup tables from distinct values already in platform.permissions
INSERT INTO platform.resource_types (resource_key, resource_name)
SELECT DISTINCT resource, INITCAP(REPLACE(resource, '_', ' '))
FROM platform.permissions
WHERE resource IS NOT NULL
ON CONFLICT (resource_key) DO NOTHING;

INSERT INTO platform.action_types (action_key, action_name, is_dangerous)
SELECT DISTINCT action, INITCAP(action),
    action IN ('delete', 'approve', 'suspend', 'impersonate', 'destroy', 'execute', 'rotate')
FROM platform.permissions
WHERE action IS NOT NULL
ON CONFLICT (action_key) DO NOTHING;

-- Backfill the FK columns on permissions
UPDATE platform.permissions p
SET resource_type_id = rt.resource_type_id
FROM platform.resource_types rt
WHERE p.resource = rt.resource_key AND p.resource_type_id IS NULL;

UPDATE platform.permissions p
SET action_type_id = at.action_type_id
FROM platform.action_types at
WHERE p.action = at.action_key AND p.action_type_id IS NULL;

-- ============================================================================
-- SEED: DocSlot navigation menus (the actual app menu structure)
-- ============================================================================
-- Top-level menus, then children. Permissions gate visibility.
-- This block is the canonical menu tree the frontends render. It covers the full
-- set of screens mocked by the admin/staff SPA: Overview, Bookings (+children),
-- Calendar, Doctors, Patients (+clinical), Analytics, Team & Roles, Settings
-- (+children), Developers/API, Security & Compliance, Care Partners.
--
-- Conventions:
--   * Every label is bilingual (menu_label / menu_label_hi) per the bilingual rule.
--   * applies_to_tenant_types scopes a menu to the tenant types that need it
--     (an individual_doctor sees fewer menus than a hospital; lab-only screens
--     target pathology_lab/diagnostic_center). NULL = all tenant types.
--   * badge_source names a backend-computed counter the FE renders as a badge.
--   * menu_permissions gate visibility: a menu with no mapping is visible to all
--     authenticated users; otherwise the user must hold one of the mapped perms.
--   * "Care Partners" is the customer-facing label for the broker/referral program
--     (MCI 6.4 — never surfaced to patients as "commission/broker").
DO $seed_menus$
DECLARE
    m_dashboard UUID;
    m_bookings UUID;
    m_calendar UUID;
    m_patients UUID;
    m_doctors UUID;
    m_lab UUID;
    m_analytics UUID;
    m_care_partners UUID;
    m_team UUID;
    m_developers UUID;
    m_security UUID;
    m_settings UUID;
BEGIN
    -- ---- Top-level menus ----------------------------------------------------
    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('dashboard', 'Overview', 'अवलोकन', 'home', '/dashboard', 10, NULL)
    RETURNING menu_id INTO m_dashboard;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types, badge_source)
    VALUES ('bookings', 'Bookings', 'अपॉइंटमेंट', 'calendar-check', '/bookings', 20, NULL, 'pending_bookings_count')
    RETURNING menu_id INTO m_bookings;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('calendar', 'Calendar', 'कैलेंडर', 'calendar', '/calendar', 30, NULL)
    RETURNING menu_id INTO m_calendar;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('patients', 'Patients', 'मरीज़', 'users', '/patients', 40, NULL)
    RETURNING menu_id INTO m_patients;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('doctors', 'Doctors', 'डॉक्टर', 'stethoscope', '/doctors', 50, ARRAY['hospital','clinic','diagnostic_center'])
    RETURNING menu_id INTO m_doctors;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('lab', 'Lab Tests', 'लैब टेस्ट', 'flask', '/lab', 60, ARRAY['pathology_lab','hospital','diagnostic_center'])
    RETURNING menu_id INTO m_lab;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('analytics', 'Analytics', 'विश्लेषण', 'chart', '/analytics', 70, NULL)
    RETURNING menu_id INTO m_analytics;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('care_partners', 'Care Partners', 'केयर पार्टनर', 'handshake', '/care-partners', 80, NULL)
    RETURNING menu_id INTO m_care_partners;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('team', 'Team & Roles', 'टीम और भूमिकाएँ', 'user-cog', '/team', 90, NULL)
    RETURNING menu_id INTO m_team;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('developers', 'Developers', 'डेवलपर्स', 'code', '/developers', 100, NULL)
    RETURNING menu_id INTO m_developers;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('security', 'Security & Compliance', 'सुरक्षा और अनुपालन', 'shield', '/security', 110, NULL)
    RETURNING menu_id INTO m_security;

    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('settings', 'Settings', 'सेटिंग्स', 'gear', '/settings', 120, NULL)
    RETURNING menu_id INTO m_settings;

    -- ---- Children -----------------------------------------------------------
    INSERT INTO platform.navigation_menus (parent_menu_id, menu_key, menu_label, menu_label_hi, menu_url, display_order, badge_source) VALUES
    (m_bookings, 'bookings.today', 'Today', 'आज', '/bookings/today', 1, 'today_bookings_count'),
    (m_bookings, 'bookings.upcoming', 'Upcoming', 'आगामी', '/bookings/upcoming', 2, NULL),
    (m_bookings, 'bookings.history', 'History', 'इतिहास', '/bookings/history', 3, NULL);

    -- Patient clinical sub-screen (PHI — gated separately below on patient.read)
    INSERT INTO platform.navigation_menus (parent_menu_id, menu_key, menu_label, menu_label_hi, menu_url, display_order) VALUES
    (m_patients, 'patients.clinical', 'Clinical Records', 'क्लिनिकल रिकॉर्ड', '/patients/clinical', 1);

    -- Care Partners children (customer-facing names for broker/commission program)
    INSERT INTO platform.navigation_menus (parent_menu_id, menu_key, menu_label, menu_label_hi, menu_url, display_order) VALUES
    (m_care_partners, 'care_partners.directory', 'Partner Directory', 'पार्टनर निर्देशिका', '/care-partners/directory', 1),
    (m_care_partners, 'care_partners.payouts', 'Payouts', 'भुगतान', '/care-partners/payouts', 2);

    -- Settings children
    INSERT INTO platform.navigation_menus (parent_menu_id, menu_key, menu_label, menu_label_hi, menu_url, display_order) VALUES
    (m_settings, 'settings.brokers', 'Partner Commission', 'पार्टनर कमीशन', '/settings/commission', 10),
    (m_settings, 'settings.users', 'Users & Roles', 'उपयोगकर्ता', '/settings/users', 20),
    (m_settings, 'settings.tenant', 'Organization', 'संगठन', '/settings/organization', 30);

    -- ---- Menu → permission gates -------------------------------------------
    -- Helper pattern: map a menu to a permission only if that permission exists.
    -- bookings + children + calendar → docslot.booking.read (tenant-wide view).
    -- ANY-of semantics (require_all=false default): we ALSO map booking.read_self
    -- so a doctor (who holds the self-scoped read, not the tenant-wide one) still
    -- sees Bookings/Calendar for their own schedule. The menu is shown if the user
    -- holds EITHER permission.
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m.menu_id, p.permission_id
    FROM platform.navigation_menus m
    JOIN platform.permissions p ON p.permission_key IN ('docslot.booking.read', 'docslot.booking.read_self')
    WHERE m.menu_key IN ('bookings','bookings.today','bookings.upcoming','bookings.history','calendar')
    ON CONFLICT DO NOTHING;

    -- patients + clinical → docslot.patient.read
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m.menu_id, p.permission_id
    FROM platform.navigation_menus m
    JOIN platform.permissions p ON p.permission_key = 'docslot.patient.read'
    WHERE m.menu_key IN ('patients','patients.clinical')
    ON CONFLICT DO NOTHING;

    -- doctors → docslot.doctor.read
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m_doctors, p.permission_id FROM platform.permissions p
    WHERE p.permission_key = 'docslot.doctor.read' ON CONFLICT DO NOTHING;

    -- lab → docslot.report.read (lab reports)
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m_lab, p.permission_id FROM platform.permissions p
    WHERE p.permission_key = 'docslot.report.read' ON CONFLICT DO NOTHING;

    -- analytics → docslot.analytics.read
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m_analytics, p.permission_id FROM platform.permissions p
    WHERE p.permission_key = 'docslot.analytics.read' ON CONFLICT DO NOTHING;

    -- Care Partners + children → commission.broker.read (customer-facing referral mgmt)
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m.menu_id, p.permission_id
    FROM platform.navigation_menus m
    JOIN platform.permissions p ON p.permission_key = 'commission.broker.read'
    WHERE m.menu_key IN ('care_partners','care_partners.directory','care_partners.payouts')
    ON CONFLICT DO NOTHING;

    -- Team & Roles → tenant.roles.assign
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m_team, p.permission_id FROM platform.permissions p
    WHERE p.permission_key = 'tenant.roles.assign' ON CONFLICT DO NOTHING;

    -- Developers / API → platform.api_clients.manage
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m_developers, p.permission_id FROM platform.permissions p
    WHERE p.permission_key = 'platform.api_clients.manage' ON CONFLICT DO NOTHING;

    -- Security & Compliance → platform.audit.read (audit/breach/encryption screens)
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m_security, p.permission_id FROM platform.permissions p
    WHERE p.permission_key = 'platform.audit.read' ON CONFLICT DO NOTHING;

    -- settings.brokers → commission.rules.read
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m.menu_id, p.permission_id
    FROM platform.navigation_menus m
    JOIN platform.permissions p ON p.permission_key = 'commission.rules.read'
    WHERE m.menu_key = 'settings.brokers' ON CONFLICT DO NOTHING;

    -- settings.users → tenant.roles.assign
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m.menu_id, p.permission_id
    FROM platform.navigation_menus m
    JOIN platform.permissions p ON p.permission_key = 'tenant.roles.assign'
    WHERE m.menu_key = 'settings.users' ON CONFLICT DO NOTHING;

    -- Overview/dashboard and Settings root have NO gating permission — visible to
    -- all authenticated users (the FE still hides empty sub-items per their gates).
END $seed_menus$;

-- ============================================================================
-- PERMISSIONS for managing the new RBAC features themselves
-- ============================================================================
INSERT INTO platform.permissions (permission_key, resource, action, scope, description, is_dangerous) VALUES
('platform.menus.read', 'navigation_menus', 'read', 'tenant', 'View navigation menu config', false),
('platform.menus.manage', 'navigation_menus', 'update', 'platform', 'Edit menu structure', true),
('platform.overrides.read', 'user_permission_overrides', 'read', 'tenant', 'View user permission overrides', false),
('platform.overrides.grant', 'user_permission_overrides', 'create', 'tenant', 'Grant/deny permission to a user (override)', true),
('platform.resource_types.manage', 'resource_types', 'update', 'platform', 'Manage resource type registry', true),
('platform.action_types.manage', 'action_types', 'update', 'platform', 'Manage action type registry', true)
ON CONFLICT (permission_key) DO NOTHING;

-- ============================================================================
-- NEW DOMAIN PERMISSION KEYS (Slice 08 — finer-grained gating)
-- ============================================================================
-- Backend endpoints were previously over-gated on broader keys:
--   * POST /patients reused docslot.patient.update (an update key gating a create)
--   * mark-no-show reused docslot.booking.complete (different clinical meaning)
-- We ADD dedicated keys and re-gate those endpoints to them. We do NOT remove
-- the old keys — the frontend's interim gates still reference .update/.complete,
-- so removing them would break FE menus/buttons. Old keys stay valid; new keys
-- are the precise authority the backend now requires for those two actions.
INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
('docslot.patient.create', (SELECT product_id FROM platform.products WHERE product_key='docslot'),
    'patient', 'create', 'tenant', 'Register a new patient record (distinct from updating an existing one)', true),
('docslot.booking.no_show', (SELECT product_id FROM platform.products WHERE product_key='docslot'),
    'booking', 'no_show', 'tenant', 'Mark a booking as no-show (distinct from completing it)', false)
ON CONFLICT (permission_key) DO NOTHING;

-- Grant the two new docslot keys to the same roles that already hold the
-- broader keys they split from, so re-gating the endpoints does not silently
-- lock out users who legitimately performed these actions before.
--   docslot.patient.create  -> roles that have docslot.patient.update
--   docslot.booking.no_show -> roles that have docslot.booking.complete
DO $new_keys$
BEGIN
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT rp.role_id, np.permission_id
    FROM platform.role_permissions rp
    JOIN platform.permissions op ON op.permission_id = rp.permission_id
        AND op.permission_key = 'docslot.patient.update'
    CROSS JOIN platform.permissions np
    WHERE np.permission_key = 'docslot.patient.create'
    ON CONFLICT DO NOTHING;

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT rp.role_id, np.permission_id
    FROM platform.role_permissions rp
    JOIN platform.permissions op ON op.permission_id = rp.permission_id
        AND op.permission_key = 'docslot.booking.complete'
    CROSS JOIN platform.permissions np
    WHERE np.permission_key = 'docslot.booking.no_show'
    ON CONFLICT DO NOTHING;
END $new_keys$;

-- Assign read perms to tenant_owner and tenant_admin; grant perms to tenant_owner only
DO $assign$
BEGIN
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_owner'
      AND p.permission_key IN ('platform.menus.read', 'platform.overrides.read', 'platform.overrides.grant')
    ON CONFLICT DO NOTHING;

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_admin'
      AND p.permission_key IN ('platform.menus.read', 'platform.overrides.read')
    ON CONFLICT DO NOTHING;
END $assign$;

-- ============================================================================
-- VIEW: effective permissions per user (role + overrides combined)
-- ============================================================================
-- Useful for admin "what can this user actually do?" screens and for debugging.
CREATE OR REPLACE VIEW platform.v_user_effective_permissions AS
-- Role-granted permissions NOT denied by an override
SELECT DISTINCT
    vup.user_id,
    vup.tenant_id,
    vup.permission_key,
    'role'::TEXT AS source
FROM platform.v_user_permissions vup
WHERE NOT EXISTS (
    SELECT 1 FROM platform.user_permission_overrides o
    JOIN platform.permissions p ON p.permission_id = o.permission_id
    WHERE o.user_id = vup.user_id
      AND p.permission_key = vup.permission_key
      AND o.is_allowed = false
      AND o.is_active = true
      AND (o.tenant_id = vup.tenant_id OR o.tenant_id IS NULL)
      AND o.effective_from <= NOW()
      AND (o.expires_at IS NULL OR o.expires_at > NOW())
)
UNION
-- Grant overrides
SELECT DISTINCT
    o.user_id,
    o.tenant_id,
    p.permission_key,
    'override_grant'::TEXT AS source
FROM platform.user_permission_overrides o
JOIN platform.permissions p ON p.permission_id = o.permission_id
WHERE o.is_allowed = true
  AND o.is_active = true
  AND o.effective_from <= NOW()
  AND (o.expires_at IS NULL OR o.expires_at > NOW());

COMMENT ON VIEW platform.v_user_effective_permissions IS 'Final effective permissions per user (role grants minus deny-overrides plus grant-overrides).';

-- ============================================================================
-- END OF RBAC ENHANCEMENTS
-- ============================================================================
-- Tables: 5 (resource_types, action_types, navigation_menus, menu_permissions,
--            user_permission_overrides)
-- Functions: updated user_has_permission() + new get_user_menus()
-- Views: v_user_effective_permissions
-- Permissions: 6 RBAC-mgmt + 2 domain (docslot.patient.create, docslot.booking.no_show)
-- Seed: ResourceTypes/ActionTypes backfilled, full navigation menu tree
--       (Overview, Bookings+children, Calendar, Patients+clinical, Doctors, Lab,
--        Analytics, Care Partners+children, Team & Roles, Developers, Security &
--        Compliance, Settings+children), bilingual + tenant_type-aware, menu→perm maps
--
-- TOTAL PLATFORM TABLES AFTER THIS FILE: 112 (was 107)
--
-- HOW THE FRONTEND USES THIS (the payoff):
--   On login, frontend calls: GET /api/v1/me/menus
--   Backend runs: SELECT * FROM platform.get_user_menus(user_id, tenant_id, tenant_type)
--   Frontend renders the returned tree. Zero hardcoded menu show/hide logic.
--   Add a feature → insert menu rows + map permission → it appears for the right
--   users on next login. No frontend deploy.
-- ============================================================================


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 7/11: RBAC Enhancements complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 8/11: Chat Identity & Discount
-- WA contact memory, behalf-booking consent, direct discount — docslot.*
-- Source: database/09_chat_identity.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 8/11: Chat Identity & Discount: % ---', '09_chat_identity.sql';
END $section$;

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
-- HIDDEN-PARTNER NUDGE SWEEP (the conversion funnel job — carrot, not stick)
-- ============================================================================
-- Nightly job: (1) recompute the funnel signal on every WhatsApp contact — how many DISTINCT patients this
-- booker number has booked for in the last 90 days, and whether the number belongs to a REGISTERED broker
-- (then it's not a "hidden" partner); (2) send the eligible "hidden Care Partners" (≥ p_min_patients distinct
-- patients, not a registered broker, not nudged within the cooldown) a bilingual "become a Care Partner" nudge
-- via the outbox + record it (one per cooldown — never nag). SECURITY DEFINER: the maintenance worker is
-- cross-tenant with no app.tenant_id, so it cannot satisfy the RLS on outbox_messages. Returns nudges sent.
CREATE OR REPLACE FUNCTION docslot.run_partner_nudge_sweep(
    p_min_patients INT DEFAULT 3, p_cooldown INTERVAL DEFAULT INTERVAL '30 days')
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    -- 1) Recompute distinct-patients-90d + broker linkage on every contact profile.
    UPDATE docslot.wa_contact_profiles wcp
    SET distinct_patients_90d = COALESCE((
            SELECT COUNT(DISTINCT bk.patient_id)
            FROM docslot.bookings bk
            WHERE bk.tenant_id = wcp.tenant_id
              AND bk.behalf_booker_phone = wcp.phone
              AND bk.booked_at > NOW() - INTERVAL '90 days'), 0),
        linked_broker_id = (
            SELECT br.broker_id FROM commission.brokers br
            WHERE regexp_replace(br.phone, '[^0-9]', '', 'g') = regexp_replace(wcp.phone, '[^0-9]', '', 'g')
            LIMIT 1),
        updated_at = NOW();

    -- 2) Nudge the eligible hidden partners (bilingual; carrot) + record it.
    WITH eligible AS (
        SELECT wcp.tenant_id, wcp.phone,
               COALESCE(wcp.preferred_language, 'en') AS lang,
               COALESCE(t.display_name, 'our clinic') AS clinic
        FROM docslot.wa_contact_profiles wcp
        LEFT JOIN platform.tenants t ON t.tenant_id = wcp.tenant_id
        WHERE wcp.linked_broker_id IS NULL
          AND wcp.distinct_patients_90d >= p_min_patients
          AND (wcp.partner_nudge_sent_at IS NULL OR wcp.partner_nudge_sent_at < NOW() - p_cooldown)
    ),
    enqueued AS (
        INSERT INTO docslot.outbox_messages
            (outbox_id, tenant_id, message_intent, payload, status, attempt_count, max_attempts, next_retry_at, created_at)
        SELECT gen_random_uuid(), e.tenant_id, 'partner_nudge',
            jsonb_build_object('to', e.phone, 'text',
                CASE WHEN e.lang = 'hi'
                THEN 'नमस्ते! आप ' || e.clinic || ' में कई मरीज़ों की अपॉइंटमेंट बुक करने में मदद कर रहे हैं। हमारे Care Partner बनें और हर रेफ़रल पर कमाएँ — कोई शुल्क नहीं। अधिक जानने के लिए "PARTNER" लिखें।'
                ELSE 'Namaste! You''ve been helping several patients book appointments at ' || e.clinic || '. Become a Care Partner and earn on every referral — no fees. Reply "PARTNER" to learn more.'
                END),
            'pending', 0, 5, NOW(), NOW()
        FROM eligible e
        RETURNING tenant_id, payload->>'to' AS phone
    ),
    marked AS (
        UPDATE docslot.wa_contact_profiles wcp
        SET partner_nudge_count = partner_nudge_count + 1, partner_nudge_sent_at = NOW(), updated_at = NOW()
        FROM enqueued en WHERE wcp.tenant_id = en.tenant_id AND wcp.phone = en.phone
        RETURNING 1
    )
    SELECT COUNT(*)::int INTO v_n FROM enqueued;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION docslot.run_partner_nudge_sweep IS
    'Recompute the hidden-Care-Partner funnel + send eligible numbers a bilingual conversion nudge via the outbox (one per cooldown). SECURITY DEFINER for the RLS-less maintenance worker. Returns nudges sent.';

GRANT EXECUTE ON FUNCTION docslot.run_partner_nudge_sweep(INT, INTERVAL) TO docslot_app;

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


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 8/11: Chat Identity & Discount complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 9/11: Future Products (Optional)
-- RuralReach + SafeHer + GenericFirst
-- Source: database/04_future_products.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 9/11: Future Products (Optional): % ---', '04_future_products.sql';
END $section$;

-- ============================================================================
-- DocSlot Platform Database — Part 4: Future Product Schemas (Optional)
-- ============================================================================
-- These schemas are for the three adjacent products documented in
-- FUTURE_PRODUCTS.md. Run only if/when these products are being built.
-- Each schema is independent — you can deploy them separately.
-- ============================================================================

-- ============================================================================
-- 04. RURALREACH — Mobile diagnostic logistics
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS ruralreach;
COMMENT ON SCHEMA ruralreach IS 'Mobile diagnostic vans + home sample collection logistics';

-- Vehicle fleet
CREATE TABLE ruralreach.vehicles (
    vehicle_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    vehicle_number      VARCHAR(20) NOT NULL UNIQUE,
    vehicle_type        VARCHAR(30) NOT NULL CHECK (vehicle_type IN ('van', 'two_wheeler', 'car', 'truck')),
    capacity_samples    INT NOT NULL DEFAULT 50,
    has_refrigeration   BOOLEAN NOT NULL DEFAULT false,
    min_temp_celsius    DECIMAL(4,1),
    max_temp_celsius    DECIMAL(4,1),
    current_status      VARCHAR(20) NOT NULL DEFAULT 'available' CHECK (current_status IN ('available', 'on_route', 'maintenance', 'off_duty')),
    current_lat         DECIMAL(10,7),
    current_lng         DECIMAL(10,7),
    last_gps_update     TIMESTAMPTZ,
    fuel_type           VARCHAR(20),
    registration_expires DATE,
    insurance_expires   DATE,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_vehicles_tenant ON ruralreach.vehicles(tenant_id) WHERE is_active = true;
CREATE INDEX idx_vehicles_status ON ruralreach.vehicles(current_status) WHERE is_active = true;
CREATE TRIGGER trg_vehicles_updated_at BEFORE UPDATE ON ruralreach.vehicles
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- Field technicians
CREATE TABLE ruralreach.technicians (
    technician_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    user_id             UUID REFERENCES platform.users(user_id),
    full_name           VARCHAR(200) NOT NULL,
    gender              VARCHAR(10),
    phone               VARCHAR(15) NOT NULL,
    employee_id         VARCHAR(50),
    certifications      JSONB DEFAULT '[]',                     -- [{name, issued_by, expires_at}]
    specialized_tests   VARCHAR(50)[],                          -- Can perform: blood, ECG, X-ray
    current_lat         DECIMAL(10,7),
    current_lng         DECIMAL(10,7),
    is_available        BOOLEAN NOT NULL DEFAULT true,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_technicians_tenant ON ruralreach.technicians(tenant_id) WHERE is_active = true;
CREATE INDEX idx_technicians_available ON ruralreach.technicians(tenant_id) WHERE is_available = true AND is_active = true;

-- Service zones
CREATE TABLE ruralreach.service_zones (
    zone_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    zone_name           VARCHAR(100) NOT NULL,
    pin_codes           VARCHAR(10)[],
    boundary_polygon    JSONB,                                   -- GeoJSON polygon
    service_fee         DECIMAL(10,2),
    estimated_arrival_minutes INT,
    is_active           BOOLEAN NOT NULL DEFAULT true
);

-- Collection requests (links to DocSlot bookings)
CREATE TABLE ruralreach.collection_requests (
    request_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),

    -- Link to source booking (cross-product reference)
    source_booking_id   UUID,                                    -- docslot.bookings.booking_id
    patient_id          UUID,                                    -- docslot.patients.patient_id
    patient_phone       VARCHAR(15) NOT NULL,
    patient_name        VARCHAR(200) NOT NULL,

    -- Collection details
    tests_requested     JSONB NOT NULL,                          -- Array of test_ids from docslot.test_catalog
    collection_address  TEXT NOT NULL,
    pickup_lat          DECIMAL(10,7),
    pickup_lng          DECIMAL(10,7),
    pin_code            VARCHAR(10) NOT NULL,
    zone_id             UUID REFERENCES ruralreach.service_zones(zone_id),

    -- Scheduling
    requested_window_start TIMESTAMPTZ NOT NULL,
    requested_window_end TIMESTAMPTZ NOT NULL,
    assigned_vehicle_id UUID REFERENCES ruralreach.vehicles(vehicle_id),
    assigned_technician_id UUID REFERENCES ruralreach.technicians(technician_id),
    estimated_arrival_at TIMESTAMPTZ,

    -- Status tracking
    status              VARCHAR(30) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'assigned', 'en_route', 'arrived', 'collected', 'in_transit_to_lab', 'delivered_to_lab', 'cancelled', 'failed')),

    -- Actuals
    technician_arrived_at TIMESTAMPTZ,
    collected_at        TIMESTAMPTZ,
    delivered_to_lab_at TIMESTAMPTZ,
    target_lab_tenant_id UUID REFERENCES platform.tenants(tenant_id),  -- Which DocSlot lab receives the sample

    -- Pricing
    base_fee            DECIMAL(10,2),
    zone_fee            DECIMAL(10,2),
    total_amount        DECIMAL(10,2),

    notes               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_requests_status ON ruralreach.collection_requests(status, requested_window_start);
CREATE INDEX idx_requests_technician ON ruralreach.collection_requests(assigned_technician_id) WHERE status IN ('assigned', 'en_route', 'arrived');
CREATE INDEX idx_requests_zone ON ruralreach.collection_requests(zone_id, requested_window_start);
CREATE INDEX idx_requests_source_booking ON ruralreach.collection_requests(source_booking_id) WHERE source_booking_id IS NOT NULL;

-- Sample chain of custody
CREATE TABLE ruralreach.sample_chain_of_custody (
    custody_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id          UUID NOT NULL REFERENCES ruralreach.collection_requests(request_id) ON DELETE CASCADE,
    sample_barcode      VARCHAR(50) NOT NULL UNIQUE,
    sample_type         VARCHAR(50),
    collected_at        TIMESTAMPTZ NOT NULL,
    collected_by_technician_id UUID REFERENCES ruralreach.technicians(technician_id),
    handover_to_vehicle_id UUID REFERENCES ruralreach.vehicles(vehicle_id),
    handover_at         TIMESTAMPTZ,
    delivered_to_lab_at TIMESTAMPTZ,
    received_by_user_id UUID REFERENCES platform.users(user_id),
    rejected            BOOLEAN NOT NULL DEFAULT false,
    rejection_reason    TEXT
);

CREATE INDEX idx_custody_request ON ruralreach.sample_chain_of_custody(request_id);
CREATE INDEX idx_custody_barcode ON ruralreach.sample_chain_of_custody(sample_barcode);

-- Cold chain temperature logs
CREATE TABLE ruralreach.cold_chain_logs (
    log_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    vehicle_id          UUID NOT NULL REFERENCES ruralreach.vehicles(vehicle_id),
    request_id          UUID REFERENCES ruralreach.collection_requests(request_id),
    temperature_celsius DECIMAL(4,1) NOT NULL,
    humidity_percent    SMALLINT,
    is_within_range     BOOLEAN NOT NULL,
    recorded_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_cold_chain_vehicle_time ON ruralreach.cold_chain_logs(vehicle_id, recorded_at DESC);
CREATE INDEX idx_cold_chain_violations ON ruralreach.cold_chain_logs(vehicle_id, recorded_at)
    WHERE is_within_range = false;

-- Routes (optimized daily routes for vehicles)
CREATE TABLE ruralreach.routes (
    route_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    vehicle_id          UUID NOT NULL REFERENCES ruralreach.vehicles(vehicle_id),
    technician_id       UUID NOT NULL REFERENCES ruralreach.technicians(technician_id),
    route_date          DATE NOT NULL,
    planned_start_at    TIMESTAMPTZ,
    actual_start_at     TIMESTAMPTZ,
    actual_end_at       TIMESTAMPTZ,
    total_stops         INT NOT NULL DEFAULT 0,
    completed_stops     INT NOT NULL DEFAULT 0,
    total_distance_km   DECIMAL(8,2),
    optimized_path      JSONB,                                   -- Array of waypoints
    status              VARCHAR(20) NOT NULL DEFAULT 'planned'
        CHECK (status IN ('planned', 'in_progress', 'completed', 'cancelled'))
);

CREATE TABLE ruralreach.route_stops (
    stop_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    route_id            UUID NOT NULL REFERENCES ruralreach.routes(route_id) ON DELETE CASCADE,
    request_id          UUID NOT NULL REFERENCES ruralreach.collection_requests(request_id),
    sequence_number     SMALLINT NOT NULL,
    planned_arrival_at  TIMESTAMPTZ,
    actual_arrival_at   TIMESTAMPTZ,
    actual_departure_at TIMESTAMPTZ,
    UNIQUE(route_id, sequence_number)
);

-- ============================================================================
-- 05. SAFEHER — Women's healthcare access
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS safeher;
COMMENT ON SCHEMA safeher IS 'Women-focused healthcare access — same-gender services, female-only slots, chaperone marketplace';

-- Female staff registry (extends docslot.doctors with gender-specific data)
CREATE TABLE safeher.female_staff (
    staff_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    doctor_id           UUID REFERENCES docslot.doctors(doctor_id),  -- If they're also a doctor in DocSlot
    technician_id       UUID REFERENCES ruralreach.technicians(technician_id),  -- Or technician in RuralReach
    full_name           VARCHAR(200) NOT NULL,
    languages_spoken    VARCHAR(10)[] NOT NULL DEFAULT ARRAY['hi','en'],
    specializations     VARCHAR(100)[],
    background_verified BOOLEAN NOT NULL DEFAULT false,
    background_verified_at TIMESTAMPTZ,
    photo_url           TEXT,
    bio                 TEXT,
    is_available_for_chaperone BOOLEAN NOT NULL DEFAULT false,
    chaperone_hourly_rate DECIMAL(10,2),
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_female_staff_tenant ON safeher.female_staff(tenant_id) WHERE is_active = true;
CREATE INDEX idx_female_staff_chaperone ON safeher.female_staff(tenant_id) WHERE is_available_for_chaperone = true;

-- Female-only slots (overlay on docslot.time_slots)
CREATE TABLE safeher.female_only_slots (
    slot_designation_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    slot_id             UUID NOT NULL UNIQUE,                    -- References docslot.time_slots
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    designation_reason  VARCHAR(100),
    requires_female_doctor BOOLEAN NOT NULL DEFAULT true,
    requires_female_technician BOOLEAN NOT NULL DEFAULT true,
    designated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    designated_by_user_id UUID REFERENCES platform.users(user_id)
);

-- Chaperone bookings
CREATE TABLE safeher.chaperone_bookings (
    chaperone_booking_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    related_booking_id  UUID,                                    -- docslot.bookings.booking_id
    patient_phone       VARCHAR(15) NOT NULL,
    patient_name        VARCHAR(200),
    chaperone_staff_id  UUID NOT NULL REFERENCES safeher.female_staff(staff_id),
    pickup_address      TEXT NOT NULL,
    destination_address TEXT NOT NULL,
    scheduled_pickup_at TIMESTAMPTZ NOT NULL,
    estimated_duration_hours DECIMAL(4,2),
    actual_pickup_at    TIMESTAMPTZ,
    actual_return_at    TIMESTAMPTZ,
    hourly_rate         DECIMAL(10,2),
    total_amount        DECIMAL(10,2),
    status              VARCHAR(20) NOT NULL DEFAULT 'requested'
        CHECK (status IN ('requested', 'confirmed', 'in_progress', 'completed', 'cancelled', 'no_show')),
    is_anonymous_booking BOOLEAN NOT NULL DEFAULT false,         -- For sensitive cases
    notes               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_chaperone_status ON safeher.chaperone_bookings(status, scheduled_pickup_at);
CREATE INDEX idx_chaperone_staff ON safeher.chaperone_bookings(chaperone_staff_id, scheduled_pickup_at DESC);

-- Privacy preferences (per patient)
CREATE TABLE safeher.patient_privacy_preferences (
    patient_id          UUID PRIMARY KEY,                        -- References docslot.patients
    requires_female_provider BOOLEAN NOT NULL DEFAULT false,
    requires_female_only_facility BOOLEAN NOT NULL DEFAULT false,
    requires_chaperone  BOOLEAN NOT NULL DEFAULT false,
    hide_from_family_phone BOOLEAN NOT NULL DEFAULT false,       -- For sensitive bookings (the phone might be shared)
    secondary_phone     VARCHAR(15),                              -- Discreet contact number
    secondary_phone_use VARCHAR(50),                              -- 'reminders_only', 'confirmations_only'
    preferred_pickup_landmarks TEXT,                              -- Discreet pickup locations
    notes               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Anonymous bookings (for sensitive procedures — partner shouldn't know)
CREATE TABLE safeher.anonymous_bookings (
    anon_booking_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    anonymous_code      VARCHAR(20) NOT NULL UNIQUE,             -- Patient receives only this
    related_booking_id  UUID,                                    -- docslot.bookings.booking_id, encrypted FK
    secondary_phone     VARCHAR(15) NOT NULL,
    contact_window_start TIME,                                    -- Only call during these hours
    contact_window_end  TIME,
    contact_method      VARCHAR(20) DEFAULT 'whatsapp_only',
    expires_at          TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Women's-only zones (clinics, waiting areas)
CREATE TABLE safeher.female_only_facilities (
    facility_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL UNIQUE REFERENCES platform.tenants(tenant_id),
    has_separate_entrance BOOLEAN NOT NULL DEFAULT false,
    has_female_only_waiting_area BOOLEAN NOT NULL DEFAULT false,
    has_female_only_restrooms BOOLEAN NOT NULL DEFAULT false,
    has_curtained_examination BOOLEAN NOT NULL DEFAULT true,
    all_female_staff    BOOLEAN NOT NULL DEFAULT false,
    certifications      JSONB DEFAULT '[]',
    photo_urls          TEXT[],
    verified_at         TIMESTAMPTZ
);

-- Reviews specifically about female-friendliness
CREATE TABLE safeher.female_friendly_reviews (
    review_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    related_booking_id  UUID,                                    -- docslot.bookings
    patient_id          UUID,                                    -- docslot.patients
    privacy_rating      SMALLINT CHECK (privacy_rating BETWEEN 1 AND 5),
    staff_sensitivity_rating SMALLINT CHECK (staff_sensitivity_rating BETWEEN 1 AND 5),
    facility_rating     SMALLINT CHECK (facility_rating BETWEEN 1 AND 5),
    chaperone_rating    SMALLINT CHECK (chaperone_rating BETWEEN 1 AND 5),
    comment             TEXT,
    is_published        BOOLEAN NOT NULL DEFAULT true,
    is_anonymous        BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_female_reviews_tenant ON safeher.female_friendly_reviews(tenant_id) WHERE is_published = true;

-- Cultural preferences (regional context)
CREATE TABLE safeher.cultural_preferences (
    preference_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    region              VARCHAR(50),
    supported_languages VARCHAR(10)[],
    respects_purdah     BOOLEAN NOT NULL DEFAULT false,
    veil_friendly_examination BOOLEAN NOT NULL DEFAULT false,
    family_decision_protocol VARCHAR(50),                        -- 'patient_only', 'spouse_required', 'family_council'
    metadata            JSONB DEFAULT '{}'
);

-- ============================================================================
-- 06. GENERICFIRST — Prescription decision support
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS genericfirst;
COMMENT ON SCHEMA genericfirst IS 'Generic drug intelligence — suggests cheaper equivalents at prescription time';

-- Master drug database
CREATE TABLE genericfirst.drugs (
    drug_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    active_ingredient   VARCHAR(200) NOT NULL,
    brand_name          VARCHAR(200),
    is_generic          BOOLEAN NOT NULL DEFAULT false,
    manufacturer        VARCHAR(200),
    dosage_form         VARCHAR(50),                              -- 'tablet', 'syrup', 'injection', 'cream'
    strength            VARCHAR(50),                              -- '500mg', '5%'
    pack_size           VARCHAR(50),                              -- '10 tablets', '100ml'
    mrp                 DECIMAL(10,2),
    average_market_price DECIMAL(10,2),
    jan_aushadhi_price  DECIMAL(10,2),                            -- PMBJP government generic price
    cdsco_approved      BOOLEAN NOT NULL DEFAULT false,
    cdsco_approval_number VARCHAR(50),
    therapeutic_class   VARCHAR(100),
    requires_prescription BOOLEAN NOT NULL DEFAULT true,
    is_scheduled_drug   BOOLEAN NOT NULL DEFAULT false,           -- Schedule H/X drugs
    schedule_classification VARCHAR(10),
    contraindications   TEXT,
    side_effects        TEXT,
    metadata            JSONB DEFAULT '{}',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_drugs_ingredient ON genericfirst.drugs(active_ingredient);
CREATE INDEX idx_drugs_brand ON genericfirst.drugs(brand_name);
CREATE INDEX idx_drugs_search ON genericfirst.drugs USING gin(brand_name gin_trgm_ops);

-- Equivalence mappings (branded → generic alternatives)
CREATE TABLE genericfirst.drug_equivalence (
    equivalence_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branded_drug_id     UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    generic_drug_id     UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    equivalence_rating  VARCHAR(10) NOT NULL CHECK (equivalence_rating IN ('AB', 'AA', 'BX', 'BA', 'BB', 'BC')),  -- FDA-style ratings
    bioequivalence_study_url TEXT,
    notes               TEXT,
    verified_by_pharma_expert BOOLEAN NOT NULL DEFAULT false,
    verified_at         TIMESTAMPTZ,
    UNIQUE(branded_drug_id, generic_drug_id)
);

CREATE INDEX idx_equivalence_branded ON genericfirst.drug_equivalence(branded_drug_id);

-- Bioequivalence studies (research references)
CREATE TABLE genericfirst.bioequivalence_studies (
    study_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branded_drug_id     UUID REFERENCES genericfirst.drugs(drug_id),
    generic_drug_id     UUID REFERENCES genericfirst.drugs(drug_id),
    study_title         TEXT NOT NULL,
    journal_name        VARCHAR(200),
    publication_date    DATE,
    doi                 VARCHAR(100),
    pdf_url             TEXT,
    sample_size         INT,
    auc_ratio           DECIMAL(5,3),                             -- Area Under Curve ratio
    cmax_ratio          DECIMAL(5,3),                             -- Max concentration ratio
    confidence_interval_90 VARCHAR(50),
    conclusion          TEXT,
    metadata            JSONB DEFAULT '{}'
);

-- Drug interactions database
CREATE TABLE genericfirst.drug_interactions (
    interaction_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    drug_id_1           UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    drug_id_2           UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    severity            VARCHAR(20) NOT NULL CHECK (severity IN ('minor', 'moderate', 'major', 'contraindicated')),
    description         TEXT NOT NULL,
    clinical_effect     TEXT,
    management_advice   TEXT,
    references_doi      VARCHAR(100),
    CHECK (drug_id_1 < drug_id_2)                                 -- Avoid duplicate pairs
);

CREATE INDEX idx_interactions_drug1 ON genericfirst.drug_interactions(drug_id_1);
CREATE INDEX idx_interactions_drug2 ON genericfirst.drug_interactions(drug_id_2);

-- Prescription suggestions log (track what was suggested vs. what was prescribed)
CREATE TABLE genericfirst.prescription_suggestions (
    suggestion_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prescription_id     UUID,                                    -- docslot.prescriptions.prescription_id
    doctor_id           UUID,                                    -- docslot.doctors.doctor_id
    branded_drug_id     UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    suggested_generic_drug_id UUID REFERENCES genericfirst.drugs(drug_id),
    suggested_jan_aushadhi BOOLEAN NOT NULL DEFAULT false,
    estimated_savings_inr DECIMAL(10,2),
    doctor_accepted     BOOLEAN,
    doctor_decline_reason VARCHAR(200),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_suggestions_doctor ON genericfirst.prescription_suggestions(doctor_id, created_at DESC);
CREATE INDEX idx_suggestions_accepted ON genericfirst.prescription_suggestions(doctor_accepted, created_at DESC);

-- Pharmacy availability (which pharmacies stock which generics)
CREATE TABLE genericfirst.pharmacy_availability (
    availability_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    pharmacy_tenant_id  UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    drug_id             UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    in_stock            BOOLEAN NOT NULL DEFAULT false,
    quantity_available  INT,
    selling_price       DECIMAL(10,2),
    last_updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(pharmacy_tenant_id, drug_id)
);

CREATE INDEX idx_pharmacy_stock ON genericfirst.pharmacy_availability(drug_id, in_stock, selling_price);

-- ============================================================================
-- FUTURE PRODUCT PERMISSIONS
-- ============================================================================
DO $$
DECLARE
    rural_id UUID;
    safe_id UUID;
    generic_id UUID;
BEGIN
    SELECT product_id INTO rural_id FROM platform.products WHERE product_key = 'ruralreach';
    SELECT product_id INTO safe_id FROM platform.products WHERE product_key = 'safeher';
    SELECT product_id INTO generic_id FROM platform.products WHERE product_key = 'genericfirst';

    -- RuralReach permissions
    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    ('ruralreach.vehicle.manage', rural_id, 'vehicle', 'update', 'tenant', 'Manage vehicle fleet', false),
    ('ruralreach.technician.manage', rural_id, 'technician', 'update', 'tenant', 'Manage technicians', false),
    ('ruralreach.request.create', rural_id, 'request', 'create', 'tenant', 'Create collection requests', false),
    ('ruralreach.request.dispatch', rural_id, 'request', 'update', 'tenant', 'Dispatch vehicles', false),
    ('ruralreach.route.optimize', rural_id, 'route', 'update', 'tenant', 'Optimize routes', false),
    ('ruralreach.cold_chain.read', rural_id, 'cold_chain', 'read', 'tenant', 'View temperature logs', false);

    -- SafeHer permissions
    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    ('safeher.female_staff.manage', safe_id, 'female_staff', 'update', 'tenant', 'Manage female staff registry', false),
    ('safeher.chaperone.book', safe_id, 'chaperone', 'create', 'tenant', 'Book chaperone services', false),
    ('safeher.facility.certify', safe_id, 'facility', 'update', 'platform', 'Certify female-only facilities', true),
    ('safeher.anonymous_bookings.read', safe_id, 'anonymous_bookings', 'read', 'tenant', 'View anonymous bookings (audit only)', true);

    -- GenericFirst permissions
    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    ('genericfirst.drugs.read', generic_id, 'drugs', 'read', 'tenant', 'Search drug database', false),
    ('genericfirst.drugs.manage', generic_id, 'drugs', 'update', 'platform', 'Manage master drug database', true),
    ('genericfirst.suggestions.read', generic_id, 'suggestions', 'read', 'tenant', 'View prescription suggestions', false),
    ('genericfirst.interactions.read', generic_id, 'interactions', 'read', 'tenant', 'View drug interactions', false);
END $$;

-- ============================================================================
-- CROSS-PRODUCT VIEWS (queries that span multiple products)
-- ============================================================================

-- Unified patient view across DocSlot + RuralReach
CREATE OR REPLACE VIEW platform.v_patient_full_view AS
SELECT
    p.patient_id,
    p.full_name,
    p.phone_number,
    p.preferred_language,
    -- DocSlot data
    (SELECT COUNT(*) FROM docslot.bookings WHERE patient_id = p.patient_id) AS total_docslot_bookings,
    -- RuralReach data
    (SELECT COUNT(*) FROM ruralreach.collection_requests WHERE patient_phone = p.phone_number) AS total_home_collections,
    -- SafeHer flags
    CASE WHEN EXISTS (
        SELECT 1 FROM safeher.patient_privacy_preferences WHERE patient_id = p.patient_id
    ) THEN true ELSE false END AS has_privacy_preferences
FROM docslot.patients p
WHERE p.deleted_at IS NULL;

-- ============================================================================
-- END OF FUTURE PRODUCT SCHEMAS
-- ============================================================================
-- Tables created:
--   RuralReach:    8 (vehicles, technicians, service_zones, collection_requests,
--                     sample_chain_of_custody, cold_chain_logs, routes, route_stops)
--   SafeHer:       8 (female_staff, female_only_slots, chaperone_bookings,
--                     patient_privacy_preferences, anonymous_bookings,
--                     female_only_facilities, female_friendly_reviews, cultural_preferences)
--   GenericFirst:  7 (drugs, drug_equivalence, bioequivalence_studies, drug_interactions,
--                     prescription_suggestions, pharmacy_availability + extensible)
--
-- TOTAL PLATFORM TABLES: 26 (platform + platform_api)
-- TOTAL DOCSLOT TABLES:  26
-- TOTAL FUTURE TABLES:   23
-- GRAND TOTAL:           97 tables across 7 schemas (after 01-06 all loaded)
-- ============================================================================


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 9/11: Future Products (Optional) complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 10/11: Roles & Grants
-- Least-privilege docslot_app role + grants (RLS-enforced, audit append-only)
-- Source: database/10_roles_grants.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 10/11: Roles & Grants: % ---', '10_roles_grants.sql';
END $section$;

-- ============================================================================
-- DocSlot Platform Database — Part 10: Least-Privilege Application Role & Grants
-- ============================================================================
-- The application MUST NOT connect as a superuser. A superuser (or a role with
-- BYPASSRLS) silently bypasses every Row-Level Security policy on the PHI tables
-- (prescriptions, lab_reports, abdm_health_records, patient_medical_history,
-- drug_alerts) — making the RLS defense-in-depth decorative. This file creates a
-- dedicated NOSUPERUSER / NOBYPASSRLS role `docslot_app` with exactly the
-- privileges the application needs, and NO ability to UPDATE/DELETE the audit log.
--
-- IDEMPOTENT: safe to re-run. The role is created only if absent; GRANTs are
-- re-runnable. Runs LAST (after all schemas/tables/functions/sequences exist).
--
-- DEV AUTH: localhost trust auth (no password), consistent with the dev setup.
-- In production, set a password out-of-band (ALTER ROLE docslot_app PASSWORD ...)
-- sourced from a secret manager — never commit it.
--
-- EXECUTION:
--   psql -d docslot_platform -f 10_roles_grants.sql
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. The least-privilege application role
-- ----------------------------------------------------------------------------
DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'docslot_app') THEN
        CREATE ROLE docslot_app
            LOGIN
            NOSUPERUSER
            NOBYPASSRLS
            NOCREATEDB
            NOCREATEROLE
            NOREPLICATION;
        RAISE NOTICE 'Created role docslot_app (NOSUPERUSER, NOBYPASSRLS).';
    ELSE
        -- Enforce the safety attributes even if the role pre-exists.
        ALTER ROLE docslot_app NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
        RAISE NOTICE 'Role docslot_app already exists; re-asserted least-privilege attributes.';
    END IF;
END $roles$;

-- ----------------------------------------------------------------------------
-- 2. Connect + schema usage
-- ----------------------------------------------------------------------------
GRANT CONNECT ON DATABASE docslot_platform TO docslot_app;
GRANT USAGE ON SCHEMA platform, platform_api, docslot, commission TO docslot_app;

-- ----------------------------------------------------------------------------
-- 3. Table privileges — SELECT/INSERT/UPDATE on app tables (NO blanket DELETE;
--    soft-delete via deleted_at). Applied to all existing tables, then the audit
--    tables are locked down to append-only below.
-- ----------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA platform     TO docslot_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA platform_api TO docslot_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA docslot      TO docslot_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA commission   TO docslot_app;

-- A handful of bridge/junction + transient tables legitimately need DELETE
-- (re-assigning scopes, releasing holds, idempotency pruning, role grants).
-- Grant DELETE narrowly — NEVER on audit_log / audit_chain.
GRANT DELETE ON
    platform.user_tenant_roles,
    platform.user_sessions,
    platform.login_attempts,
    platform.idempotency_keys,
    platform_api.api_client_scopes,
    platform_api.api_tokens,
    platform_api.webhook_subscriptions,
    platform_api.webhook_deliveries,
    docslot.slot_holds,
    docslot.patient_tenant_links,
    commission.referral_clicks,
    commission.broker_tenant_links
TO docslot_app;

-- ----------------------------------------------------------------------------
-- 4. AUDIT APPEND-ONLY at the grant layer (belt-and-suspenders with the trigger)
--    The app may INSERT audit rows + the chain trigger appends chain rows, but
--    the app can NEVER UPDATE/DELETE the audit trail.
-- ----------------------------------------------------------------------------
REVOKE UPDATE, DELETE ON platform.audit_log   FROM docslot_app;
REVOKE UPDATE, DELETE ON platform.audit_chain FROM docslot_app;
GRANT  SELECT, INSERT  ON platform.audit_log   TO docslot_app;
GRANT  SELECT, INSERT  ON platform.audit_chain TO docslot_app;   -- chain trigger INSERTs as the caller

-- ----------------------------------------------------------------------------
-- 5. Sequences (BIGSERIAL audit_chain.sequence_number, booking/prescription/
--    report number sequences) — the triggers call nextval() as the caller.
-- ----------------------------------------------------------------------------
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA platform     TO docslot_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA platform_api TO docslot_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA docslot      TO docslot_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA commission   TO docslot_app;

-- ----------------------------------------------------------------------------
-- 6. Functions — RBAC resolver, menus, permission check, audit chain helpers,
--    verify, masking, RLS session helpers. EXECUTE on everything the app calls.
-- ----------------------------------------------------------------------------
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA platform   TO docslot_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA commission TO docslot_app;
-- docslot has app-callable functions too (e.g. generate_time_slots — the slot
-- materializer); trigger functions are harmless to include.
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA docslot    TO docslot_app;

-- ----------------------------------------------------------------------------
-- 7. RLS: the app role is subject to RLS (NOBYPASSRLS). The policies on the PHI
--    tables already allow access when app.tenant_id matches or app.is_super_admin
--    is set. The app sets app.tenant_id per transaction (SET LOCAL). Ensure the
--    policies apply to docslot_app (they apply to PUBLIC by default).
-- ----------------------------------------------------------------------------
-- (No FORCE ROW LEVEL SECURITY needed: docslot_app is not the table owner, so RLS
--  is enforced for it automatically.)

-- ----------------------------------------------------------------------------
-- 8. Default privileges for any tables/sequences created LATER (e.g. by future
--    migrations) so the app keeps working without re-running this file.
-- ----------------------------------------------------------------------------
ALTER DEFAULT PRIVILEGES IN SCHEMA platform, platform_api, docslot, commission
    GRANT SELECT, INSERT, UPDATE ON TABLES TO docslot_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA platform, platform_api, docslot, commission
    GRANT USAGE, SELECT ON SEQUENCES TO docslot_app;

DO $done$ BEGIN RAISE NOTICE 'docslot_app grants applied (least-privilege, RLS-enforced, audit append-only).'; END $done$;

-- ============================================================================
-- 9. SUPER_ADMIN UNIVERSAL PERMISSION SWEEP (end-of-bundle, runs LAST)
-- ============================================================================
-- super_admin = "full access to the entire platform (all tenants, all products)"
-- by definition (see database/README.md "Seeded System Roles"). Rather than
-- remembering to grant every new permission to super_admin in every product
-- file, we sweep the entire platform.permissions registry into super_admin's
-- role_permissions ONCE, here, at the very end of the bundle — AFTER every
-- product file (01,02,03,05,06,07,08,09,04) has inserted its permissions.
--
-- WHY HERE AND NOT IN 08: the bundle runs ...,08,09,04,10. Permissions are still
-- being inserted by 04 (future products: ruralreach/safeher/genericfirst), which
-- runs AFTER 08. A sweep in 08 would miss 04's keys. 10 is the terminal file, so
-- it sees the complete registry. This file is also re-runnable (idempotent), so
-- the sweep self-heals if any earlier grant was missed.
--
-- This closes the historical gap where super_admin held only platform + commission
-- perms and was missing all docslot, ai, and future-product permissions (the
-- authorization hole that motivated this consolidation wave).
INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'super_admin' AND r.is_system = true
ON CONFLICT DO NOTHING;

DO $sa$
DECLARE
    v_have INT;
    v_total INT;
BEGIN
    SELECT count(*) INTO v_total FROM platform.permissions;
    SELECT count(*) INTO v_have
    FROM platform.role_permissions rp
    JOIN platform.roles r ON r.role_id = rp.role_id AND r.role_key = 'super_admin';
    IF v_have <> v_total THEN
        RAISE EXCEPTION 'super_admin permission sweep incomplete: % of % granted', v_have, v_total;
    END IF;
    RAISE NOTICE 'super_admin holds ALL % permissions (universal sweep verified).', v_total;
END $sa$;

-- ============================================================================
-- END OF ROLES & GRANTS
-- ============================================================================


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 10/11: Roles & Grants complete ---';
    RAISE NOTICE '';
END $section_end$;


-- ============================================================================
-- PART 11/11: RBAC Hardening
-- RLS on RBAC tables, tenant-status gate, grant-option guard, menu ancestors, SoD, scoped impersonation — runs LAST
-- Source: database/11_rbac_hardening.sql
-- ============================================================================

DO $section$
BEGIN
    RAISE NOTICE '--- PART 11/11: RBAC Hardening: % ---', '11_rbac_hardening.sql';
END $section$;

-- ============================================================================
-- DocSlot Platform Database — Part 11: RBAC Hardening (R1–R6)
-- ============================================================================
-- Closes the residual enterprise-grade gaps found when reconciling the
-- RBAC_Navigation_PaaS_PostgreSQL.md reference against the shipped schema.
-- Everything DocSlot already does well (global identity, permission-level
-- overrides, hash-chained audit, SET LOCAL RLS on PHI, least-privilege app role)
-- is left untouched. This file adds ONLY what was genuinely missing:
--
--   R1  RLS on the RBAC/entitlement tables themselves (roles, role_permissions,
--       user_tenant_roles, user_permission_overrides, tenant_product_subscriptions,
--       navigation_menus, menu_permissions). Previously RLS existed only on the 5
--       PHI tables, so authz data had no DB-level tenant isolation / anti-tamper.
--   R2  Tenant-status enforcement in permission/menu resolution. A suspended,
--       cancelled, or trial-expired tenant must resolve to ZERO permissions/menus.
--   R3  Privilege-escalation guard (grant-option model) — SECURITY DEFINER grant
--       helpers that assert the actor may grant what they're granting.
--   R4  Ancestor inclusion in get_user_menus — a visible child under a gated
--       parent no longer orphans; the container is pulled in (matches the doc).
--   R5  Separation of Duties — role_incompatibility + enforcement trigger.
--   R6  Scoped, time-boxed, audited impersonation — replaces the all-or-nothing
--       app.is_super_admin god-flag for routine support access.
--
-- DESIGN NOTE (why this doesn't break login):
--   Enabling RLS on user_tenant_roles would normally break the "which tenants do
--   I belong to?" lookup that runs before a tenant context exists. We solve this
--   the standard way: the resolver functions and the membership lookup are
--   SECURITY DEFINER (run as the object owner, which is NOBYPASSRLS-exempt as the
--   table owner), so they see what they need; DIRECT table access by docslot_app
--   stays tenant-scoped by the policies. App code MUST use the definer functions
--   for login-time cross-tenant reads (helper provided below).
--
-- SECURITY-SENSITIVE: touches RBAC, RLS, grants, payout-adjacent SoD. Per CLAUDE.md
-- this requires security-compliance-auditor sign-off and a docslot-test-strategist
-- invariant suite (deny-wins, no cross-tenant read/write, suspended-tenant blocked,
-- no escalation, ancestors-included, SoD) before merge.
--
-- IDEMPOTENT: safe to re-run.
--
-- DEPENDENCIES: run AFTER 10_roles_grants.sql (needs docslot_app + all RBAC
--   tables/functions). This becomes the new terminal file in the bundle.
--
-- EXECUTION: psql -d docslot_platform -f 11_rbac_hardening.sql
-- ============================================================================

\set ON_ERROR_STOP on

-- ============================================================================
-- R2 — TENANT SERVICEABILITY GATE
-- ============================================================================
-- Single source of truth for "is this tenant allowed to resolve access right now?"
-- Used by the permission view and resolver so suspension/cancellation/trial-expiry
-- is structural, not left to app discretion. Platform-scoped access (tenant_id IS
-- NULL) is unaffected — it is not tenant-gated.
CREATE OR REPLACE FUNCTION platform.tenant_is_serviceable(p_tenant_id UUID)
RETURNS BOOLEAN LANGUAGE SQL STABLE AS $$
    SELECT EXISTS (
        SELECT 1 FROM platform.tenants t
        WHERE t.tenant_id = p_tenant_id
          AND t.deleted_at IS NULL
          AND (
                t.status = 'active'
             OR (t.status = 'trial'
                 AND (t.trial_ends_at IS NULL OR t.trial_ends_at > NOW()))
          )
    );
$$;
COMMENT ON FUNCTION platform.tenant_is_serviceable IS
    'R2: true only for active or non-expired-trial, non-deleted tenants. Gates permission/menu resolution so suspended/cancelled tenants resolve nothing.';

-- Re-define the effective-permissions view to exclude non-serviceable tenants.
-- Platform-scoped rows (utr.tenant_id IS NULL) pass through unconditionally.
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
  AND u.deleted_at IS NULL
  AND r.deleted_at IS NULL
  -- R2: platform access is never tenant-gated; tenant access requires serviceable tenant
  AND (utr.tenant_id IS NULL OR platform.tenant_is_serviceable(utr.tenant_id));

COMMENT ON VIEW platform.v_user_permissions IS
    'Effective permissions per user per tenant. R2: excludes suspended/cancelled/trial-expired tenants. Use for authorization checks.';

-- ============================================================================
-- R2 (cont.) — gate overrides on tenant serviceability too
-- ============================================================================
-- resolve_user_permissions adds grant-overrides directly from
-- user_permission_overrides (not via the view), so the same gate must apply there.
CREATE OR REPLACE FUNCTION platform.resolve_user_permissions(
    p_user_id UUID,
    p_tenant_id UUID DEFAULT NULL
) RETURNS TABLE(permission_key VARCHAR)
LANGUAGE SQL STABLE
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
    WITH active_overrides AS (
        SELECT p.permission_key, o.is_allowed
        FROM platform.user_permission_overrides o
        JOIN platform.permissions p ON p.permission_id = o.permission_id
        WHERE o.user_id = p_user_id
          AND o.is_active = true
          AND (o.tenant_id = p_tenant_id OR o.tenant_id IS NULL)
          AND o.effective_from <= NOW()
          AND (o.expires_at IS NULL OR o.expires_at > NOW())
          -- R2: a tenant-scoped override only counts for a serviceable tenant
          AND (o.tenant_id IS NULL OR platform.tenant_is_serviceable(o.tenant_id))
    ),
    denies AS (
        SELECT permission_key FROM active_overrides WHERE is_allowed = false
    ),
    role_grants AS (
        SELECT DISTINCT vup.permission_key
        FROM platform.v_user_permissions vup
        WHERE vup.user_id = p_user_id
          AND (vup.tenant_id = p_tenant_id OR vup.scope = 'platform')
    )
    SELECT rg.permission_key
    FROM role_grants rg
    WHERE rg.permission_key NOT IN (SELECT permission_key FROM denies)
    UNION
    SELECT ao.permission_key
    FROM active_overrides ao
    WHERE ao.is_allowed = true
      AND ao.permission_key NOT IN (SELECT permission_key FROM denies);
$$;
COMMENT ON FUNCTION platform.resolve_user_permissions IS
    'Single-query effective permission set (role grants - deny-overrides + grant-overrides). R2 tenant-gated. SECURITY DEFINER so it works under R1 RLS. Call once per request; check in memory.';

-- The hot-path single-permission check (08 version) — gate overrides the same way.
CREATE OR REPLACE FUNCTION platform.user_has_permission(
    p_user_id UUID,
    p_permission_key VARCHAR,
    p_tenant_id UUID DEFAULT NULL
) RETURNS BOOLEAN
LANGUAGE SQL STABLE
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
    WITH ovr AS (
        SELECT o.is_allowed
        FROM platform.user_permission_overrides o
        JOIN platform.permissions p ON p.permission_id = o.permission_id
        WHERE o.user_id = p_user_id
          AND p.permission_key = p_permission_key
          AND o.is_active = true
          AND (o.tenant_id = p_tenant_id OR o.tenant_id IS NULL)
          AND o.effective_from <= NOW()
          AND (o.expires_at IS NULL OR o.expires_at > NOW())
          AND (o.tenant_id IS NULL OR platform.tenant_is_serviceable(o.tenant_id))
        ORDER BY o.is_allowed ASC   -- deny (false) wins over grant (true)
        LIMIT 1
    )
    SELECT CASE
        WHEN EXISTS (SELECT 1 FROM ovr)
            THEN (SELECT is_allowed FROM ovr)
        ELSE EXISTS (
            SELECT 1 FROM platform.v_user_permissions
            WHERE user_id = p_user_id
              AND permission_key = p_permission_key
              AND (tenant_id = p_tenant_id OR scope = 'platform')
        )
    END;
$$;
COMMENT ON FUNCTION platform.user_has_permission IS
    'Effective permission check: deny-override > grant-override > role grant. R2 tenant-gated, time-boxed overrides respected. SECURITY DEFINER for R1 RLS.';

-- Keep the admin "what can this user do?" view consistent with R2.
CREATE OR REPLACE VIEW platform.v_user_effective_permissions AS
SELECT DISTINCT
    vup.user_id, vup.tenant_id, vup.permission_key, 'role'::TEXT AS source
FROM platform.v_user_permissions vup
WHERE NOT EXISTS (
    SELECT 1 FROM platform.user_permission_overrides o
    JOIN platform.permissions p ON p.permission_id = o.permission_id
    WHERE o.user_id = vup.user_id
      AND p.permission_key = vup.permission_key
      AND o.is_allowed = false
      AND o.is_active = true
      AND (o.tenant_id = vup.tenant_id OR o.tenant_id IS NULL)
      AND o.effective_from <= NOW()
      AND (o.expires_at IS NULL OR o.expires_at > NOW())
)
UNION
SELECT DISTINCT
    o.user_id, o.tenant_id, p.permission_key, 'override_grant'::TEXT AS source
FROM platform.user_permission_overrides o
JOIN platform.permissions p ON p.permission_id = o.permission_id
WHERE o.is_allowed = true
  AND o.is_active = true
  AND o.effective_from <= NOW()
  AND (o.expires_at IS NULL OR o.expires_at > NOW())
  AND (o.tenant_id IS NULL OR platform.tenant_is_serviceable(o.tenant_id));

-- ============================================================================
-- R4 — ANCESTOR-INCLUSIVE MENU RESOLUTION
-- ============================================================================
-- Old get_user_menus filtered each menu independently; a visible child under a
-- permission-gated parent the user lacks was returned WITHOUT its parent, so the
-- frontend could not attach it. This rewrite computes the directly-visible set,
-- then walks UP to pull in every ancestor container (visible regardless of its own
-- gate — containers exist to hold reachable leaves), exactly as the reference doc
-- intends. SECURITY DEFINER so it works under R1 RLS on navigation_menus.
CREATE OR REPLACE FUNCTION platform.get_user_menus(
    p_user_id UUID,
    p_tenant_id UUID,
    p_tenant_type VARCHAR DEFAULT NULL,
    p_product_key VARCHAR DEFAULT 'docslot'
) RETURNS TABLE(
    menu_id UUID,
    parent_menu_id UUID,
    menu_key VARCHAR,
    menu_label VARCHAR,
    menu_label_hi VARCHAR,
    menu_icon VARCHAR,
    menu_url VARCHAR,
    display_order INT,
    is_section_header BOOLEAN,
    badge_source VARCHAR
)
LANGUAGE SQL STABLE
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
    WITH RECURSIVE user_perms AS (
        SELECT rp.permission_key
        FROM platform.resolve_user_permissions(p_user_id, p_tenant_id) rp
    ),
    -- Candidate menus for this product / tenant / tenant_type
    candidate AS (
        SELECT m.*
        FROM platform.navigation_menus m
        WHERE m.is_active = true
          AND m.product_key = p_product_key
          AND (m.applies_to_tenant_types IS NULL
               OR p_tenant_type IS NULL
               OR p_tenant_type = ANY(m.applies_to_tenant_types))
          AND (m.tenant_id IS NULL OR m.tenant_id = p_tenant_id)
    ),
    -- Directly visible: ungated, or the user holds a gating permission
    directly_visible AS (
        SELECT c.menu_id
        FROM candidate c
        WHERE NOT EXISTS (SELECT 1 FROM platform.menu_permissions mp WHERE mp.menu_id = c.menu_id)
           OR EXISTS (
                SELECT 1 FROM platform.menu_permissions mp
                JOIN platform.permissions p ON p.permission_id = mp.permission_id
                WHERE mp.menu_id = c.menu_id
                  AND p.permission_key IN (SELECT permission_key FROM user_perms)
           )
    ),
    -- Walk UP to include ancestor containers so the tree stays connected
    visible_tree AS (
        SELECT c.* FROM candidate c WHERE c.menu_id IN (SELECT menu_id FROM directly_visible)
        UNION
        SELECT parent.* FROM candidate parent
        JOIN visible_tree child ON child.parent_menu_id = parent.menu_id
    )
    SELECT DISTINCT
        vt.menu_id, vt.parent_menu_id, vt.menu_key, vt.menu_label, vt.menu_label_hi,
        vt.menu_icon, vt.menu_url, vt.display_order, vt.is_section_header, vt.badge_source
    FROM visible_tree vt
    ORDER BY vt.display_order, vt.menu_label;
$$;
COMMENT ON FUNCTION platform.get_user_menus IS
    'R4: returns the menu tree a user can see WITH ancestor containers included (no orphaned children), filtered by permissions + tenant_type. SECURITY DEFINER for R1 RLS.';

-- Login-time, CROSS-TENANT self-read: the tenants a user can switch into. Runs
-- BEFORE any app.tenant_id exists, so a direct read of user_tenant_roles is blocked
-- by the R1 utr_read policy. SECURITY DEFINER (owner-rights) bypasses that, and the
-- p_user_id filter means a caller only ever sees their OWN memberships — no leak.
-- Preserves the original semantics (every active, non-deleted membership, incl.
-- is_primary) rather than only tenants where the user has resolved permissions.
CREATE OR REPLACE FUNCTION platform.user_memberships(p_user_id UUID)
RETURNS TABLE(tenant_id UUID, tenant_code VARCHAR, display_name VARCHAR, tenant_type VARCHAR, is_primary BOOLEAN)
LANGUAGE SQL STABLE
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
    SELECT t.tenant_id, t.tenant_code, t.display_name, t.tenant_type, bool_or(utr.is_primary)
    FROM platform.user_tenant_roles utr
    JOIN platform.tenants t ON t.tenant_id = utr.tenant_id
    WHERE utr.user_id = p_user_id
      AND utr.tenant_id IS NOT NULL
      AND utr.revoked_at IS NULL
      AND (utr.expires_at IS NULL OR utr.expires_at > NOW())
      AND t.deleted_at IS NULL
    GROUP BY t.tenant_id, t.tenant_code, t.display_name, t.tenant_type;
$$;
COMMENT ON FUNCTION platform.user_memberships IS
    'Login-time tenant switch-list for a user. SECURITY DEFINER so it works before app.tenant_id is set (R1); filtered to the caller''s own user_id. Recovers is_primary.';

-- ============================================================================
-- R6 — SCOPED, TIME-BOXED, AUDITED IMPERSONATION
-- ============================================================================
-- Replaces the blunt app.is_super_admin GUC (which bypasses ALL PHI RLS across
-- every tenant) for routine support: an impersonation is scoped to ONE tenant,
-- has a reason and an expiry, and is recorded. The PHI policies can then prefer
-- platform.current_impersonated_tenant() over the global flag (follow-up wave).
CREATE TABLE IF NOT EXISTS platform.impersonation_sessions (
    impersonation_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_user_id       UUID NOT NULL REFERENCES platform.users(user_id),
    target_tenant_id    UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    target_user_id      UUID REFERENCES platform.users(user_id),   -- NULL = tenant-wide support
    reason              TEXT NOT NULL,
    is_break_glass      BOOLEAN NOT NULL DEFAULT false,
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL,
    ended_at            TIMESTAMPTZ,
    ended_by_user_id    UUID REFERENCES platform.users(user_id),
    CONSTRAINT chk_impersonation_window CHECK (expires_at > started_at)
);
CREATE INDEX IF NOT EXISTS idx_impersonation_active
    ON platform.impersonation_sessions(actor_user_id, target_tenant_id)
    WHERE ended_at IS NULL;

-- These are platform-staff records: a tenant must neither read nor forge them.
-- RLS confines visibility/writes to a super_admin context; creation flows ONLY
-- through begin_impersonation() (SECURITY DEFINER, owner — bypasses RLS).
ALTER TABLE platform.impersonation_sessions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS impersonation_super_only ON platform.impersonation_sessions;
CREATE POLICY impersonation_super_only ON platform.impersonation_sessions FOR ALL
    USING (platform.current_is_super_admin())
    WITH CHECK (platform.current_is_super_admin());

-- Append-only except closing the session (ended_at / ended_by_user_id). A tenant
-- can't reach this (RLS), and even a super_admin context can't rewrite history.
CREATE OR REPLACE FUNCTION platform.impersonation_append_only()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'impersonation_sessions is append-only';
    END IF;
    IF ROW(NEW.impersonation_id, NEW.actor_user_id, NEW.target_tenant_id, NEW.target_user_id,
           NEW.reason, NEW.is_break_glass, NEW.started_at, NEW.expires_at)
       IS DISTINCT FROM
       ROW(OLD.impersonation_id, OLD.actor_user_id, OLD.target_tenant_id, OLD.target_user_id,
           OLD.reason, OLD.is_break_glass, OLD.started_at, OLD.expires_at) THEN
        RAISE EXCEPTION 'impersonation_sessions: only ended_at/ended_by_user_id may be updated';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_impersonation_append_only ON platform.impersonation_sessions;
CREATE TRIGGER trg_impersonation_append_only
    BEFORE UPDATE OR DELETE ON platform.impersonation_sessions
    FOR EACH ROW EXECUTE FUNCTION platform.impersonation_append_only();

-- Session-scoped accessor used by the PHI RLS policies (05_security_hardening.sql).
-- AUDITED-BY-CONSTRUCTION (issue #3): the raw app.impersonated_tenant GUC is INERT
-- on its own. This returns a tenant ONLY when it is backed by a live, non-expired
-- impersonation_sessions row for the ACTING user (app.user_id) — and such rows are
-- created EXCLUSIVELY by begin_impersonation(), which writes the hash-chained
-- audit_log entry in the same transaction. Consequences:
--   * A bare docslot_app session that does set_config('app.impersonated_tenant', <t>)
--     with no matching session sees NO cross-tenant PHI (and emits no audit) — the
--     GUC alone cannot unlock medical data.
--   * Once the session expires (expires_at <= NOW()) or is ended, the GUC stops
--     resolving — access is time-boxed, not just at open.
-- SECURITY DEFINER (owner) is REQUIRED: impersonation_sessions is RLS-confined to a
-- super_admin context, so a plain docslot_app caller could not otherwise read its own
-- backing row to validate. The function leaks nothing beyond the target_tenant_id it
-- was already asked about. STABLE: evaluated once per query, consistent within a tx.
-- This is the canonical (validating) definition; 05 defines a fail-closed bootstrap
-- reader because impersonation_sessions does not exist yet when 05 runs.
CREATE OR REPLACE FUNCTION platform.current_impersonated_tenant()
RETURNS UUID
LANGUAGE SQL STABLE SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
    SELECT s.target_tenant_id
    FROM platform.impersonation_sessions s
    WHERE s.target_tenant_id = NULLIF(current_setting('app.impersonated_tenant', true), '')::UUID
      AND s.actor_user_id    = NULLIF(current_setting('app.user_id', true), '')::UUID
      AND s.ended_at IS NULL
      AND s.expires_at > NOW()
    LIMIT 1;
$$;
COMMENT ON FUNCTION platform.current_impersonated_tenant IS
    'R6/issue#3: resolves app.impersonated_tenant to a tenant ONLY when a live, non-expired impersonation_sessions row exists for app.user_id. The GUC is inert without an audited begin_impersonation() session. SECURITY DEFINER to read past the session table''s super-only RLS.';

-- Open an impersonation session. Requires platform.users.impersonate. Writes audit.
CREATE OR REPLACE FUNCTION platform.begin_impersonation(
    p_actor_user_id UUID,
    p_target_tenant_id UUID,
    p_reason TEXT,
    p_target_user_id UUID DEFAULT NULL,
    p_ttl INTERVAL DEFAULT INTERVAL '30 minutes',
    p_break_glass BOOLEAN DEFAULT false
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
-- public is required: the INSERT into audit_log fires platform.append_to_audit_chain,
-- which calls digest() from the pgcrypto extension (installed in public).
SET search_path = platform, public, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    IF NOT platform.user_has_permission(p_actor_user_id, 'platform.users.impersonate', NULL) THEN
        RAISE EXCEPTION 'actor % lacks platform.users.impersonate', p_actor_user_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF p_reason IS NULL OR length(btrim(p_reason)) = 0 THEN
        RAISE EXCEPTION 'impersonation requires a non-empty reason';
    END IF;

    INSERT INTO platform.impersonation_sessions
        (actor_user_id, target_tenant_id, target_user_id, reason, is_break_glass, expires_at)
    VALUES
        (p_actor_user_id, p_target_tenant_id, p_target_user_id, p_reason, p_break_glass, NOW() + p_ttl)
    RETURNING impersonation_id INTO v_id;

    -- Audit-chain the most sensitive support action (DPDP S.8(7) accountability).
    -- The AFTER INSERT trigger platform.trigger_audit_chain hash-chains this row.
    INSERT INTO platform.audit_log
        (user_id, impersonator_user_id, tenant_id, action, resource_type, resource_id,
         resource_label, purpose, legal_basis, success)
    VALUES
        (p_target_user_id, p_actor_user_id, p_target_tenant_id, 'impersonate', 'tenant', p_target_tenant_id,
         'Impersonation session ' || v_id::text, p_reason,
         CASE WHEN p_break_glass THEN 'legal_obligation' ELSE 'contract' END, true);

    -- Break-glass surfaces on the security review queue.
    IF p_break_glass THEN
        INSERT INTO platform.alerts (code, severity, message, tenant_id, metadata)
        VALUES ('impersonation.break_glass', 'critical',
                'Break-glass impersonation opened by ' || p_actor_user_id::text || ' on tenant ' || p_target_tenant_id::text,
                p_target_tenant_id,
                jsonb_build_object('impersonation_id', v_id, 'actor_user_id', p_actor_user_id, 'reason', p_reason));
    END IF;

    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.begin_impersonation IS
    'R6: opens a tenant-scoped, time-boxed, reason-logged impersonation session. Replaces the global app.is_super_admin bypass for routine support.';

-- Close an impersonation session — the audited OTHER half of the lifecycle. begin_impersonation()
-- writes an audit row on OPEN; this writes one on CLOSE, so "support ended their session" is just as
-- accountable as "support started" it (DPDP S.8(7)). Without this, the only way to set ended_at is a raw
-- docslot_app UPDATE that emits no audit. SECURITY DEFINER (owner) to read/update past the session table's
-- super-only RLS; public is on the search_path because the audit_log INSERT fires the pgcrypto hash chain.
-- Idempotent: closing an already-ended session is a no-op (no duplicate audit row). Authorization: the actor
-- who OPENED the session may always self-close; anyone else must hold platform.users.impersonate (support
-- lead / break-glass cleanup) — the same permission begin_impersonation() requires.
CREATE OR REPLACE FUNCTION platform.end_impersonation(
    p_impersonation_id UUID,
    p_actor_user_id UUID
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, public, pg_temp
AS $$
DECLARE
    v_session platform.impersonation_sessions%ROWTYPE;
    v_rows INT;
BEGIN
    SELECT * INTO v_session
    FROM platform.impersonation_sessions
    WHERE impersonation_id = p_impersonation_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'impersonation session % not found', p_impersonation_id
            USING ERRCODE = 'no_data_found';
    END IF;

    IF p_actor_user_id IS DISTINCT FROM v_session.actor_user_id
       AND NOT platform.user_has_permission(p_actor_user_id, 'platform.users.impersonate', NULL) THEN
        RAISE EXCEPTION 'actor % may not end impersonation session %', p_actor_user_id, p_impersonation_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    -- Already closed (per our snapshot) ⇒ nothing to do, no second audit row (double-click / retry safe).
    IF v_session.ended_at IS NOT NULL THEN
        RETURN false;
    END IF;

    -- Only ended_at / ended_by_user_id may change here — exactly what the append-only trigger permits. The
    -- `AND ended_at IS NULL` makes the close ATOMIC: two concurrent closers that both passed the snapshot
    -- guard above can't both win — only the one whose UPDATE actually flips the row proceeds to audit, so a
    -- single close yields EXACTLY one end_impersonation row (no TOCTOU duplicate).
    UPDATE platform.impersonation_sessions
    SET ended_at = NOW(), ended_by_user_id = p_actor_user_id
    WHERE impersonation_id = p_impersonation_id
      AND ended_at IS NULL;

    GET DIAGNOSTICS v_rows = ROW_COUNT;
    IF v_rows = 0 THEN
        RETURN false;          -- lost the race to a concurrent close; it owns the audit row, not us
    END IF;

    -- Close the accountability loop. The AFTER INSERT trigger hash-chains this row like any other.
    INSERT INTO platform.audit_log
        (user_id, impersonator_user_id, tenant_id, action, resource_type, resource_id,
         resource_label, purpose, legal_basis, success)
    VALUES
        (v_session.target_user_id, p_actor_user_id, v_session.target_tenant_id, 'end_impersonation', 'tenant',
         v_session.target_tenant_id, 'Impersonation session ' || p_impersonation_id::text || ' ended',
         v_session.reason, CASE WHEN v_session.is_break_glass THEN 'legal_obligation' ELSE 'contract' END, true);

    RETURN true;
END;
$$;
COMMENT ON FUNCTION platform.end_impersonation IS
    'R6/issue#3: closes an impersonation session (ended_at/ended_by) and writes the symmetric end_impersonation audit row. Self-close by the opening actor, else requires platform.users.impersonate. Idempotent on an already-ended session.';

-- Oversight read for the Security & Compliance console (issue #3): list impersonation sessions with a
-- derived status (active / expired / ended) plus actor + target-tenant labels for display. SECURITY DEFINER
-- to read past the super-only RLS so the API can gate the surface on a review permission
-- (platform.anomalies.review) WITHOUT requiring the reader to be a platform super_admin. Returns metadata
-- only — no PHI; the API seam masks the actor to initials. STABLE, ordered newest-first.
CREATE OR REPLACE FUNCTION platform.list_impersonation_sessions(p_limit INT DEFAULT 100)
RETURNS TABLE (
    impersonation_id   UUID,
    actor_user_id      UUID,
    actor_name         TEXT,
    target_tenant_id   UUID,
    target_tenant_name TEXT,
    target_user_id     UUID,
    reason             TEXT,
    is_break_glass     BOOLEAN,
    started_at         TIMESTAMPTZ,
    expires_at         TIMESTAMPTZ,
    ended_at           TIMESTAMPTZ,
    ended_by_user_id   UUID,
    status             TEXT
)
LANGUAGE SQL STABLE SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
    SELECT s.impersonation_id, s.actor_user_id, a.full_name,
           s.target_tenant_id, t.display_name, s.target_user_id,
           s.reason, s.is_break_glass, s.started_at, s.expires_at,
           s.ended_at, s.ended_by_user_id,
           CASE WHEN s.ended_at IS NOT NULL   THEN 'ended'
                WHEN s.expires_at <= NOW()    THEN 'expired'
                ELSE 'active' END AS status
    FROM platform.impersonation_sessions s
    LEFT JOIN platform.users   a ON a.user_id   = s.actor_user_id
    LEFT JOIN platform.tenants t ON t.tenant_id = s.target_tenant_id
    ORDER BY s.started_at DESC
    LIMIT GREATEST(p_limit, 0);
$$;
COMMENT ON FUNCTION platform.list_impersonation_sessions IS
    'issue#3: oversight list of impersonation sessions (active/expired/ended) with actor + target labels. SECURITY DEFINER so the review surface need not require super_admin; metadata only, no PHI.';

-- ============================================================================
-- R5 — SEPARATION OF DUTIES
-- ============================================================================
-- Declares pairs of roles that must NOT be held by the same user in the same
-- tenant (e.g., payout-approver vs payout-executor). Enforced by a trigger on
-- user_tenant_roles. Permission-level maker-checker (one user can't both create
-- AND approve the SAME record) remains an application-layer control; this table
-- is the structural, DB-enforced half.
CREATE TABLE IF NOT EXISTS platform.role_incompatibility (
    role_a_id   UUID NOT NULL REFERENCES platform.roles(role_id) ON DELETE CASCADE,
    role_b_id   UUID NOT NULL REFERENCES platform.roles(role_id) ON DELETE CASCADE,
    reason      TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (role_a_id, role_b_id),
    CONSTRAINT chk_incompat_distinct CHECK (role_a_id <> role_b_id)
);
COMMENT ON TABLE platform.role_incompatibility IS
    'R5: Separation-of-Duties. A user may not hold both roles in the same tenant. Store each pair once; the trigger checks both orderings.';

CREATE OR REPLACE FUNCTION platform.enforce_role_sod()
RETURNS TRIGGER
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_conflict UUID;
BEGIN
    IF NEW.revoked_at IS NOT NULL THEN
        RETURN NEW;  -- a revoked assignment can't create a live conflict
    END IF;

    SELECT existing.role_id INTO v_conflict
    FROM platform.user_tenant_roles existing
    JOIN platform.role_incompatibility ri
      ON (ri.role_a_id = NEW.role_id AND ri.role_b_id = existing.role_id)
      OR (ri.role_b_id = NEW.role_id AND ri.role_a_id = existing.role_id)
    WHERE existing.user_id = NEW.user_id
      AND existing.revoked_at IS NULL
      AND existing.user_tenant_role_id <> NEW.user_tenant_role_id
      -- NULL (platform-level) compares equal to NULL; never invents a magic UUID.
      AND existing.tenant_id IS NOT DISTINCT FROM NEW.tenant_id
    LIMIT 1;

    IF v_conflict IS NOT NULL THEN
        RAISE EXCEPTION 'SoD violation: role % is incompatible with already-held role % for user % in this tenant',
            NEW.role_id, v_conflict, NEW.user_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_utr_sod ON platform.user_tenant_roles;
CREATE TRIGGER trg_utr_sod
    BEFORE INSERT OR UPDATE ON platform.user_tenant_roles
    FOR EACH ROW EXECUTE FUNCTION platform.enforce_role_sod();

-- ============================================================================
-- R3 — PRIVILEGE-ESCALATION GUARD (grant-option model)
-- ============================================================================
-- Adds is_grantable to role_permissions (default true = preserve current
-- behaviour) and SECURITY DEFINER helpers that assert the actor may grant what
-- they grant. Route ALL grant writes through these once R1 locks down direct
-- writes; super_admin is exempt (it holds everything, with grant option).
ALTER TABLE platform.role_permissions
    ADD COLUMN IF NOT EXISTS is_grantable BOOLEAN NOT NULL DEFAULT true;

-- True if the actor is a platform super_admin (universal grantor).
CREATE OR REPLACE FUNCTION platform.is_super_admin(p_user_id UUID)
RETURNS BOOLEAN LANGUAGE SQL STABLE AS $$
    SELECT EXISTS (
        SELECT 1
        FROM platform.user_tenant_roles utr
        JOIN platform.roles r ON r.role_id = utr.role_id
        WHERE utr.user_id = p_user_id
          AND utr.revoked_at IS NULL
          AND (utr.expires_at IS NULL OR utr.expires_at > NOW())
          AND r.role_key = 'super_admin'
          AND r.is_system = true
    );
$$;

-- Grant a permission to a role, only if the actor may grant it.
CREATE OR REPLACE FUNCTION platform.grant_permission_to_role(
    p_actor_user_id UUID,
    p_role_id UUID,
    p_permission_id UUID,
    p_tenant_id UUID DEFAULT NULL,
    p_grantable BOOLEAN DEFAULT false
) RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_perm_key VARCHAR;
    v_perm_scope VARCHAR;
BEGIN
    SELECT permission_key, scope INTO v_perm_key, v_perm_scope
    FROM platform.permissions WHERE permission_id = p_permission_id;
    IF v_perm_key IS NULL THEN
        RAISE EXCEPTION 'unknown permission %', p_permission_id;
    END IF;

    IF NOT platform.is_super_admin(p_actor_user_id) THEN
        -- Non-super actors may never confer platform-scoped authority...
        IF v_perm_scope = 'platform' THEN
            RAISE EXCEPTION 'actor % may not grant platform-scoped permission %', p_actor_user_id, v_perm_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- ...and may only grant a permission they themselves hold WITH grant option.
        IF NOT EXISTS (
            SELECT 1
            FROM platform.user_tenant_roles utr
            JOIN platform.role_permissions rp ON rp.role_id = utr.role_id
            WHERE utr.user_id = p_actor_user_id
              AND utr.revoked_at IS NULL
              AND (utr.expires_at IS NULL OR utr.expires_at > NOW())
              AND (utr.tenant_id = p_tenant_id OR utr.tenant_id IS NULL)
              AND rp.permission_id = p_permission_id
              AND rp.is_grantable = true
        ) THEN
            RAISE EXCEPTION 'actor % does not hold permission % with grant option', p_actor_user_id, v_perm_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;

    INSERT INTO platform.role_permissions (role_id, permission_id, granted_by, is_grantable)
    VALUES (p_role_id, p_permission_id, p_actor_user_id, p_grantable)
    ON CONFLICT (role_id, permission_id)
        DO UPDATE SET is_grantable = EXCLUDED.is_grantable, granted_by = EXCLUDED.granted_by;
END;
$$;
COMMENT ON FUNCTION platform.grant_permission_to_role IS
    'R3: grant a permission to a role only if the actor is super_admin OR holds it with grant option and it is not platform-scoped. Prevents privilege escalation.';

-- Assign a role to a user, only if the actor may confer that role's authority.
CREATE OR REPLACE FUNCTION platform.assign_role_to_user(
    p_actor_user_id UUID,
    p_user_id UUID,
    p_role_id UUID,
    p_tenant_id UUID DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_role_scope VARCHAR;
    v_role_key VARCHAR;
    v_id UUID;
BEGIN
    SELECT scope, role_key INTO v_role_scope, v_role_key
    FROM platform.roles WHERE role_id = p_role_id AND deleted_at IS NULL;
    IF v_role_key IS NULL THEN
        RAISE EXCEPTION 'unknown or deleted role %', p_role_id;
    END IF;

    IF NOT platform.is_super_admin(p_actor_user_id) THEN
        -- Only super_admin may confer platform-scoped roles (incl. super_admin itself).
        IF v_role_scope = 'platform' THEN
            RAISE EXCEPTION 'actor % may not assign platform-scoped role %', p_actor_user_id, v_role_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- Actor must hold tenant.roles.assign in the target tenant.
        IF NOT platform.user_has_permission(p_actor_user_id, 'tenant.roles.assign', p_tenant_id) THEN
            RAISE EXCEPTION 'actor % may not assign roles in tenant %', p_actor_user_id, p_tenant_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- No escalation: the actor cannot confer a permission they do not hold.
        IF EXISTS (
            SELECT 1
            FROM platform.role_permissions rp
            JOIN platform.permissions pm ON pm.permission_id = rp.permission_id
            WHERE rp.role_id = p_role_id
              AND NOT platform.user_has_permission(p_actor_user_id, pm.permission_key, p_tenant_id)
        ) THEN
            RAISE EXCEPTION 'actor % cannot assign role % — it confers permissions the actor does not hold', p_actor_user_id, v_role_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;

    INSERT INTO platform.user_tenant_roles (user_id, tenant_id, role_id, granted_by)
    VALUES (p_user_id, p_tenant_id, p_role_id, p_actor_user_id)
    ON CONFLICT (user_id, tenant_id, role_id) DO UPDATE
        SET revoked_at = NULL, revoked_by = NULL, revoked_reason = NULL, granted_by = EXCLUDED.granted_by
    RETURNING user_tenant_role_id INTO v_id;

    RETURN v_id;  -- SoD trigger (R5) fires here and will reject incompatible pairs
END;
$$;
COMMENT ON FUNCTION platform.assign_role_to_user IS
    'R3: assign a role only if the actor is super_admin OR holds tenant.roles.assign and every permission the role confers (no escalation). SoD enforced by trigger.';

-- Revoke a role assignment (soft). Returns false if it was already revoked.
CREATE OR REPLACE FUNCTION platform.revoke_role_assignment(
    p_actor_user_id UUID,
    p_user_tenant_role_id UUID,
    p_reason TEXT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_tenant UUID;
    v_revoked TIMESTAMPTZ;
    v_user UUID;
    v_role_id UUID;
    v_confers_admin BOOLEAN;
BEGIN
    IF p_reason IS NULL OR length(btrim(p_reason)) = 0 THEN
        RAISE EXCEPTION 'a revoke reason is mandatory';
    END IF;
    -- '[deactivated] ' is a RESERVED marker that set_tenant_user_active writes so reactivate can find the
    -- rows it deactivated. A hand-typed revoke reason must never be able to forge it (else an unrelated
    -- reactivate would silently un-revoke a revoked-for-cause assignment).
    IF btrim(p_reason) ILIKE '[deactivated]%' THEN
        RAISE EXCEPTION 'revoke reason may not start with the reserved "[deactivated]" prefix';
    END IF;
    SELECT tenant_id, revoked_at, user_id, role_id
      INTO v_tenant, v_revoked, v_user, v_role_id
    FROM platform.user_tenant_roles WHERE user_tenant_role_id = p_user_tenant_role_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'role assignment % not found', p_user_tenant_role_id;
    END IF;
    IF v_revoked IS NOT NULL THEN
        RETURN false;  -- idempotent: already revoked
    END IF;
    IF NOT platform.is_super_admin(p_actor_user_id) THEN
        IF v_tenant IS NULL THEN
            RAISE EXCEPTION 'actor % may not revoke a platform-level assignment', p_actor_user_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NOT platform.user_has_permission(p_actor_user_id, 'tenant.roles.assign', v_tenant) THEN
            RAISE EXCEPTION 'actor % may not revoke roles in tenant %', p_actor_user_id, v_tenant
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- Last-active-admin guard (permission-based, never role_key strings): refuse to
        -- strip the tenant's final administrator. Only bites when (a) the role being
        -- revoked confers an admin capability, (b) no OTHER active member of the tenant
        -- holds one, and (c) the target keeps no other active admin-conferring assignment.
        -- Prevents tenant-bricking via the role-revoke door (red-team V4). 23000 → 409.
        v_confers_admin := EXISTS (
            SELECT 1 FROM platform.role_permissions rp
            JOIN platform.permissions pm ON pm.permission_id = rp.permission_id
            WHERE rp.role_id = v_role_id
              AND pm.permission_key IN ('tenant.users.update', 'tenant.roles.assign'));
        IF v_confers_admin
           AND NOT platform.tenant_has_other_active_admin(v_tenant, v_user)
           AND NOT EXISTS (
               SELECT 1 FROM platform.user_tenant_roles utr2
               JOIN platform.role_permissions rp2 ON rp2.role_id = utr2.role_id
               JOIN platform.permissions pm2 ON pm2.permission_id = rp2.permission_id
               WHERE utr2.user_id = v_user AND utr2.tenant_id = v_tenant
                 AND utr2.user_tenant_role_id <> p_user_tenant_role_id
                 AND utr2.revoked_at IS NULL
                 AND (utr2.expires_at IS NULL OR utr2.expires_at > NOW())
                 AND pm2.permission_key IN ('tenant.users.update', 'tenant.roles.assign')) THEN
            RAISE EXCEPTION 'cannot revoke the last administrator''s access in tenant %', v_tenant
                USING ERRCODE = 'integrity_constraint_violation';
        END IF;
    END IF;
    UPDATE platform.user_tenant_roles
        SET revoked_at = NOW(), revoked_by = p_actor_user_id, revoked_reason = left(p_reason, 200)
        WHERE user_tenant_role_id = p_user_tenant_role_id;
    RETURN true;
END;
$$;
COMMENT ON FUNCTION platform.revoke_role_assignment IS
    'R3: soft-revoke an assignment; super_admin OR tenant.roles.assign in the assignment''s tenant. Idempotent. Last-active-admin guard prevents tenant-bricking.';

-- Grant/deny a single permission to a user (the per-user override escape hatch).
CREATE OR REPLACE FUNCTION platform.set_user_permission_override(
    p_actor_user_id UUID,
    p_user_id UUID,
    p_permission_id UUID,
    p_tenant_id UUID,
    p_is_allowed BOOLEAN,
    p_reason TEXT,
    p_effective_from TIMESTAMPTZ DEFAULT NULL,
    p_expires_at TIMESTAMPTZ DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_perm_key VARCHAR;
    v_perm_scope VARCHAR;
    v_id UUID;
BEGIN
    IF p_reason IS NULL OR length(btrim(p_reason)) = 0 THEN
        RAISE EXCEPTION 'an override reason is mandatory';
    END IF;
    SELECT permission_key, scope INTO v_perm_key, v_perm_scope
    FROM platform.permissions WHERE permission_id = p_permission_id;
    IF v_perm_key IS NULL THEN
        RAISE EXCEPTION 'unknown permission %', p_permission_id;
    END IF;

    IF NOT platform.is_super_admin(p_actor_user_id) THEN
        IF NOT platform.user_has_permission(p_actor_user_id, 'platform.overrides.grant', p_tenant_id) THEN
            RAISE EXCEPTION 'actor % may not manage overrides in tenant %', p_actor_user_id, p_tenant_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF v_perm_scope = 'platform' THEN
            RAISE EXCEPTION 'actor % may not override platform-scoped permission %', p_actor_user_id, v_perm_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- No escalation: GRANTING a permission requires the actor to hold it.
        IF p_is_allowed AND NOT platform.user_has_permission(p_actor_user_id, v_perm_key, p_tenant_id) THEN
            RAISE EXCEPTION 'actor % cannot grant permission % they do not hold', p_actor_user_id, v_perm_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;

    INSERT INTO platform.user_permission_overrides
        (user_id, permission_id, tenant_id, is_allowed, reason, granted_by_user_id, effective_from, expires_at)
    VALUES
        (p_user_id, p_permission_id, p_tenant_id, p_is_allowed, p_reason, p_actor_user_id,
         COALESCE(p_effective_from, NOW()), p_expires_at)
    ON CONFLICT (user_id, permission_id, tenant_id) DO UPDATE
        SET is_allowed = EXCLUDED.is_allowed, reason = EXCLUDED.reason,
            granted_by_user_id = EXCLUDED.granted_by_user_id, effective_from = EXCLUDED.effective_from,
            expires_at = EXCLUDED.expires_at, is_active = true,
            revoked_at = NULL, revoked_by_user_id = NULL, updated_at = NOW()
    RETURNING override_id INTO v_id;
    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.set_user_permission_override IS
    'R3: set a per-user permission override; super_admin OR platform.overrides.grant in-tenant, no platform-scope, no grant of a permission the actor lacks. Deny-wins preserved.';

-- Create a tenant-custom (non-system) role. Not an escalation vector by itself
-- (an empty role), but routed here so it works under R1 RLS for platform scope.
CREATE OR REPLACE FUNCTION platform.create_custom_role(
    p_actor_user_id UUID,
    p_role_key VARCHAR,
    p_name VARCHAR,
    p_description TEXT,
    p_tenant_id UUID,
    p_scope VARCHAR
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    IF NOT platform.is_super_admin(p_actor_user_id) THEN
        IF p_scope = 'platform' OR p_tenant_id IS NULL THEN
            RAISE EXCEPTION 'actor % may not create a platform-scoped role', p_actor_user_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NOT (platform.user_has_permission(p_actor_user_id, 'platform.roles.manage', p_tenant_id)
             OR platform.user_has_permission(p_actor_user_id, 'tenant.roles.assign', p_tenant_id)) THEN
            RAISE EXCEPTION 'actor % may not create roles in tenant %', p_actor_user_id, p_tenant_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;
    INSERT INTO platform.roles (role_key, name, description, tenant_id, scope, is_system, is_default)
    VALUES (p_role_key, p_name, p_description, p_tenant_id, p_scope, false, false)
    RETURNING role_id INTO v_id;
    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.create_custom_role IS
    'R3: create a non-system role; super_admin for platform scope, else platform.roles.manage/tenant.roles.assign in-tenant.';

-- Revoke a single permission from a role. Mirror of grant_permission_to_role's escalation guard,
-- plus a system-role guard: a non-super actor may never edit a built-in (is_system) role's matrix.
-- Idempotent — returns false when the permission was not granted on the role.
CREATE OR REPLACE FUNCTION platform.revoke_permission_from_role(
    p_actor_user_id UUID,
    p_role_id UUID,
    p_permission_id UUID,
    p_tenant_id UUID DEFAULT NULL
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_perm_key VARCHAR;
    v_perm_scope VARCHAR;
    v_is_system BOOLEAN;
    v_rows INT;
BEGIN
    SELECT permission_key, scope INTO v_perm_key, v_perm_scope
    FROM platform.permissions WHERE permission_id = p_permission_id;
    IF v_perm_key IS NULL THEN
        RAISE EXCEPTION 'unknown permission %', p_permission_id;
    END IF;

    SELECT is_system INTO v_is_system
    FROM platform.roles WHERE role_id = p_role_id AND deleted_at IS NULL;
    IF v_is_system IS NULL THEN
        RAISE EXCEPTION 'unknown or deleted role %', p_role_id;
    END IF;

    IF NOT platform.is_super_admin(p_actor_user_id) THEN
        -- Built-in roles are catalog artifacts; only super_admin may alter them.
        IF v_is_system THEN
            RAISE EXCEPTION 'actor % may not edit the matrix of system role %', p_actor_user_id, p_role_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- Non-super actors may never alter platform-scoped authority...
        IF v_perm_scope = 'platform' THEN
            RAISE EXCEPTION 'actor % may not alter platform-scoped permission %', p_actor_user_id, v_perm_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- ...and may only revoke a permission they themselves hold WITH grant option.
        IF NOT EXISTS (
            SELECT 1
            FROM platform.user_tenant_roles utr
            JOIN platform.role_permissions rp ON rp.role_id = utr.role_id
            WHERE utr.user_id = p_actor_user_id
              AND utr.revoked_at IS NULL
              AND (utr.expires_at IS NULL OR utr.expires_at > NOW())
              AND (utr.tenant_id = p_tenant_id OR utr.tenant_id IS NULL)
              AND rp.permission_id = p_permission_id
              AND rp.is_grantable = true
        ) THEN
            RAISE EXCEPTION 'actor % does not hold permission % with grant option', p_actor_user_id, v_perm_key
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;

    DELETE FROM platform.role_permissions
    WHERE role_id = p_role_id AND permission_id = p_permission_id;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows > 0;
END;
$$;
COMMENT ON FUNCTION platform.revoke_permission_from_role IS
    'R3: revoke a permission from a role only if super_admin OR (role is not system AND actor holds it with grant option AND it is not platform-scoped). Idempotent. Companion to grant_permission_to_role.';

-- Duplicate a role into a new custom (tenant) role, copying its permission grants atomically.
-- This is the "Duplicate built-in role" admin gesture. The no-escalation rule mirrors
-- assign_role_to_user: a non-super actor cannot mint a role conferring permissions they lack.
CREATE OR REPLACE FUNCTION platform.duplicate_role(
    p_actor_user_id UUID,
    p_source_role_id UUID,
    p_new_role_key VARCHAR,
    p_new_name VARCHAR,
    p_description TEXT,
    p_tenant_id UUID
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_source_scope VARCHAR;
    v_new_scope VARCHAR;
    v_new_id UUID;
BEGIN
    SELECT scope INTO v_source_scope
    FROM platform.roles WHERE role_id = p_source_role_id AND deleted_at IS NULL;
    IF v_source_scope IS NULL THEN
        RAISE EXCEPTION 'unknown or deleted source role %', p_source_role_id;
    END IF;

    -- A duplicate is always a non-system custom role. Super_admin may keep the source scope
    -- (e.g. clone a platform role); everyone else produces a tenant-scoped role.
    IF platform.is_super_admin(p_actor_user_id) THEN
        v_new_scope := v_source_scope;
    ELSE
        v_new_scope := 'tenant';
        IF v_source_scope = 'platform' OR p_tenant_id IS NULL THEN
            RAISE EXCEPTION 'actor % may not duplicate a platform-scoped role', p_actor_user_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NOT (platform.user_has_permission(p_actor_user_id, 'platform.roles.manage', p_tenant_id)
             OR platform.user_has_permission(p_actor_user_id, 'tenant.roles.assign', p_tenant_id)) THEN
            RAISE EXCEPTION 'actor % may not create roles in tenant %', p_actor_user_id, p_tenant_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- No escalation: every permission the source confers must be held by the actor.
        IF EXISTS (
            SELECT 1
            FROM platform.role_permissions rp
            JOIN platform.permissions pm ON pm.permission_id = rp.permission_id
            WHERE rp.role_id = p_source_role_id
              AND NOT platform.user_has_permission(p_actor_user_id, pm.permission_key, p_tenant_id)
        ) THEN
            RAISE EXCEPTION 'actor % cannot duplicate role % — it confers permissions the actor does not hold', p_actor_user_id, p_source_role_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;

    INSERT INTO platform.roles (role_key, name, description, tenant_id, scope, is_system, is_default)
    VALUES (p_new_role_key, p_new_name, p_description, p_tenant_id, v_new_scope, false, false)
    RETURNING role_id INTO v_new_id;

    -- Copy grants, but NEVER let a non-super actor mint a grant-option source: is_grantable is forced
    -- false for them, so a permission they hold WITHOUT grant option cannot become delegable via a clone.
    -- Only super_admin (who already holds everything with grant option) preserves the source flag.
    INSERT INTO platform.role_permissions (role_id, permission_id, granted_by, is_grantable)
    SELECT v_new_id, rp.permission_id, p_actor_user_id,
           CASE WHEN platform.is_super_admin(p_actor_user_id) THEN rp.is_grantable ELSE false END
    FROM platform.role_permissions rp
    WHERE rp.role_id = p_source_role_id;

    RETURN v_new_id;
END;
$$;
COMMENT ON FUNCTION platform.duplicate_role IS
    'R3: clone a role into a new custom role and copy its grants. Non-super actors get a tenant-scoped role and must hold every permission the source confers (no escalation).';

-- ============================================================================
-- R3 (catalog plane) — CREATE MODULES / PERMISSIONS (the "vocabulary")
-- ============================================================================
-- Modules (resource_types) and permissions are PLATFORM-LEVEL catalog: they define
-- the authority vocabulary the whole product speaks. Creating them is a platform-admin
-- act, gated on 'platform.permissions.manage' (super_admin holds it). These run as
-- definer so the authorization check is enforced at the database, consistent with the
-- grant/revoke/duplicate path — even though the catalog tables carry no RLS.
--
-- NOTE: a permission only *does* something once application code checks it
-- ([RequirePermission("…")]). create_permission adds the catalog row (makes it
-- grantable + visible in the matrix); enforcement ships with the feature that needs it.

CREATE OR REPLACE FUNCTION platform.create_resource_type(
    p_actor_user_id UUID,
    p_resource_key VARCHAR,
    p_resource_name VARCHAR,
    p_description TEXT DEFAULT NULL,
    p_display_order INT DEFAULT 0
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    IF NOT (platform.is_super_admin(p_actor_user_id)
         OR platform.user_has_permission(p_actor_user_id, 'platform.permissions.manage', NULL)) THEN
        RAISE EXCEPTION 'actor % may not manage the permission catalog', p_actor_user_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF EXISTS (SELECT 1 FROM platform.resource_types WHERE resource_key = p_resource_key) THEN
        RAISE EXCEPTION 'module % already exists', p_resource_key
            USING ERRCODE = 'unique_violation';
    END IF;

    INSERT INTO platform.resource_types (resource_key, resource_name, description, display_order, is_active)
    VALUES (p_resource_key, p_resource_name, p_description, p_display_order, true)
    RETURNING resource_type_id INTO v_id;
    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.create_resource_type IS
    'Catalog: create a module (resource_type). Gated on platform.permissions.manage (or super_admin).';

CREATE OR REPLACE FUNCTION platform.create_permission(
    p_actor_user_id UUID,
    p_permission_key VARCHAR,
    p_resource VARCHAR,
    p_action VARCHAR,
    p_scope VARCHAR,
    p_description TEXT,
    p_is_dangerous BOOLEAN DEFAULT false
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    IF NOT (platform.is_super_admin(p_actor_user_id)
         OR platform.user_has_permission(p_actor_user_id, 'platform.permissions.manage', NULL)) THEN
        RAISE EXCEPTION 'actor % may not manage the permission catalog', p_actor_user_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF p_scope NOT IN ('platform', 'tenant', 'self') THEN
        RAISE EXCEPTION 'invalid scope % (must be platform|tenant|self)', p_scope;
    END IF;
    IF EXISTS (SELECT 1 FROM platform.permissions WHERE permission_key = p_permission_key) THEN
        RAISE EXCEPTION 'permission % already exists', p_permission_key
            USING ERRCODE = 'unique_violation';
    END IF;

    -- Ensure the action exists in the registry so the matrix has a column label + danger flag.
    -- ON CONFLICT DO NOTHING: an existing action_key is left as-is (its is_dangerous is NOT refreshed) —
    -- the per-permission is_dangerous on the permission row is the source of truth for the cell anyway.
    INSERT INTO platform.action_types (action_key, action_name, is_dangerous)
    VALUES (p_action, INITCAP(REPLACE(p_action, '_', ' ')), p_is_dangerous)
    ON CONFLICT (action_key) DO NOTHING;

    -- is_system=false → a custom catalog entry (deletable later); link the module + action registries
    -- so the matrix groups/labels it correctly (NULL-safe if the module isn't registered yet).
    INSERT INTO platform.permissions
        (permission_key, resource, action, scope, description, is_system, is_dangerous, resource_type_id, action_type_id)
    SELECT p_permission_key, p_resource, p_action, p_scope, p_description, false, p_is_dangerous,
           rt.resource_type_id, at.action_type_id
    FROM (SELECT 1) _
    LEFT JOIN platform.resource_types rt ON rt.resource_key = p_resource
    LEFT JOIN platform.action_types  at ON at.action_key   = p_action
    RETURNING permission_id INTO v_id;

    -- Maintain the platform invariant: super_admin's role holds EVERY permission in the registry
    -- (the original seed did super_admin CROSS JOIN all permissions). A newly minted permission must be
    -- granted to super_admin too, or the "super_admin holds everything" guarantee silently regresses.
    INSERT INTO platform.role_permissions (role_id, permission_id, granted_by, is_grantable)
    SELECT r.role_id, v_id, p_actor_user_id, true
    FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
    ON CONFLICT (role_id, permission_id) DO NOTHING;

    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.create_permission IS
    'Catalog: create a permission (resource.action), is_system=false. Ensures the action_type exists and links resource/action registries. Gated on platform.permissions.manage (or super_admin). A permission is inert until application code checks it.';

-- ============================================================================
-- SHARED RLS PREDICATE HELPERS — defined here (before their first users, the
-- MODULE LICENSING tme_* policies below and the R1 RBAC-table policies further
-- down) so a fresh bundle build resolves them. They depend only on the tenant-
-- context accessors (current_tenant_id / current_impersonated_tenant /
-- current_is_super_admin), all defined earlier.
-- ============================================================================
-- Shared predicate helpers keep the policies terse and consistent.
CREATE OR REPLACE FUNCTION platform.rls_can_see_tenant(p_row_tenant UUID)
RETURNS BOOLEAN LANGUAGE SQL STABLE AS $$
    SELECT p_row_tenant IS NULL                                  -- global/system row
        OR p_row_tenant = platform.current_tenant_id()          -- own tenant
        OR p_row_tenant = platform.current_impersonated_tenant()-- scoped impersonation (R6)
        OR platform.current_is_super_admin();                   -- platform god-context
$$;

-- These predicates gate the RBAC/ENTITLEMENT tables only (roles, role_permissions,
-- user_tenant_roles, …) — NOT PHI (the PHI policies in 05 use current_impersonated_tenant()
-- and never the god-flag). The global super_admin context is intentionally retained here:
-- platform administration of roles/permissions/menus is a legitimate cross-tenant capability.
-- NOTE (audit Finding 4): a super_admin context satisfies rls_can_write_tenant for ANY tenant,
-- which would bypass the R3 grant-option/escalation guard on a DIRECT table write. This is safe
-- TODAY because the only sanctioned RBAC mutation path is the SECURITY DEFINER functions
-- (grant_permission_to_role / assign_role_to_user / …), which enforce R3 regardless of RLS.
-- Any future convenience path that writes these tables directly MUST route through those functions.
CREATE OR REPLACE FUNCTION platform.rls_can_write_tenant(p_row_tenant UUID)
RETURNS BOOLEAN LANGUAGE SQL STABLE AS $$
    -- Writes never target a NULL (global/system) row unless super_admin context.
    SELECT (p_row_tenant IS NOT NULL AND p_row_tenant = platform.current_tenant_id())
        OR platform.current_is_super_admin();
$$;

-- ============================================================================
-- MODULE LICENSING — a COMMERCIAL DISPLAY GATE, NOT a security boundary
-- ============================================================================
-- Per-tenant per-module entitlement that drives the matrix "Module not licensed"
-- state. DENYLIST semantics: a module is licensed for a tenant UNLESS an explicit
-- row says is_licensed=false (so default = all licensed, no backfill needed).
--
-- ⚠ Licensing is DISPLAY-ONLY. Permission resolution (resolve_user_permissions /
-- user_has_permission) NEVER consults this table — the RBAC boundary stays RBAC.
-- An unlicensed module only greys the cell in the admin matrix; it does not revoke
-- access. (If commercial enforcement is ever wanted, it belongs at the feature
-- entry point, explicitly — never silently inside permission resolution.)
CREATE TABLE IF NOT EXISTS platform.tenant_module_entitlements (
    entitlement_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    resource_type_id   UUID NOT NULL REFERENCES platform.resource_types(resource_type_id) ON DELETE CASCADE,
    is_licensed        BOOLEAN NOT NULL DEFAULT true,
    reason             TEXT,
    updated_by_user_id UUID REFERENCES platform.users(user_id),
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (tenant_id, resource_type_id)
);
-- Partial index: the matrix only ever queries the (rare) unlicensed rows per tenant.
CREATE INDEX IF NOT EXISTS idx_tme_tenant_unlicensed
    ON platform.tenant_module_entitlements(tenant_id) WHERE NOT is_licensed;

DROP TRIGGER IF EXISTS trg_tme_updated_at ON platform.tenant_module_entitlements;
CREATE TRIGGER trg_tme_updated_at BEFORE UPDATE ON platform.tenant_module_entitlements
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- RLS: tenant-scoped (own tenant + global + super/impersonation), like the entitlement tables.
ALTER TABLE platform.tenant_module_entitlements ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tme_read  ON platform.tenant_module_entitlements;
DROP POLICY IF EXISTS tme_write ON platform.tenant_module_entitlements;
CREATE POLICY tme_read  ON platform.tenant_module_entitlements FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
CREATE POLICY tme_write ON platform.tenant_module_entitlements FOR ALL
    USING (platform.rls_can_write_tenant(tenant_id))
    WITH CHECK (platform.rls_can_write_tenant(tenant_id));

-- docslot_app reads the table (RLS-scoped) for the matrix; writes go through the
-- definer setter only (sole-writer, same posture as the catalog tables).
GRANT SELECT ON platform.tenant_module_entitlements TO docslot_app;

-- Helper: is a module licensed for a tenant? Denylist — licensed unless an explicit false row.
CREATE OR REPLACE FUNCTION platform.module_is_licensed(p_tenant_id UUID, p_resource_key VARCHAR)
RETURNS BOOLEAN
LANGUAGE SQL STABLE
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
    SELECT NOT EXISTS (
        SELECT 1
        FROM platform.tenant_module_entitlements e
        JOIN platform.resource_types rt ON rt.resource_type_id = e.resource_type_id
        WHERE e.tenant_id = p_tenant_id
          AND rt.resource_key = p_resource_key
          AND e.is_licensed = false
    );
$$;
COMMENT ON FUNCTION platform.module_is_licensed IS
    'Display gate: true unless the tenant has an explicit is_licensed=false row for the module. NOT consulted by permission resolution.';

-- Setter (definer): set a tenant's module entitlement. Commercial/platform-admin act,
-- gated on platform.settings.update (super_admin holds it). Audited by the app layer.
CREATE OR REPLACE FUNCTION platform.set_module_license(
    p_actor_user_id UUID,
    p_tenant_id UUID,
    p_resource_type_id UUID,
    p_is_licensed BOOLEAN,
    p_reason TEXT DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    IF NOT (platform.is_super_admin(p_actor_user_id)
         OR platform.user_has_permission(p_actor_user_id, 'platform.settings.update', p_tenant_id)) THEN
        RAISE EXCEPTION 'actor % may not manage module licensing for tenant %', p_actor_user_id, p_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    INSERT INTO platform.tenant_module_entitlements
        (tenant_id, resource_type_id, is_licensed, reason, updated_by_user_id)
    VALUES (p_tenant_id, p_resource_type_id, p_is_licensed, p_reason, p_actor_user_id)
    ON CONFLICT (tenant_id, resource_type_id) DO UPDATE
        SET is_licensed = EXCLUDED.is_licensed, reason = EXCLUDED.reason,
            updated_by_user_id = EXCLUDED.updated_by_user_id, updated_at = NOW()
    RETURNING entitlement_id INTO v_id;
    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.set_module_license IS
    'Set a tenant''s per-module license (denylist). Gated on platform.settings.update (or super_admin). Display-only — does not affect access.';

-- ============================================================================
-- R1 — ROW-LEVEL SECURITY ON THE RBAC / ENTITLEMENT TABLES
-- ============================================================================
-- Until now RLS protected only the 5 PHI tables. The authorization data itself
-- had no DB-level isolation. We enable RLS with: read = own-tenant rows + global
-- (tenant_id IS NULL) rows + super_admin/impersonation; write = own-tenant rows
-- only (global/system rows are writable solely via super_admin / definer funcs).
--
-- docslot_app is NOBYPASSRLS and not the owner, so these policies bind to it.
-- The resolver functions above are SECURITY DEFINER, so login-time resolution
-- still sees what it needs even though direct table reads are tenant-scoped.
--
-- ⚠ DEPLOYMENT DEPENDENCY (audit Finding 1): once these *_write policies are live,
-- a PLATFORM super_admin or CROSS-TENANT admin write fails unless the request sets
-- app.is_super_admin transaction-locally (the PHI policies in 05 already expect this
-- GUC; it is currently never set by the app). Tenant-scoped admin writes are
-- unaffected. R1 MUST ship together with the app change that, inside the same
-- SET LOCAL transaction as app.tenant_id, runs:
--   SELECT set_config('app.is_super_admin', <true|false from validated JWT>, true);
-- and/or routes RBAC mutations through assign_role_to_user()/grant_permission_to_role()
-- (which additionally enforces the R3 escalation guard). Until then, keep R1 staged.

-- The shared predicate helpers (rls_can_see_tenant / rls_can_write_tenant) are
-- defined ABOVE, before the MODULE LICENSING section — its tme_read/tme_write
-- policies are the first users, so the definitions must precede them on a fresh build.

-- ---- roles -----------------------------------------------------------------
ALTER TABLE platform.roles ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS roles_read  ON platform.roles;
DROP POLICY IF EXISTS roles_write ON platform.roles;
CREATE POLICY roles_read  ON platform.roles FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
CREATE POLICY roles_write ON platform.roles FOR ALL
    USING (platform.rls_can_write_tenant(tenant_id))
    WITH CHECK (platform.rls_can_write_tenant(tenant_id));

-- ---- role_permissions (no tenant_id; derive via the owning role) -----------
ALTER TABLE platform.role_permissions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS role_perms_read  ON platform.role_permissions;
DROP POLICY IF EXISTS role_perms_write ON platform.role_permissions;
CREATE POLICY role_perms_read  ON platform.role_permissions FOR SELECT
    USING (EXISTS (SELECT 1 FROM platform.roles r
                   WHERE r.role_id = role_permissions.role_id
                     AND platform.rls_can_see_tenant(r.tenant_id)));
CREATE POLICY role_perms_write ON platform.role_permissions FOR ALL
    USING (EXISTS (SELECT 1 FROM platform.roles r
                   WHERE r.role_id = role_permissions.role_id
                     AND platform.rls_can_write_tenant(r.tenant_id)))
    WITH CHECK (EXISTS (SELECT 1 FROM platform.roles r
                   WHERE r.role_id = role_permissions.role_id
                     AND platform.rls_can_write_tenant(r.tenant_id)));

-- ---- user_tenant_roles -----------------------------------------------------
ALTER TABLE platform.user_tenant_roles ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS utr_read  ON platform.user_tenant_roles;
DROP POLICY IF EXISTS utr_write ON platform.user_tenant_roles;
CREATE POLICY utr_read  ON platform.user_tenant_roles FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
CREATE POLICY utr_write ON platform.user_tenant_roles FOR ALL
    USING (platform.rls_can_write_tenant(tenant_id))
    WITH CHECK (platform.rls_can_write_tenant(tenant_id));

-- ---- user_permission_overrides ---------------------------------------------
ALTER TABLE platform.user_permission_overrides ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS upo_read  ON platform.user_permission_overrides;
DROP POLICY IF EXISTS upo_write ON platform.user_permission_overrides;
CREATE POLICY upo_read  ON platform.user_permission_overrides FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
CREATE POLICY upo_write ON platform.user_permission_overrides FOR ALL
    USING (platform.rls_can_write_tenant(tenant_id))
    WITH CHECK (platform.rls_can_write_tenant(tenant_id));

-- ---- tenant_product_subscriptions (entitlement — read own, write super only)
ALTER TABLE platform.tenant_product_subscriptions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tps_read  ON platform.tenant_product_subscriptions;
DROP POLICY IF EXISTS tps_write ON platform.tenant_product_subscriptions;
CREATE POLICY tps_read  ON platform.tenant_product_subscriptions FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
-- A tenant must NOT be able to self-grant entitlement; writes are super_admin only.
CREATE POLICY tps_write ON platform.tenant_product_subscriptions FOR ALL
    USING (platform.current_is_super_admin())
    WITH CHECK (platform.current_is_super_admin());

-- ---- navigation_menus (global menus tenant_id IS NULL; tenant-custom otherwise)
ALTER TABLE platform.navigation_menus ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS nav_read  ON platform.navigation_menus;
DROP POLICY IF EXISTS nav_write ON platform.navigation_menus;
CREATE POLICY nav_read  ON platform.navigation_menus FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
-- WITH CHECK forbids a tenant inserting a tenant_id IS NULL (global) menu.
CREATE POLICY nav_write ON platform.navigation_menus FOR ALL
    USING (platform.rls_can_write_tenant(tenant_id))
    WITH CHECK (platform.rls_can_write_tenant(tenant_id));

-- ---- menu_permissions (no tenant_id; derive via the owning menu) -----------
ALTER TABLE platform.menu_permissions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS menu_perms_read  ON platform.menu_permissions;
DROP POLICY IF EXISTS menu_perms_write ON platform.menu_permissions;
CREATE POLICY menu_perms_read  ON platform.menu_permissions FOR SELECT
    USING (EXISTS (SELECT 1 FROM platform.navigation_menus m
                   WHERE m.menu_id = menu_permissions.menu_id
                     AND platform.rls_can_see_tenant(m.tenant_id)));
CREATE POLICY menu_perms_write ON platform.menu_permissions FOR ALL
    USING (EXISTS (SELECT 1 FROM platform.navigation_menus m
                   WHERE m.menu_id = menu_permissions.menu_id
                     AND platform.rls_can_write_tenant(m.tenant_id)))
    WITH CHECK (EXISTS (SELECT 1 FROM platform.navigation_menus m
                   WHERE m.menu_id = menu_permissions.menu_id
                     AND platform.rls_can_write_tenant(m.tenant_id)));

-- ============================================================================
-- USER LIFECYCLE (tenant-scoped) — invite is fixed in the .NET handler; these are
-- the deactivate / reactivate / edit-profile / reset-access write paths.
-- ----------------------------------------------------------------------------
-- Design notes (all four are SECURITY DEFINER, the actor is ALWAYS the authenticated
-- principal passed by the API — never a body field):
--   * platform.users / user_tenant_roles are GLOBAL tables (a user is one identity
--     across tenants). RLS does not row-filter them usefully, so EVERY function
--     re-checks the actor's permission AND scopes the target to THIS tenant by
--     requiring a membership row — the cross-cutting catch on the global tables.
--   * "Deactivate in a tenant" = soft-revoke the target's active memberships IN THIS
--     TENANT (marked so reactivate can restore exactly them). It NEVER flips the
--     global users.is_active — a tenant-scoped permission must not have cross-tenant
--     blast radius (red-team V1). The global flip is a super_admin/break-glass action.
--   * Reactivate re-runs the escalation guard per role (restore only roles the actor
--     may currently assign) so reactivation is not an escalation-via-the-revoke door.
-- ============================================================================

-- Does the tenant have an active admin OTHER than p_excluding_user_id? "Admin" is
-- permission-based (holds tenant.users.update or tenant.roles.assign), never a role
-- name — honoring the no-hardcoded-roles invariant. Used by the last-admin guards.
CREATE OR REPLACE FUNCTION platform.tenant_has_other_active_admin(
    p_tenant_id UUID,
    p_excluding_user_id UUID
) RETURNS BOOLEAN
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1
        FROM (
            SELECT DISTINCT utr.user_id
            FROM platform.user_tenant_roles utr
            WHERE utr.tenant_id = p_tenant_id
              AND utr.revoked_at IS NULL
              AND (utr.expires_at IS NULL OR utr.expires_at > NOW())
              AND utr.user_id <> p_excluding_user_id
        ) u
        WHERE platform.user_has_permission(u.user_id, 'tenant.users.update', p_tenant_id)
           OR platform.user_has_permission(u.user_id, 'tenant.roles.assign', p_tenant_id)
    );
END;
$$;
COMMENT ON FUNCTION platform.tenant_has_other_active_admin IS
    'True if some OTHER active member of the tenant holds an admin capability (tenant.users.update/roles.assign). Permission-based, not role-name-based.';

-- Deactivate (revoke all active memberships in this tenant) or reactivate (restore the
-- ones this routine revoked, re-running the escalation guard). Tenant-scoped only.
CREATE OR REPLACE FUNCTION platform.set_tenant_user_active(
    p_actor_user_id UUID,
    p_target_user_id UUID,
    p_tenant_id UUID,
    p_is_active BOOLEAN,
    p_reason TEXT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    -- Reserved, NON-user-writable marker: revoke_role_assignment rejects any reason starting with
    -- '[deactivated]' (see its guard), so ONLY this routine can mark a revocation as a deactivation.
    -- That stops a hand-typed revoke reason from being silently un-revoked by a later reactivate.
    v_marker     CONSTANT TEXT := '[deactivated] ';
    v_row        RECORD;
    v_restored   INT := 0;
    v_marked     INT := 0;
BEGIN
    IF p_reason IS NULL OR length(btrim(p_reason)) = 0 THEN
        RAISE EXCEPTION 'a reason is mandatory';
    END IF;
    -- Permission re-check (defense in depth behind [RequirePermission]).
    IF NOT platform.is_super_admin(p_actor_user_id)
       AND NOT platform.user_has_permission(p_actor_user_id, 'tenant.users.update', p_tenant_id) THEN
        RAISE EXCEPTION 'actor % may not manage users in tenant %', p_actor_user_id, p_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    -- Tenant-membership scoping: target must belong to THIS tenant (active OR revoked —
    -- reactivation operates on revoked rows).
    IF NOT EXISTS (
        SELECT 1 FROM platform.user_tenant_roles
        WHERE user_id = p_target_user_id AND tenant_id = p_tenant_id
    ) THEN
        RAISE EXCEPTION 'user % is not a member of tenant %', p_target_user_id, p_tenant_id
            USING ERRCODE = 'no_data_found';
    END IF;

    IF NOT p_is_active THEN
        -- DEACTIVATE -------------------------------------------------------------
        IF p_actor_user_id = p_target_user_id THEN
            RAISE EXCEPTION 'you cannot deactivate your own access'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        -- Last-active-admin guard: refuse if the target is the tenant's only admin.
        IF NOT platform.is_super_admin(p_actor_user_id)
           AND NOT platform.tenant_has_other_active_admin(p_tenant_id, p_target_user_id)
           AND (platform.user_has_permission(p_target_user_id, 'tenant.users.update', p_tenant_id)
                OR platform.user_has_permission(p_target_user_id, 'tenant.roles.assign', p_tenant_id)) THEN
            RAISE EXCEPTION 'cannot deactivate the last administrator of tenant %', p_tenant_id
                USING ERRCODE = 'integrity_constraint_violation';
        END IF;
        UPDATE platform.user_tenant_roles
            SET revoked_at = NOW(),
                revoked_by = p_actor_user_id,
                revoked_reason = left(v_marker || p_reason, 200)
            WHERE user_id = p_target_user_id
              AND tenant_id = p_tenant_id
              AND revoked_at IS NULL;
        RETURN true;
    ELSE
        -- REACTIVATE -------------------------------------------------------------
        SELECT count(*) INTO v_marked
        FROM platform.user_tenant_roles
        WHERE user_id = p_target_user_id AND tenant_id = p_tenant_id
          AND revoked_at IS NOT NULL AND revoked_reason LIKE v_marker || '%';
        IF v_marked = 0 THEN
            RETURN true;  -- nothing this routine deactivated → idempotent no-op
        END IF;
        FOR v_row IN
            SELECT utr.user_tenant_role_id, utr.role_id, r.scope AS role_scope
            FROM platform.user_tenant_roles utr
            JOIN platform.roles r ON r.role_id = utr.role_id AND r.deleted_at IS NULL
            WHERE utr.user_id = p_target_user_id
              AND utr.tenant_id = p_tenant_id
              AND utr.revoked_at IS NOT NULL
              AND utr.revoked_reason LIKE v_marker || '%'
        LOOP
            -- Restore only roles the actor may CURRENTLY assign (inline no-escalation
            -- guard, mirroring assign_role_to_user — including its platform-scope reject).
            IF platform.is_super_admin(p_actor_user_id)
               OR (
                   v_row.role_scope <> 'platform'
                   AND platform.user_has_permission(p_actor_user_id, 'tenant.roles.assign', p_tenant_id)
                   AND NOT EXISTS (
                       SELECT 1 FROM platform.role_permissions rp
                       JOIN platform.permissions pm ON pm.permission_id = rp.permission_id
                       WHERE rp.role_id = v_row.role_id
                         AND NOT platform.user_has_permission(p_actor_user_id, pm.permission_key, p_tenant_id)
                   )
               ) THEN
                UPDATE platform.user_tenant_roles
                    SET revoked_at = NULL, revoked_by = NULL, revoked_reason = NULL
                    WHERE user_tenant_role_id = v_row.user_tenant_role_id;
                v_restored := v_restored + 1;
            END IF;
        END LOOP;
        IF v_restored = 0 THEN
            RAISE EXCEPTION 'no roles could be restored — you may not assign this user''s roles in tenant %', p_tenant_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        RETURN true;
    END IF;
END;
$$;
COMMENT ON FUNCTION platform.set_tenant_user_active IS
    'Deactivate (soft-revoke all active memberships in the tenant, marked) or reactivate (restore marked ones, re-running the escalation guard). Tenant-scoped; never touches global users.is_active. Self-guard + last-admin guard.';

-- Edit a user's profile (whitelisted columns only). Self-edit allowed (benign).
CREATE OR REPLACE FUNCTION platform.update_user_profile(
    p_actor_user_id UUID,
    p_target_user_id UUID,
    p_tenant_id UUID,
    p_full_name VARCHAR,
    p_phone VARCHAR,
    p_preferred_language VARCHAR
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
BEGIN
    IF p_full_name IS NULL OR length(btrim(p_full_name)) = 0 THEN
        RAISE EXCEPTION 'full name is required';
    END IF;
    IF p_preferred_language IS NULL OR p_preferred_language NOT IN ('en', 'hi') THEN
        RAISE EXCEPTION 'preferred language must be en or hi';
    END IF;
    IF NOT platform.is_super_admin(p_actor_user_id)
       AND NOT platform.user_has_permission(p_actor_user_id, 'tenant.users.update', p_tenant_id) THEN
        RAISE EXCEPTION 'actor % may not edit users in tenant %', p_actor_user_id, p_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM platform.user_tenant_roles
        WHERE user_id = p_target_user_id AND tenant_id = p_tenant_id
    ) THEN
        RAISE EXCEPTION 'user % is not a member of tenant %', p_target_user_id, p_tenant_id
            USING ERRCODE = 'no_data_found';
    END IF;
    -- Whitelist: full_name, phone, preferred_language ONLY. Email/auth/status/mfa
    -- are never mutable here (red-team A12 identity-takeover).
    UPDATE platform.users
        SET full_name          = left(btrim(p_full_name), 200),
            phone               = NULLIF(btrim(p_phone), ''),
            preferred_language  = p_preferred_language,
            updated_at          = NOW()
        WHERE user_id = p_target_user_id AND deleted_at IS NULL;
    RETURN true;
END;
$$;
COMMENT ON FUNCTION platform.update_user_profile IS
    'Edit full_name/phone/preferred_language only; tenant.users.update + tenant membership. Email/auth/status untouchable.';

-- Reset/unlock access: flags only — force a password change, clear the lockout.
-- Never generates/returns/stores plaintext; never nulls password_hash (would break
-- chk_user_has_auth); never advances password_changed_at.
CREATE OR REPLACE FUNCTION platform.reset_user_access(
    p_actor_user_id UUID,
    p_target_user_id UUID,
    p_tenant_id UUID,
    p_reason TEXT
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
BEGIN
    IF p_reason IS NULL OR length(btrim(p_reason)) = 0 THEN
        RAISE EXCEPTION 'a reason is mandatory';
    END IF;
    IF p_actor_user_id = p_target_user_id THEN
        RAISE EXCEPTION 'you cannot reset your own access — use self-service recovery'
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NOT platform.is_super_admin(p_actor_user_id)
       AND NOT platform.user_has_permission(p_actor_user_id, 'tenant.users.update', p_tenant_id) THEN
        RAISE EXCEPTION 'actor % may not reset access in tenant %', p_actor_user_id, p_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM platform.user_tenant_roles
        WHERE user_id = p_target_user_id AND tenant_id = p_tenant_id
    ) THEN
        RAISE EXCEPTION 'user % is not a member of tenant %', p_target_user_id, p_tenant_id
            USING ERRCODE = 'no_data_found';
    END IF;
    UPDATE platform.users
        SET must_change_password = true,
            locked_until         = NULL,
            failed_login_count   = 0,
            updated_at           = NOW()
        WHERE user_id = p_target_user_id AND deleted_at IS NULL;
    RETURN true;
END;
$$;
COMMENT ON FUNCTION platform.reset_user_access IS
    'Force password change + clear lockout (flags only; no plaintext, preserves chk_user_has_auth). Self-guard; tenant.users.update + membership.';

-- ============================================================================
-- GRANTS for the new objects (docslot_app is least-privilege; see file 10)
-- ============================================================================
-- New tables follow the same SELECT/INSERT/UPDATE pattern; impersonation +
-- role_incompatibility are not append-only audit tables but should not be DELETEd
-- by the app (lifecycle via ended_at / soft semantics).
-- No direct INSERT: sessions are created ONLY via begin_impersonation() (definer).
-- SELECT/UPDATE are further confined to a super_admin context by RLS + append-only trigger.
GRANT SELECT, UPDATE ON platform.impersonation_sessions TO docslot_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON platform.role_incompatibility TO docslot_app;

-- EXECUTE on the new/redefined functions (definer funcs run as owner regardless,
-- but the app still needs EXECUTE to call them).
GRANT EXECUTE ON FUNCTION
    platform.tenant_is_serviceable(UUID),
    platform.resolve_user_permissions(UUID, UUID),
    platform.user_has_permission(UUID, VARCHAR, UUID),
    platform.get_user_menus(UUID, UUID, VARCHAR, VARCHAR),
    platform.user_memberships(UUID),
    platform.current_impersonated_tenant(),
    platform.begin_impersonation(UUID, UUID, TEXT, UUID, INTERVAL, BOOLEAN),
    platform.end_impersonation(UUID, UUID),
    platform.list_impersonation_sessions(INT),
    platform.is_super_admin(UUID),
    platform.grant_permission_to_role(UUID, UUID, UUID, UUID, BOOLEAN),
    platform.revoke_permission_from_role(UUID, UUID, UUID, UUID),
    platform.duplicate_role(UUID, UUID, VARCHAR, VARCHAR, TEXT, UUID),
    platform.create_resource_type(UUID, VARCHAR, VARCHAR, TEXT, INT),
    platform.create_permission(UUID, VARCHAR, VARCHAR, VARCHAR, VARCHAR, TEXT, BOOLEAN),
    platform.module_is_licensed(UUID, VARCHAR),
    platform.set_module_license(UUID, UUID, UUID, BOOLEAN, TEXT),
    platform.assign_role_to_user(UUID, UUID, UUID, UUID),
    platform.revoke_role_assignment(UUID, UUID, TEXT),
    platform.set_user_permission_override(UUID, UUID, UUID, UUID, BOOLEAN, TEXT, TIMESTAMPTZ, TIMESTAMPTZ),
    platform.tenant_has_other_active_admin(UUID, UUID),
    platform.set_tenant_user_active(UUID, UUID, UUID, BOOLEAN, TEXT),
    platform.update_user_profile(UUID, UUID, UUID, VARCHAR, VARCHAR, VARCHAR),
    platform.reset_user_access(UUID, UUID, UUID, TEXT),
    platform.create_custom_role(UUID, VARCHAR, VARCHAR, TEXT, UUID, VARCHAR),
    platform.rls_can_see_tenant(UUID),
    platform.rls_can_write_tenant(UUID)
TO docslot_app;

-- ----------------------------------------------------------------------------
-- Catalog write path: the SECURITY DEFINER functions are the SOLE writers.
-- The catalog tables (permissions / resource_types / action_types) carry no RLS
-- (they are global vocabulary, no tenant rows to isolate). To stop the app role
-- from bypassing the create_permission/create_resource_type authorization check
-- with a direct INSERT, revoke its direct write grants — keeping SELECT for the
-- read-only matrix/catalog projections. The definer funcs run as owner and are
-- unaffected. (Mirrors how R1 RLS makes the definer funcs the sole writers of the
-- entitlement tables; for the RLS-less catalog tables we achieve it via REVOKE.)
-- ----------------------------------------------------------------------------
REVOKE INSERT, UPDATE ON platform.permissions     FROM docslot_app;
REVOKE INSERT, UPDATE ON platform.resource_types  FROM docslot_app;
REVOKE INSERT, UPDATE ON platform.action_types    FROM docslot_app;

-- ============================================================================
-- POST-CONDITIONS (fail loud if the hardening did not take)
-- ============================================================================
DO $verify$
DECLARE
    v_rls_count INT;
BEGIN
    SELECT count(*) INTO v_rls_count
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'platform'
      AND c.relname IN ('roles','role_permissions','user_tenant_roles',
                        'user_permission_overrides','tenant_product_subscriptions',
                        'navigation_menus','menu_permissions')
      AND c.relrowsecurity = true;
    IF v_rls_count <> 7 THEN
        RAISE EXCEPTION 'R1 incomplete: expected RLS on 7 RBAC tables, found %', v_rls_count;
    END IF;
    RAISE NOTICE 'RBAC hardening applied: RLS on % RBAC tables, R2 tenant gate, R3 grant guard, R4 menu ancestors, R5 SoD, R6 scoped impersonation.', v_rls_count;
END $verify$;

-- ============================================================================
-- END OF RBAC HARDENING
-- ============================================================================
-- New tables: impersonation_sessions, role_incompatibility (2)
-- New columns: role_permissions.is_grantable
-- New/redefined functions: tenant_is_serviceable, resolve_user_permissions*,
--   user_has_permission*, get_user_menus*, current_impersonated_tenant,
--   begin_impersonation, end_impersonation, list_impersonation_sessions,
--   is_super_admin, grant_permission_to_role,
--   assign_role_to_user, enforce_role_sod, rls_can_see/write_tenant
--   (* = redefined to be tenant-gated + SECURITY DEFINER)
-- RLS enabled: roles, role_permissions, user_tenant_roles,
--   user_permission_overrides, tenant_product_subscriptions, navigation_menus,
--   menu_permissions (7)
-- FOLLOW-UPS (not in this slice): R7 perm_epoch cache invalidation, R8 feature-
--   level entitlement intersection, R9 SCIM/multi-IdP, optimistic concurrency.
-- ============================================================================


DO $section_end$
BEGIN
    RAISE NOTICE '--- PART 11/11: RBAC Hardening complete ---';
    RAISE NOTICE '';
END $section_end$;

-- ============================================================================
-- END OF BUNDLE
-- ============================================================================
