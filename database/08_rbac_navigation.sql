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
    m_portal UUID;
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

    -- Care Partner SELF-SERVICE portal (/portal). Distinct audience from the admin
    -- "Care Partners" screen above: this is the partner's OWN wallet/links/book-on-
    -- behalf surface, gated on the self-scoped commission.broker.read_self (held by
    -- the 'broker' role). The server resolves broker_id from the JWT — the menu is
    -- visible to anyone holding read_self; non-broker holders (e.g. tenant_owner)
    -- simply see an empty self-scoped portal. Placed adjacent to Care Partners for
    -- admins; for a broker it is the primary surface alongside Overview.
    INSERT INTO platform.navigation_menus (menu_key, menu_label, menu_label_hi, menu_icon, menu_url, display_order, applies_to_tenant_types)
    VALUES ('partner_portal', 'My Portal', 'मेरा पोर्टल', 'wallet', '/portal', 85, NULL)
    RETURNING menu_id INTO m_portal;

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

    -- My Portal (partner self-service) → commission.broker.read_self (self-scoped).
    -- This is the SELF read, NOT the tenant-wide commission.broker.read that gates
    -- the admin Care Partners screen — so an admin who lacks read_self never sees it,
    -- and a broker (who holds only the *_self keys) sees Portal but not the admin screen.
    INSERT INTO platform.menu_permissions (menu_id, permission_id)
    SELECT m_portal, p.permission_id FROM platform.permissions p
    WHERE p.permission_key = 'commission.broker.read_self' ON CONFLICT DO NOTHING;

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
--        Analytics, Care Partners+children, My Portal (partner self-service),
--        Team & Roles, Developers, Security & Compliance, Settings+children),
--        bilingual + tenant_type-aware, menu→perm maps. "My Portal" is gated on the
--        self-scoped commission.broker.read_self (the broker's own surface), distinct
--        from the admin "Care Partners" screen gated on tenant-wide commission.broker.read.
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
