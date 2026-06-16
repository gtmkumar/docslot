-- Demo login seed for live frontend↔API wiring (idempotent).
-- Creates one hospital tenant + one tenant_owner user whose credentials match the
-- frontend's existing demo hint (priyanka@apollocare.in / reception). Run as a
-- superuser (bypasses RLS). Password is bcrypt via pgcrypto so the .NET
-- IPasswordHasher (BCrypt) verifies it.
--
--   psql -d docslot_platform -f database/seed_demo_login.sql

\set tenant_id '11111111-1111-1111-1111-111111111111'
\set user_id   'a9d05107-c0de-4d05-8107-c0ded05a0001'

INSERT INTO platform.tenants
    (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
VALUES (:'tenant_id', 'apollo-care', 'Apollo Care Pvt Ltd', 'Apollo Care · Andheri West', 'hospital',
        'ops@apollocare.in', '+919820011223', 'active')
ON CONFLICT (tenant_id) DO UPDATE
    SET status = 'active', deleted_at = NULL, display_name = EXCLUDED.display_name;

INSERT INTO platform.users
    (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, preferred_language, created_at, updated_at)
VALUES (:'user_id', 'priyanka@apollocare.in', crypt('reception', gen_salt('bf', 10)), 'Priyanka R',
        true, true, false, 'en', NOW(), NOW())
ON CONFLICT (email) DO UPDATE
    SET password_hash = EXCLUDED.password_hash, deleted_at = NULL, is_active = true;

-- tenant_owner in Apollo Care, primary → login resolves this tenant as active so
-- the issued JWT carries tenant_id (required: tenant comes from the claim, not a header).
INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
SELECT gen_random_uuid(), :'user_id', :'tenant_id', r.role_id, true, NOW()
FROM platform.roles r
WHERE r.role_key = 'tenant_owner' AND r.is_system = true
ON CONFLICT DO NOTHING;

SELECT u.email, t.display_name AS tenant, r.role_key
FROM platform.user_tenant_roles utr
JOIN platform.users u ON u.user_id = utr.user_id
JOIN platform.tenants t ON t.tenant_id = utr.tenant_id
JOIN platform.roles r ON r.role_id = utr.role_id
WHERE u.user_id = :'user_id';
