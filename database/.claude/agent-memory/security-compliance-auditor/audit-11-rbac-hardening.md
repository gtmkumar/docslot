---
name: audit-11-rbac-hardening
description: Verdict and conditions issued for the wave that added database/11_rbac_hardening.sql (RBAC RLS, SoD, scoped impersonation)
metadata:
  type: project
---

Audited database/11_rbac_hardening.sql (2026-06-21). Verdict: **APPROVE-WITH-CHANGES**.

Conditions issued (verify honored in later waves):
1. **BLOCKER-class integration risk:** app never sets `app.is_super_admin`, and RBAC admin writes use EF direct INSERT (not the definer helpers). With R1 `*_write` policies enabled, platform-scoped (tenant_id IS NULL) and cross-tenant admin writes by docslot_app will be silently blocked. Required: either route admin writes through assign_role_to_user/grant_permission_to_role definer funcs, OR have the app set `app.is_super_admin` GUC for platform-admin sessions inside the SET LOCAL transaction. See [[rbac-rls-layout]].
2. **begin_impersonation does NOT write to audit_log/audit_chain** — only inserts impersonation_sessions. DPDP S.8(7) accountability requires the hash-chained trail. Required: append an audit_log row inside begin_impersonation (and on session end).
3. **impersonation_sessions has no RLS** and has GRANT SELECT/INSERT/UPDATE to docslot_app. A tenant context could read/forge platform impersonation records. Required: enable RLS (read = actor or super-admin/impersonated-tenant; forbid tenant forging actor_user_id), and make it effectively append-only (no UPDATE except ended_at, via trigger).
4. **SoD COALESCE sentinel** uses all-zeros UUID '00000000-...-000000000000' to compare NULL (platform) tenants. Theoretical false-positive if a real tenant ever uses that UUID. Recommend `IS NOT DISTINCT FROM` instead of COALESCE-to-sentinel.
5. resource_types, action_types, access_policies, purpose_of_use_log still have NO RLS (access_policies/purpose_of_use_log are sensitive). Flagged as follow-up, not a blocker for this slice.

User had already validated (PG18): bundle applies clean, suspended tenants → 0 perms, SoD blocks, escalation guard blocks, RLS as docslot_app shows own-tenant+global, definer login path intact, tenant can't insert global menus, deny-wins intact.
