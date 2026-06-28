---
name: module-licensing-slice
description: Per-module licensing IAM slice (tenant_module_entitlements) — cleared as display-only gate; key facts + posture
metadata:
  type: project
---

Per-module licensing slice (audited, PASS) — `platform.tenant_module_entitlements` in `database/11_rbac_hardening.sql` (~L1164) + bundle `docslot_complete.sql` (~L7462).

Design intent: COMMERCIAL DISPLAY GATE ONLY. Greys the admin matrix cell; must NEVER affect access. DENYLIST semantics (licensed unless explicit is_licensed=false row).

**Why display-only is verified:** `resolve_user_permissions` (11_rbac_hardening.sql L105-142) and `user_has_permission` (L147-180) contain ZERO references to licensing/entitlements. `module_is_licensed`/`tenant_module_entitlements` are only read by IamReadService for matrix projection. Granted flags computed independently from RolePermissions. Integration test (IamAdminTests.cs ~L288) asserts unlicensed module's permission still resolves.

**Posture (matches catalog hardening):**
- RLS enabled: tme_read USING rls_can_see_tenant, tme_write USING/CHECK rls_can_write_tenant.
- GRANT SELECT only to docslot_app; writes via `set_module_license` SECURITY DEFINER (sole-writer pattern).
- `set_module_license` gate: is_super_admin OR user_has_permission('platform.settings.update'). That perm is PLATFORM-scoped + dangerous=true (01_platform_core.sql L223), so only platform operators / super_admin reach it. A tenant admin cannot hold a platform-scoped perm (R3 assign_role blocks conferring platform-scope to non-super). Therefore caller-supplied tenant_id in SetModuleLicenseRequest is a platform-admin act over any tenant — acceptable, NOT a cross-tenant escalation.

**create_permission change (L1090-1147):** now auto-grants new permission to super_admin's role only (`WHERE r.role_key='super_admin' AND r.is_system=true`, ON CONFLICT DO NOTHING). Maintains "super_admin holds every permission" invariant from original CROSS JOIN seed. No over-grant — targets exactly one role.

**Audit:** set_module_license writes app-layer audit row via IAuditTrailWriter; `reason` is free text → low PII risk (commercial reason string, operator-entered). Advised: treat reason as non-PII / avoid pasting patient data — informational only.
