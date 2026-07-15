-- Lean production bootstrap: the ONE initial platform-scoped super_admin user.
--
-- Deliberately NOT part of docslot_complete.sql (that bundle is schema + master/reference
-- data ONLY — permissions, system roles, navigation menus, etc. — and stays environment-
-- agnostic and zero-user). This script is the one operational step every fresh environment
-- (prod, staging, a rebuilt VPS) needs on top of the bundle to become loggable-into.
--
-- SAFE TO COMMIT: no password is hardcoded. Each run generates a fresh random password,
-- hashes it (pgcrypto bcrypt, matching mediq.Api's login verification), and prints the
-- PLAINTEXT once via RAISE NOTICE — it is never written to disk or persisted anywhere else.
-- Capture it from the terminal output immediately; it cannot be recovered afterward (only
-- reset, by re-running this script — see the ON CONFLICT behavior below).
--
-- USAGE:
--   psql -d docslot_platform_prod -f seed_prod_superadmin.sql
--   psql -d docslot_platform_prod -v superadmin_email=ops@yourcompany.in -f seed_prod_superadmin.sql
--
-- RE-RUN SAFETY: if a user with this email already exists, this script does NOT touch their
-- password or role — it just reports that they already exist. Prod credentials never get
-- silently reset by re-running the bootstrap. To rotate the password, do it explicitly:
--   UPDATE platform.users SET password_hash = crypt('<new>', gen_salt('bf',10)) WHERE email = '...';
--
-- PREREQUISITE: run database/docslot_complete.sql (or the numbered 01..11 files) first —
-- this script only assigns the 'super_admin' system role, it does not create it.

\set superadmin_email superadmin@docslot.io

-- psql does NOT interpolate :'vars' inside dollar-quoted DO bodies (deliberately, so it
-- never clobbers literal colons in PL/pgSQL — array slices, etc.). Route the value through
-- a session GUC instead, read back with current_setting() inside the DO block below.
SELECT set_config('docslot.seed_superadmin_email', :'superadmin_email', false);

DO $seed$
DECLARE
    v_email       citext := current_setting('docslot.seed_superadmin_email');
    v_user_id     uuid;
    v_role_id     uuid;
    v_password    text := encode(gen_random_bytes(18), 'base64');
    v_existing    uuid;
BEGIN
    -- base64 can contain '/', '+', '=' — strip to keep the printed password copy-paste safe.
    v_password := regexp_replace(v_password, '[^A-Za-z0-9]', '', 'g');

    SELECT user_id INTO v_existing FROM platform.users WHERE email = v_email AND deleted_at IS NULL;
    IF v_existing IS NOT NULL THEN
        RAISE NOTICE 'Superadmin % already exists (user_id=%) — no changes made. Re-run with a different -v superadmin_email to create another, or rotate the password explicitly (see file header).', v_email, v_existing;
        RETURN;
    END IF;

    SELECT role_id INTO v_role_id FROM platform.roles WHERE role_key = 'super_admin' AND is_system = true;
    IF v_role_id IS NULL THEN
        RAISE EXCEPTION 'platform.roles has no is_system super_admin row — run docslot_complete.sql (master data) first.';
    END IF;

    INSERT INTO platform.users
        (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, preferred_language, created_at, updated_at)
    VALUES (gen_random_uuid(), v_email, crypt(v_password, gen_salt('bf', 10)), 'Super Admin',
            true, true, true, 'en', NOW(), NOW())
    RETURNING user_id INTO v_user_id;

    -- Platform-scoped (tenant_id NULL) — not tied to any tenant, matches super_admin's
    -- cross-tenant design (database/README.md "Seeded System Roles").
    INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
    VALUES (gen_random_uuid(), v_user_id, NULL, v_role_id, true, NOW());

    RAISE NOTICE '=== Superadmin created ===';
    RAISE NOTICE 'Email:    %', v_email;
    RAISE NOTICE 'Password: % (shown once — copy it now)', v_password;
    RAISE NOTICE '==========================';
END $seed$;
