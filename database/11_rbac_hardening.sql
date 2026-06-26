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
BEGIN
    IF p_reason IS NULL OR length(btrim(p_reason)) = 0 THEN
        RAISE EXCEPTION 'a revoke reason is mandatory';
    END IF;
    SELECT tenant_id, revoked_at INTO v_tenant, v_revoked
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
    END IF;
    UPDATE platform.user_tenant_roles
        SET revoked_at = NOW(), revoked_by = p_actor_user_id, revoked_reason = left(p_reason, 200)
        WHERE user_tenant_role_id = p_user_tenant_role_id;
    RETURN true;
END;
$$;
COMMENT ON FUNCTION platform.revoke_role_assignment IS
    'R3: soft-revoke an assignment; super_admin OR tenant.roles.assign in the assignment''s tenant. Idempotent.';

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
-- TODAY because the only sanctioned RBAC mutation path is the SECURITY DEFINER functions below
-- (grant_permission_to_role / assign_role_to_user / …), which enforce R3 regardless of RLS.
-- Any future convenience path that writes these tables directly MUST route through those functions.
CREATE OR REPLACE FUNCTION platform.rls_can_write_tenant(p_row_tenant UUID)
RETURNS BOOLEAN LANGUAGE SQL STABLE AS $$
    -- Writes never target a NULL (global/system) row unless super_admin context.
    SELECT (p_row_tenant IS NOT NULL AND p_row_tenant = platform.current_tenant_id())
        OR platform.current_is_super_admin();
$$;

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
    platform.assign_role_to_user(UUID, UUID, UUID, UUID),
    platform.revoke_role_assignment(UUID, UUID, TEXT),
    platform.set_user_permission_override(UUID, UUID, UUID, UUID, BOOLEAN, TEXT, TIMESTAMPTZ, TIMESTAMPTZ),
    platform.create_custom_role(UUID, VARCHAR, VARCHAR, TEXT, UUID, VARCHAR),
    platform.rls_can_see_tenant(UUID),
    platform.rls_can_write_tenant(UUID)
TO docslot_app;

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
