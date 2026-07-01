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
-- INVITATIONS (issue #89, epic #80 Phase C) — token-based tenant onboarding
-- ============================================================================
-- A NEW capability that sits ALONGSIDE the existing direct-add invite
-- (POST /tenants/{id}/users → provision_user + assign_role_to_user). Instead of
-- an admin conferring a role synchronously, an admin mints a single-use, hashed,
-- expiring token; the invitee redeems it to self-provision (set their own
-- password + display name) and receive the pre-vetted role. Only a SHA-256 HASH
-- of the token is ever stored — the plaintext is returned exactly once at
-- create/resend time and is never re-fetchable.
--
-- All writes travel SECURITY DEFINER functions (mirrors the RBAC write path):
--   * create/resend/revoke re-check the actor's tenant.users.create at the DB and
--     RE-USE the assign_role_to_user escalation guard (R3 no-escalation) so an
--     actor can only ever invite to a role they may themselves confer.
--   * accept is UNAUTHENTICATED — the token IS the authorization. It runs with no
--     app.tenant_id / actor context, so it MUST be a definer function (RLS would
--     otherwise hide the row + block the write). It is single-use and atomic.
-- The table itself carries tenant RLS (rls_can_see/write_tenant) so the LIST read
-- (a normal tenant-scoped query) is bounded to the caller's tenant.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS platform.invitations (
    invitation_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    invited_email       CITEXT NOT NULL,
    role_id             UUID REFERENCES platform.roles(role_id),           -- role to grant on accept (nullable)
    invited_by_user_id  UUID REFERENCES platform.users(user_id),
    token_hash          TEXT NOT NULL,                                     -- SHA-256 of the random token; plaintext NEVER stored
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
                          CHECK (status IN ('pending','accepted','revoked','expired')),
    expires_at          TIMESTAMPTZ NOT NULL,
    resend_count        INT NOT NULL DEFAULT 0,
    accepted_user_id    UUID REFERENCES platform.users(user_id),
    accepted_at         TIMESTAMPTZ,
    revoked_at          TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- List/badge access pattern: the console lists invites for a tenant filtered by status.
CREATE INDEX IF NOT EXISTS idx_invitations_tenant_status
    ON platform.invitations(tenant_id, status);
-- At most ONE live pending invite per (tenant, email) — a second create collides (23505 → 409).
-- Partial: revoked/accepted/expired rows do NOT block re-inviting the same address later.
CREATE UNIQUE INDEX IF NOT EXISTS uq_invitations_pending_email
    ON platform.invitations(tenant_id, invited_email) WHERE status = 'pending';

DROP TRIGGER IF EXISTS trg_invitations_updated_at ON platform.invitations;
CREATE TRIGGER trg_invitations_updated_at BEFORE UPDATE ON platform.invitations
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- RLS: tenant-scoped (own tenant + super/impersonation), mirroring tenant_module_entitlements.
ALTER TABLE platform.invitations ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS invitations_read  ON platform.invitations;
DROP POLICY IF EXISTS invitations_write ON platform.invitations;
CREATE POLICY invitations_read  ON platform.invitations FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
CREATE POLICY invitations_write ON platform.invitations FOR ALL
    USING (platform.rls_can_write_tenant(tenant_id))
    WITH CHECK (platform.rls_can_write_tenant(tenant_id));

-- Explicit grant: this table is created AFTER file 10's blanket grant ran. SELECT-only — every write
-- travels a SECURITY DEFINER function (runs as owner), so the app role must NOT hold direct INSERT/UPDATE:
-- a raw own-tenant write would bypass the R3 no-escalation guard (rls_can_write_tenant does not enforce it).
-- The REVOKE is defensive/idempotent for post-hoc application over an already-granted live DB.
GRANT SELECT ON platform.invitations TO docslot_app;
REVOKE INSERT, UPDATE ON platform.invitations FROM docslot_app;

-- ---- create_invitation: mint a pending, hashed, expiring invite -----------------------------------
-- NOTE: p_invited_email is TEXT (not CITEXT) so a driver-sent text parameter resolves to this overload —
-- PostgreSQL will not implicitly coerce text→citext during FUNCTION resolution. The invited_email COLUMN is
-- still CITEXT, so the INSERT assignment-casts it (case-insensitive identity is preserved at rest).
CREATE OR REPLACE FUNCTION platform.create_invitation(
    p_actor_user_id UUID,
    p_tenant_id     UUID,
    p_invited_email TEXT,
    p_token_hash    TEXT,
    p_expires_at    TIMESTAMPTZ,
    p_role_id       UUID DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_role_scope VARCHAR;
    v_role_key   VARCHAR;
    v_id         UUID;
BEGIN
    -- Actor must be allowed to invite users into THIS tenant.
    IF NOT platform.is_super_admin(p_actor_user_id)
       AND NOT platform.user_has_permission(p_actor_user_id, 'tenant.users.create', p_tenant_id) THEN
        RAISE EXCEPTION 'actor % may not invite users in tenant %', p_actor_user_id, p_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    -- R3 no-escalation: if a role is pre-attached, the actor must be able to CONFER it — identical guard
    -- to assign_role_to_user, applied HERE at mint time so the token can never grant more than the inviter
    -- holds (the actual assignment at accept time is unauthenticated and trusts this decision).
    IF p_role_id IS NOT NULL THEN
        SELECT scope, role_key INTO v_role_scope, v_role_key
        FROM platform.roles WHERE role_id = p_role_id AND deleted_at IS NULL;
        IF v_role_key IS NULL THEN
            RAISE EXCEPTION 'unknown or deleted role %', p_role_id;
        END IF;

        IF NOT platform.is_super_admin(p_actor_user_id) THEN
            IF v_role_scope = 'platform' THEN
                RAISE EXCEPTION 'actor % may not invite to platform-scoped role %', p_actor_user_id, v_role_key
                    USING ERRCODE = 'insufficient_privilege';
            END IF;
            IF NOT platform.user_has_permission(p_actor_user_id, 'tenant.roles.assign', p_tenant_id) THEN
                RAISE EXCEPTION 'actor % may not confer roles in tenant %', p_actor_user_id, p_tenant_id
                    USING ERRCODE = 'insufficient_privilege';
            END IF;
            IF EXISTS (
                SELECT 1
                FROM platform.role_permissions rp
                JOIN platform.permissions pm ON pm.permission_id = rp.permission_id
                WHERE rp.role_id = p_role_id
                  AND NOT platform.user_has_permission(p_actor_user_id, pm.permission_key, p_tenant_id)
            ) THEN
                RAISE EXCEPTION 'actor % cannot invite to role % — it confers permissions the actor does not hold', p_actor_user_id, v_role_key
                    USING ERRCODE = 'insufficient_privilege';
            END IF;
        END IF;
    END IF;

    -- One live pending invite per (tenant, email); the partial unique index raises 23505 on a duplicate.
    INSERT INTO platform.invitations
        (tenant_id, invited_email, role_id, invited_by_user_id, token_hash, expires_at)
    VALUES
        (p_tenant_id, p_invited_email, p_role_id, p_actor_user_id, p_token_hash, p_expires_at)
    RETURNING invitation_id INTO v_id;

    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.create_invitation IS
    'Mint a single-use pending invitation (hashed token). Actor needs tenant.users.create; a pre-attached role re-uses the R3 no-escalation guard. One live pending invite per (tenant,email).';

-- ---- resend_invitation: rotate the token + extend expiry, bump the counter -------------------------
CREATE OR REPLACE FUNCTION platform.resend_invitation(
    p_actor_user_id  UUID,
    p_tenant_id      UUID,
    p_invitation_id  UUID,
    p_new_token_hash TEXT,
    p_new_expires_at TIMESTAMPTZ
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    IF NOT platform.is_super_admin(p_actor_user_id)
       AND NOT platform.user_has_permission(p_actor_user_id, 'tenant.users.create', p_tenant_id) THEN
        RAISE EXCEPTION 'actor % may not resend invitations in tenant %', p_actor_user_id, p_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    -- Only a still-PENDING invite is resendable; rotating the hash invalidates any previously-issued token.
    UPDATE platform.invitations
        SET token_hash   = p_new_token_hash,
            expires_at   = p_new_expires_at,
            resend_count = resend_count + 1,
            updated_at   = NOW()
        WHERE invitation_id = p_invitation_id
          AND tenant_id     = p_tenant_id
          AND status        = 'pending'
    RETURNING invitation_id INTO v_id;

    IF v_id IS NULL THEN
        RAISE EXCEPTION 'invitation % is not pending in tenant %', p_invitation_id, p_tenant_id
            USING ERRCODE = 'no_data_found';
    END IF;
    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.resend_invitation IS
    'Rotate an invitation token + extend expiry (bumps resend_count). Pending-only; needs tenant.users.create. Invalidates any prior token for the invite.';

-- ---- revoke_invitation: cancel a pending invite ---------------------------------------------------
CREATE OR REPLACE FUNCTION platform.revoke_invitation(
    p_actor_user_id UUID,
    p_tenant_id     UUID,
    p_invitation_id UUID
) RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    IF NOT platform.is_super_admin(p_actor_user_id)
       AND NOT platform.user_has_permission(p_actor_user_id, 'tenant.users.create', p_tenant_id) THEN
        RAISE EXCEPTION 'actor % may not revoke invitations in tenant %', p_actor_user_id, p_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    UPDATE platform.invitations
        SET status     = 'revoked',
            revoked_at = NOW(),
            updated_at = NOW()
        WHERE invitation_id = p_invitation_id
          AND tenant_id     = p_tenant_id
          AND status        = 'pending'
    RETURNING invitation_id INTO v_id;

    RETURN v_id IS NOT NULL;   -- false when already accepted/revoked/expired (idempotent)
END;
$$;
COMMENT ON FUNCTION platform.revoke_invitation IS
    'Revoke a pending invitation (status=revoked). Needs tenant.users.create. Idempotent: returns false if not pending.';

-- ---- accept_invitation: UNAUTHENTICATED redemption (the token is the authorization) ---------------
-- Runs with NO actor/tenant context, so it is a definer function that bypasses RLS. Constant-time-ish:
-- a garbage/expired/revoked/already-used token all raise the SAME no_data_found error (no enumeration).
-- Provisions the user (or LINKS an existing global identity by email without overwriting the profile),
-- assigns the pre-vetted role (no re-escalation check — the mint already vetted it), marks accepted.
-- OUT columns are out_*-prefixed so they can't collide with column references inside the body (e.g. the
-- ON CONFLICT (user_id, tenant_id, role_id) target would otherwise be ambiguous → SQLSTATE 42702).
DROP FUNCTION IF EXISTS platform.accept_invitation(TEXT, TEXT, TEXT);
CREATE FUNCTION platform.accept_invitation(
    p_token_hash    TEXT,
    p_password_hash TEXT,
    p_display_name  TEXT
) RETURNS TABLE(out_invitation_id UUID, out_user_id UUID, out_tenant_id UUID, out_already_existed BOOLEAN)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_inv        platform.invitations%ROWTYPE;
    v_user_id    UUID;
    v_existed    BOOLEAN := false;
BEGIN
    -- Opportunistically age out anything past its window so a stale 'pending' can't be accepted below.
    UPDATE platform.invitations
        SET status = 'expired', updated_at = NOW()
        WHERE status = 'pending' AND expires_at <= NOW() AND token_hash = p_token_hash;

    SELECT * INTO v_inv
    FROM platform.invitations
    WHERE token_hash = p_token_hash
      AND status = 'pending'
      AND expires_at > NOW();

    IF v_inv.invitation_id IS NULL THEN
        -- Garbage OR expired OR revoked OR already accepted — one indistinguishable failure.
        RAISE EXCEPTION 'invitation is invalid, expired, or already used'
            USING ERRCODE = 'no_data_found';
    END IF;

    -- Provision-or-link the user (email is the GLOBAL identity). Never overwrite an existing profile.
    SELECT u.user_id INTO v_user_id
    FROM platform.users u WHERE u.email = v_inv.invited_email;

    IF v_user_id IS NULL THEN
        INSERT INTO platform.users
            (email, password_hash, full_name, email_verified, must_change_password, is_active, created_at, updated_at)
        VALUES
            (v_inv.invited_email, p_password_hash, p_display_name, true, false, true, NOW(), NOW())
        RETURNING platform.users.user_id INTO v_user_id;
    ELSE
        v_existed := true;   -- link only; do NOT reset their password/profile
    END IF;

    -- Assign the pre-vetted role (mint-time R3 guard already authorized it). ON CONFLICT un-revokes.
    IF v_inv.role_id IS NOT NULL THEN
        INSERT INTO platform.user_tenant_roles (user_id, tenant_id, role_id, granted_by)
        VALUES (v_user_id, v_inv.tenant_id, v_inv.role_id, v_inv.invited_by_user_id)
        ON CONFLICT (user_id, tenant_id, role_id) DO UPDATE
            SET revoked_at = NULL, revoked_by = NULL, revoked_reason = NULL;
    END IF;

    -- Single-use: flip to accepted. The partial unique index means only one pending row existed.
    UPDATE platform.invitations
        SET status = 'accepted', accepted_user_id = v_user_id, accepted_at = NOW(), updated_at = NOW()
        WHERE platform.invitations.invitation_id = v_inv.invitation_id;

    out_invitation_id   := v_inv.invitation_id;
    out_user_id         := v_user_id;
    out_tenant_id       := v_inv.tenant_id;
    out_already_existed := v_existed;
    RETURN NEXT;
END;
$$;
COMMENT ON FUNCTION platform.accept_invitation IS
    'Unauthenticated single-use redemption: token hash → provision/link user + assign pre-vetted role + mark accepted. Garbage/expired/revoked/used all raise one no_data_found (no enumeration).';

-- EXECUTE grants for the invitation definer functions (docslot_app calls them; they run as owner).
GRANT EXECUTE ON FUNCTION
    platform.create_invitation(UUID, UUID, TEXT, TEXT, TIMESTAMPTZ, UUID),
    platform.resend_invitation(UUID, UUID, UUID, TEXT, TIMESTAMPTZ),
    platform.revoke_invitation(UUID, UUID, UUID),
    platform.accept_invitation(TEXT, TEXT, TEXT)
TO docslot_app;

-- ============================================================================
-- BRANCH / DEPARTMENT MEMBERSHIP SCOPE — an ORGANIZATIONAL DISPLAY ATTRIBUTE,
-- NOT an access-enforcement boundary (epic #80, Phase C — issue #90)
-- ============================================================================
-- A tenant's physical branches (Andheri W, Bandra, …) plus a per-membership scope
-- (branch_id + department) on user_tenant_roles. This lets the People UI show and
-- filter staff by e.g. "Cardiology · Andheri W" and power the "N branches" stat.
--
-- ⚠ DISPLAY GATE, NOT a security boundary — mirrors MODULE LICENSING above.
-- Permission resolution (resolve_user_permissions / user_has_permission /
-- get_user_menus / v_user_permissions) NEVER consults branch_id or department. A
-- user scoped to "Cardiology · Andheri W" has EXACTLY the same effective permissions
-- as one with no scope; the scope only narrows what the People list displays/filters.
-- (If per-branch data access is ever wanted, it belongs at the feature entry point,
-- explicitly — never silently inside permission resolution.)

-- ---- platform.branches -----------------------------------------------------
CREATE TABLE IF NOT EXISTS platform.branches (
    branch_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    name        VARCHAR(200) NOT NULL,
    code        VARCHAR(50),
    is_active   BOOLEAN NOT NULL DEFAULT true,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at  TIMESTAMPTZ
);
-- Live branches per tenant (the People "All branches" filter + the "N branches" stat).
CREATE INDEX IF NOT EXISTS idx_branches_tenant
    ON platform.branches(tenant_id) WHERE deleted_at IS NULL;

DROP TRIGGER IF EXISTS trg_branches_updated_at ON platform.branches;
CREATE TRIGGER trg_branches_updated_at BEFORE UPDATE ON platform.branches
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- RLS: tenant-scoped, same predicates as the other platform tables.
ALTER TABLE platform.branches ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS branches_read  ON platform.branches;
DROP POLICY IF EXISTS branches_write ON platform.branches;
CREATE POLICY branches_read  ON platform.branches FOR SELECT
    USING (platform.rls_can_see_tenant(tenant_id));
CREATE POLICY branches_write ON platform.branches FOR ALL
    USING (platform.rls_can_write_tenant(tenant_id))
    WITH CHECK (platform.rls_can_write_tenant(tenant_id));

-- Branches confer NO permissions (no R3 escalation surface), so a DIRECT own-tenant
-- write by docslot_app under RLS is safe — no SECURITY DEFINER indirection needed.
GRANT SELECT, INSERT, UPDATE ON platform.branches TO docslot_app;

-- ---- membership scope columns on user_tenant_roles -------------------------
-- Additive, nullable: NULL branch_id = "All branches", NULL department = "All departments".
-- These are ORG-ATTRIBUTE columns only; nothing in permission resolution reads them.
ALTER TABLE platform.user_tenant_roles
    ADD COLUMN IF NOT EXISTS branch_id  UUID REFERENCES platform.branches(branch_id),
    ADD COLUMN IF NOT EXISTS department VARCHAR(120);

-- Setter (definer): set a MEMBERSHIP's org scope. user_tenant_roles is an RBAC table,
-- so this MUST only ever touch branch_id/department — NEVER role_id (no escalation path).
-- Re-checks the actor holds tenant.users.update in the row's tenant (or is super_admin);
-- a supplied branch must be an active branch of that same tenant.
CREATE OR REPLACE FUNCTION platform.set_membership_scope(
    p_actor_user_id       UUID,
    p_user_tenant_role_id UUID,
    p_branch_id           UUID,
    p_department          VARCHAR
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_tenant_id UUID;
BEGIN
    SELECT tenant_id INTO v_tenant_id
    FROM platform.user_tenant_roles
    WHERE user_tenant_role_id = p_user_tenant_role_id;

    IF v_tenant_id IS NULL THEN
        -- Unknown membership OR a platform-level (tenant-less) assignment — out of scope.
        RAISE EXCEPTION 'membership % not found in a tenant scope', p_user_tenant_role_id
            USING ERRCODE = 'no_data_found';
    END IF;

    IF NOT (platform.is_super_admin(p_actor_user_id)
         OR platform.user_has_permission(p_actor_user_id, 'tenant.users.update', v_tenant_id)) THEN
        RAISE EXCEPTION 'actor % may not set membership scope in tenant %', p_actor_user_id, v_tenant_id
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF p_branch_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM platform.branches b
                       WHERE b.branch_id = p_branch_id AND b.tenant_id = v_tenant_id
                         AND b.deleted_at IS NULL) THEN
        RAISE EXCEPTION 'branch % is not an active branch of tenant %', p_branch_id, v_tenant_id
            USING ERRCODE = 'foreign_key_violation';
    END IF;

    -- The ONLY columns this function may write. role_id is never referenced here → no escalation.
    UPDATE platform.user_tenant_roles
        SET branch_id = p_branch_id, department = p_department
        WHERE user_tenant_role_id = p_user_tenant_role_id;

    RETURN p_user_tenant_role_id;
END;
$$;
COMMENT ON FUNCTION platform.set_membership_scope IS
    'Set a membership''s org scope (branch_id/department) — DISPLAY ONLY. Gated on tenant.users.update (or super_admin). NEVER writes role_id; not consulted by permission resolution.';

GRANT EXECUTE ON FUNCTION platform.set_membership_scope(UUID, UUID, UUID, VARCHAR) TO docslot_app;

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
-- New tables: impersonation_sessions, role_incompatibility, tenant_module_entitlements, branches (4)
-- New columns: role_permissions.is_grantable, user_tenant_roles.branch_id + .department (org scope, display-only)
-- New/redefined functions: tenant_is_serviceable, resolve_user_permissions*,
--   user_has_permission*, get_user_menus*, current_impersonated_tenant,
--   begin_impersonation, end_impersonation, list_impersonation_sessions,
--   is_super_admin, grant_permission_to_role,
--   assign_role_to_user, enforce_role_sod, rls_can_see/write_tenant,
--   module_is_licensed, set_module_license, set_membership_scope
--   (* = redefined to be tenant-gated + SECURITY DEFINER)
-- RLS enabled: roles, role_permissions, user_tenant_roles,
--   user_permission_overrides, tenant_product_subscriptions, navigation_menus,
--   menu_permissions (7)
-- FOLLOW-UPS (not in this slice): R7 perm_epoch cache invalidation, R8 feature-
--   level entitlement intersection, R9 SCIM/multi-IdP, optimistic concurrency.
-- ============================================================================
