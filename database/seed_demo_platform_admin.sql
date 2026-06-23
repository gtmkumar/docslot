-- Platform-admin demo login + Developers/Security data (idempotent, superuser).
-- The Developers + Security console screens are PLATFORM-admin features (a tenant_owner
-- gets 403). This seeds an admin who is super_admin (platform) AND tenant_owner in Apollo
-- (so the token carries the Apollo tenant) + the data those screens list.
\set tenant_id '11111111-1111-1111-1111-111111111111'
\set admin_id  'ad300001-0000-4000-8000-000000000001'

-- Admin user (login: admin@docslot.io / admin).
INSERT INTO platform.users
  (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, preferred_language, created_at, updated_at)
VALUES (:'admin_id', 'admin@docslot.io', crypt('admin', gen_salt('bf',10)), 'Platform Admin',
        true, true, true, 'en', NOW(), NOW())
ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL, is_active = true;

-- super_admin at platform scope (tenant NULL) → all 127 permissions.
INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
SELECT gen_random_uuid(), :'admin_id', NULL, role_id, false, NOW()
FROM platform.roles WHERE role_key='super_admin' AND is_system=true
ON CONFLICT DO NOTHING;
-- tenant_owner in Apollo, primary → token activeTenantId = Apollo so tenant-scoped data resolves too.
INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
SELECT gen_random_uuid(), :'admin_id', :'tenant_id', role_id, true, NOW()
FROM platform.roles WHERE role_key='tenant_owner' AND is_system=true
ON CONFLICT DO NOTHING;

-- API clients (owned by Apollo) — mirrors the Developers screen prototype.
INSERT INTO platform_api.api_clients
  (client_id, client_code, client_name, client_secret_hash, client_type, owner_tenant_id, owner_email, purpose, is_active, created_at)
SELECT md5('apollo-client-'||code)::uuid, code, name, crypt('client-secret-'||code, gen_salt('bf',10)),
       ctype, :'tenant_id', email::citext, purpose, active, NOW()
FROM (VALUES
  ('apollo-hms','Apollo HMS Integration','partner','integrations@apollohms.in','Two-way booking + records sync',true),
  ('star-insurance','Star Insurance Claims','partner','api@starinsurance.in','Cashless claim verification',true),
  ('pharmeasy','PharmEasy Rx Sync','partner','dev@pharmeasy.in','Prescription fulfilment',true),
  ('legacy-portal','Legacy Web Portal','first_party','it@apollocare.in','Internal legacy booking portal',false)
) AS c(code,name,ctype,email,purpose,active)
ON CONFLICT (client_code) DO NOTHING;

-- Breach log (Security → breaches). PK breach_id; tenant linkage via affected_tenant_ids[].
INSERT INTO platform.breach_log (breach_id, breach_type, severity, description, affected_tenant_ids, affected_record_count, detected_at, detection_method)
SELECT md5('apollo-breach-'||t)::uuid, t, sev, descr, ARRAY[:'tenant_id']::uuid[], recs, NOW() - (age||' days')::interval, method
FROM (VALUES
  ('unauthorized_access','medium','Repeated failed logins from a single IP on a staff account; account locked, no data accessed.',0,'3','automated_anomaly'),
  ('data_export_anomaly','low','Bulk patient export attempted outside business hours; blocked by policy, flagged for review.',0,'12','policy_engine')
) AS b(t,sev,descr,recs,age,method)
ON CONFLICT (breach_id) DO NOTHING;

-- DPDP data-deletion requests (Security → DPDP). scope ∈ all|specific_tenant|specific_products.
INSERT INTO platform.data_deletion_requests
  (request_id, requester_type, requester_email, subject_phone, tenant_ids, scope, status, reason, created_at)
SELECT md5('apollo-dpdp-'||ph)::uuid, rtype, email::citext, ph, ARRAY[:'tenant_id']::uuid[], 'specific_tenant', st, reason, NOW() - (age||' days')::interval
FROM (VALUES
  ('patient','riya@example.in','+919820000072','pending','Patient requested account + history deletion under DPDP S.12.','2'),
  ('patient','aman@example.in','+919820000012','completed','Erasure completed; cryptographic key destroyed, certificate issued.','9')
) AS d(rtype,email,ph,st,reason,age)
ON CONFLICT (request_id) DO NOTHING;

SELECT (SELECT count(*) FROM platform_api.api_clients WHERE owner_tenant_id=:'tenant_id') api_clients,
       (SELECT count(*) FROM platform.breach_log WHERE :'tenant_id'::uuid = ANY(affected_tenant_ids)) breaches,
       (SELECT count(*) FROM platform.data_deletion_requests WHERE :'tenant_id'::uuid = ANY(tenant_ids)) dpdp_requests;
