-- ============================================================================
-- 12_password_reset.sql — Self-service + admin-initiated PASSWORD RESET
-- ============================================================================
-- A NEW capability that mirrors the token-based invitation flow (file 11) but
-- for an EXISTING user's credential. Two mint paths + one redeem path, all
-- SECURITY DEFINER so the app role (docslot_app, least-privilege) never writes
-- the token/credential tables directly:
--
--   * request_password_reset       — self-service ("forgot password"). The API
--     resolves the user from the email and, ONLY for a live active user, mints a
--     one-time hashed token. Anti-enumeration lives in the API (always 200); this
--     function is only ever called for a real user.
--   * admin_request_password_reset — an admin (tenant.users.update) or a
--     super_admin mints a reset token FOR a target user, returning the one-time
--     link to hand over out-of-band. RE-USES the R3 no-escalation guard: a
--     tenant admin may not reset a user who outranks them (holds a platform role
--     or any tenant permission the actor lacks). super_admin resets cross-tenant.
--   * consume_password_reset       — UNAUTHENTICATED redemption (the token IS the
--     authorization). Validates unused + unexpired, sets the new hash, clears
--     must_change_password + lockout, marks the token used, and REVOKES every
--     active session (a reset ends all logins). Garbage/expired/used all raise
--     ONE no_data_found so redemption never enumerates.
--
-- Reuses the EXISTING platform.password_reset_tokens table (01_platform_core.sql,
-- table 11). Only a SHA-256 HASH of the token is stored; the plaintext is
-- returned exactly once by the API and never persisted.
--
-- Runs LAST (after 11_rbac_hardening.sql) — depends on is_super_admin() and
-- user_has_permission() defined there, and on file 10's blanket app grants.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Force the SECURITY DEFINER path: the token table is created in 01 and picked
-- up by file 10's blanket GRANT SELECT, INSERT, UPDATE ON ALL TABLES. Revoke the
-- direct writes so the app role can only mint/consume through the definer
-- functions (which run as owner). SELECT is retained (harmless; read-only
-- projections / diagnostics). Idempotent + defensive for post-hoc application.
-- ----------------------------------------------------------------------------
REVOKE INSERT, UPDATE ON platform.password_reset_tokens FROM docslot_app;

