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
