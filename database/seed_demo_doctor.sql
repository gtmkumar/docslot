-- Demo DOCTOR login seed for live frontend↔API wiring (idempotent, superuser).
-- Mirrors seed_demo_login.sql but creates a user holding the system `doctor` role
-- (tenant-scoped + primary, so the issued JWT carries Apollo's tenant_id) AND a
-- linked docslot.doctors profile row so doctor-specific screens/schedules resolve.
--
--   psql -d docslot_platform -f database/seed_demo_doctor.sql
--
-- Login: dr.mehta@apollocare.in / doctor

\set tenant_id '11111111-1111-1111-1111-111111111111'
\set user_id   'a9d05107-c0de-4d05-8107-c0ded05a0d0c'
\set doctor_id 'd0c70001-0000-4000-8000-000000000001'

-- Tenant (no-op if seed_demo_login.sql already created it).
INSERT INTO platform.tenants
    (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
VALUES (:'tenant_id', 'apollo-care', 'Apollo Care Pvt Ltd', 'Apollo Care · Andheri West', 'hospital',
        'ops@apollocare.in', '+919820011223', 'active')
ON CONFLICT (tenant_id) DO UPDATE
    SET status = 'active', deleted_at = NULL;

-- Auth user (bcrypt via pgcrypto so the .NET BCrypt IPasswordHasher verifies it).
INSERT INTO platform.users
    (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, preferred_language, created_at, updated_at)
VALUES (:'user_id', 'dr.mehta@apollocare.in', crypt('doctor', gen_salt('bf', 10)), 'Dr. Arjun Mehta',
        true, true, false, 'en', NOW(), NOW())
ON CONFLICT (email) DO UPDATE
    SET password_hash = EXCLUDED.password_hash, deleted_at = NULL, is_active = true;

-- `doctor` system role, scoped to Apollo + primary → login resolves the tenant and
-- the JWT carries tenant_id (tenant comes from the claim, never a header).
INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
SELECT gen_random_uuid(), :'user_id', :'tenant_id', r.role_id, true, NOW()
FROM platform.roles r
WHERE r.role_key = 'doctor' AND r.is_system = true
ON CONFLICT DO NOTHING;

-- Clinical profile linked by user_id (qualifications NOT NULL → '[]'; role/nmc satisfy CHECKs).
INSERT INTO docslot.doctors
    (doctor_id, tenant_id, user_id, full_name, display_name, gender, role, specialization,
     qualifications, experience_years, consultation_fee, follow_up_fee, phone, email,
     nmc_verification_status, is_active, is_accepting_new_patients, created_at, updated_at)
VALUES (:'doctor_id', :'tenant_id', :'user_id', 'Dr. Arjun Mehta', 'Dr. Mehta', 'male', 'doctor',
        'General Medicine', '[{"degree":"MBBS"},{"degree":"MD (Internal Medicine)"}]'::jsonb,
        12, 500, 300, '+919820044556', 'dr.mehta@apollocare.in',
        'not_verified', true, true, NOW(), NOW())
ON CONFLICT (doctor_id) DO UPDATE
    SET user_id = EXCLUDED.user_id, deleted_at = NULL, is_active = true;

SELECT u.email, u.full_name, t.display_name AS tenant, r.role_key,
       d.doctor_id IS NOT NULL AS has_profile
FROM platform.user_tenant_roles utr
JOIN platform.users u  ON u.user_id = utr.user_id
JOIN platform.tenants t ON t.tenant_id = utr.tenant_id
JOIN platform.roles r  ON r.role_id = utr.role_id
LEFT JOIN docslot.doctors d ON d.user_id = u.user_id
WHERE u.user_id = :'user_id';