-- ---- request_password_reset: self-service mint ------------------------------
-- Insert a single live token for the user, invalidating any prior unused token
-- (only one live reset link at a time). The caller has ALREADY verified the user
-- exists + is active + has a password — this function does no enumeration-relevant
-- work (it is never reached for an unknown email).
CREATE OR REPLACE FUNCTION platform.request_password_reset(
    p_user_id      UUID,
    p_token_hash   TEXT,
    p_requested_ip TEXT DEFAULT NULL,
    p_expires_at   TIMESTAMPTZ DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    -- One live reset token per user: spend any still-unused prior token.
    UPDATE platform.password_reset_tokens
        SET used_at = NOW()
        WHERE user_id = p_user_id AND used_at IS NULL;

    INSERT INTO platform.password_reset_tokens (user_id, token_hash, requested_ip, expires_at)
    VALUES (p_user_id, p_token_hash, CAST(p_requested_ip AS inet),
            COALESCE(p_expires_at, NOW() + INTERVAL '1 hour'))
    RETURNING token_id INTO v_id;

    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.request_password_reset IS
    'Self-service "forgot password" mint: one live hashed token per user (invalidates prior). Anti-enumeration is enforced by the API (always 200); this fn runs only for a real active user.';

-- ---- admin_request_password_reset: admin-initiated mint (R3-guarded) --------
-- Same INSERT as the self-service path but gated: a tenant admin needs
-- tenant.users.update in the SHARED tenant AND may not reset a user who outranks
-- them (holds a platform-scoped role, or any tenant permission the actor does not
-- itself hold — the R3 no-escalation guard, mirroring create_invitation /
-- assign_role_to_user). A super_admin (platform.users.*) bypasses the guard and
-- may reset cross-tenant (p_tenant_id may be NULL for the platform route).
-- Violations RAISE 42501 → ForbiddenException. A missing/soft-deleted target or
-- non-member raises no_data_found.
CREATE OR REPLACE FUNCTION platform.admin_request_password_reset(
    p_actor_user_id  UUID,
    p_target_user_id UUID,
    p_token_hash     TEXT,
    p_requested_ip   TEXT DEFAULT NULL,
    p_expires_at     TIMESTAMPTZ DEFAULT NULL,
    p_tenant_id      UUID DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_id UUID;
BEGIN
    -- An admin resets OTHER users; self-reset flows through self-service recovery.
    IF p_actor_user_id = p_target_user_id THEN
        RAISE EXCEPTION 'use self-service password recovery to reset your own password'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    -- The target must be a live, real user.
    IF NOT EXISTS (
        SELECT 1 FROM platform.users WHERE user_id = p_target_user_id AND deleted_at IS NULL
    ) THEN
        RAISE EXCEPTION 'target user % not found', p_target_user_id
            USING ERRCODE = 'no_data_found';
    END IF;

    IF NOT platform.is_super_admin(p_actor_user_id) THEN
        -- Tenant-admin path: a tenant is mandatory and the actor must administer users there.
        IF p_tenant_id IS NULL THEN
            RAISE EXCEPTION 'a tenant context is required to reset a user password'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
        IF NOT platform.user_has_permission(p_actor_user_id, 'tenant.users.update', p_tenant_id) THEN
            RAISE EXCEPTION 'actor % may not reset passwords in tenant %', p_actor_user_id, p_tenant_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;

        -- Target must be a member of the shared tenant (an admin's reach is their own tenant).
        IF NOT EXISTS (
            SELECT 1 FROM platform.user_tenant_roles
            WHERE user_id = p_target_user_id AND tenant_id = p_tenant_id AND revoked_at IS NULL
        ) THEN
            RAISE EXCEPTION 'user % is not a member of tenant %', p_target_user_id, p_tenant_id
                USING ERRCODE = 'no_data_found';
        END IF;

        -- R3 no-escalation (a): never reset a platform-privileged user (super_admin holds a
        -- platform-scoped role, assigned tenant-independently — check ALL of the target's live roles).
        IF EXISTS (
            SELECT 1
            FROM platform.user_tenant_roles utr
            JOIN platform.roles r ON r.role_id = utr.role_id
            WHERE utr.user_id = p_target_user_id AND utr.revoked_at IS NULL AND r.scope = 'platform'
        ) THEN
            RAISE EXCEPTION 'actor % may not reset a platform-privileged user %', p_actor_user_id, p_target_user_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;

        -- R3 no-escalation (b): never reset a user whose tenant roles confer any permission the
        -- actor does not itself hold in that tenant (identical guard shape to create_invitation).
        IF EXISTS (
            SELECT 1
            FROM platform.user_tenant_roles utr
            JOIN platform.role_permissions rp ON rp.role_id = utr.role_id
            JOIN platform.permissions pm ON pm.permission_id = rp.permission_id
            WHERE utr.user_id = p_target_user_id
              AND utr.tenant_id = p_tenant_id
              AND utr.revoked_at IS NULL
              AND NOT platform.user_has_permission(p_actor_user_id, pm.permission_key, p_tenant_id)
        ) THEN
            RAISE EXCEPTION 'actor % may not reset a higher-privileged user % in tenant %',
                p_actor_user_id, p_target_user_id, p_tenant_id
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;

    -- One live reset token per user: spend any still-unused prior token, then mint.
    UPDATE platform.password_reset_tokens
        SET used_at = NOW()
        WHERE user_id = p_target_user_id AND used_at IS NULL;

    INSERT INTO platform.password_reset_tokens (user_id, token_hash, requested_ip, expires_at)
    VALUES (p_target_user_id, p_token_hash, CAST(p_requested_ip AS inet),
            COALESCE(p_expires_at, NOW() + INTERVAL '1 hour'))
    RETURNING token_id INTO v_id;

    RETURN v_id;
END;
$$;
COMMENT ON FUNCTION platform.admin_request_password_reset IS
    'Admin-initiated reset mint. Tenant admin needs tenant.users.update + the R3 no-escalation guard (no platform-privileged / higher-privileged target); super_admin resets cross-tenant. 42501 → 403.';

-- ---- consume_password_reset: UNAUTHENTICATED redemption ---------------------
-- The token IS the authorization. Validates a live (unused + unexpired) token,
-- sets the new hash, clears must_change_password + lockout, marks the token used,
-- and REVOKES every active session. Garbage/expired/used/deleted-user all raise
-- ONE no_data_found (no enumeration). Returns the user_id.
CREATE OR REPLACE FUNCTION platform.consume_password_reset(
    p_token_hash    TEXT,
    p_password_hash TEXT
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = platform, pg_temp
AS $$
DECLARE
    v_token_id UUID;
    v_user_id  UUID;
BEGIN
    -- A live token: unused AND unexpired. Lock the row so a concurrent redeem can't double-spend it.
    SELECT token_id, user_id INTO v_token_id, v_user_id
    FROM platform.password_reset_tokens
    WHERE token_hash = p_token_hash
      AND used_at IS NULL
      AND expires_at > NOW()
    FOR UPDATE;

    -- Garbage / expired / already-used all fall here → ONE indistinguishable failure (no enumeration).
    IF v_token_id IS NULL THEN
        RAISE EXCEPTION 'password reset token is invalid, expired, or already used'
            USING ERRCODE = 'no_data_found';
    END IF;

    -- The user must still be live (a soft-deleted account cannot be reactivated via a stale token).
    IF NOT EXISTS (
        SELECT 1 FROM platform.users WHERE user_id = v_user_id AND deleted_at IS NULL
    ) THEN
        RAISE EXCEPTION 'password reset token is invalid, expired, or already used'
            USING ERRCODE = 'no_data_found';
    END IF;

    -- Set the new credential; clear the forced-change flag + any lockout so the user can log in.
    UPDATE platform.users
        SET password_hash        = p_password_hash,
            must_change_password = false,
            failed_login_count   = 0,
            locked_until         = NULL,
            updated_at           = NOW()
        WHERE user_id = v_user_id AND deleted_at IS NULL;

    -- Single-use: mark the token spent.
    UPDATE platform.password_reset_tokens SET used_at = NOW() WHERE token_id = v_token_id;

    -- A password reset ends every existing login (defence against a session opened with the old credential).
    UPDATE platform.user_sessions
        SET revoked_at = NOW(), revoked_reason = 'password_reset'
        WHERE user_id = v_user_id AND revoked_at IS NULL;

    RETURN v_user_id;
END;
$$;
COMMENT ON FUNCTION platform.consume_password_reset IS
    'Unauthenticated single-use redemption: token hash → set new password_hash, clear must_change_password + lockout, mark used, revoke all active sessions. Garbage/expired/used all raise one no_data_found (no enumeration).';

-- ---- Grants: docslot_app calls the definer functions (they run as owner) -----
GRANT EXECUTE ON FUNCTION
    platform.request_password_reset(UUID, TEXT, TEXT, TIMESTAMPTZ),
    platform.admin_request_password_reset(UUID, UUID, TEXT, TEXT, TIMESTAMPTZ, UUID),
    platform.consume_password_reset(TEXT, TEXT)
TO docslot_app;
