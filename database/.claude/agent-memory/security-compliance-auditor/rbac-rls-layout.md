---
name: rbac-rls-layout
description: Where RLS/RBAC isolation lives across DB files 05/10/11, the session-GUC model, and the runtime write-path RLS hazard the app must satisfy
metadata:
  type: project
---

RLS / RBAC isolation is spread across three DB files. Read all three before auditing any RBAC change.

- **05_security_hardening.sql** — RLS on the 5 PHI tables only (patient_medical_history, prescriptions, lab_reports, abdm_health_records, drug_alerts). Defines the session-GUC helpers: `platform.current_tenant_id()` reads `app.tenant_id`; `platform.current_is_super_admin()` reads `app.is_super_admin` GUC. Audit chain (audit_log → audit_chain trigger, hash-chained, append-only via block_audit_log_mutation trigger + REVOKE in file 10).
- **10_roles_grants.sql** — `docslot_app` login role: NOSUPERUSER, NOBYPASSRLS. So all RLS binds to it. Has SELECT/INSERT/UPDATE on platform/docslot/etc.; DELETE only on a narrow list incl. user_tenant_roles; audit_log/audit_chain are INSERT-only. Runs `GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA platform` (so later definer funcs are callable). Ends with the super_admin universal permission sweep.
- **11_rbac_hardening.sql** — terminal file. Adds RLS to 7 RBAC tables (roles, role_permissions, user_tenant_roles, user_permission_overrides, tenant_product_subscriptions, navigation_menus, menu_permissions). Redefines resolve_user_permissions / user_has_permission / get_user_menus as SECURITY DEFINER + `SET search_path = platform, pg_temp` + tenant-gated by `tenant_is_serviceable()`. Adds grant-option guard (grant_permission_to_role, assign_role_to_user), SoD (role_incompatibility + enforce_role_sod trigger), scoped impersonation (impersonation_sessions + begin_impersonation).

**The GUC model has a gap the app does not satisfy (load-bearing):**
- App sets `app.tenant_id` per transaction via `UnitOfWork.BeginTenantScopeAsync` (SET LOCAL, good — see UnitOfWork.cs).
- App **NEVER sets `app.is_super_admin`** anywhere in .NET code (grep: only SQL comments reference it). So `current_is_super_admin()` is ALWAYS false at runtime. Platform/super_admin god-context exists only when seeds run as the superuser psql role (BYPASSRLS).
- RBAC admin writes (AssignRole, SetOverride, CreateRole) go through EF direct INSERT as docslot_app (RoleAssignmentRepository.cs / UserAdmin.cs / RoleAdmin.cs), NOT through the SECURITY DEFINER assign_role_to_user / grant_permission_to_role helpers. These direct writes are subject to the new R1 `*_write` policies, whose WITH CHECK requires row.tenant_id = current_tenant_id() OR current_is_super_admin(). Consequence: platform-scoped (tenant_id IS NULL) role assignments and cross-tenant admin writes by the app will be BLOCKED once file 11 RLS is enabled, because is_super_admin GUC is never set. This is the primary integration risk of wave 11.

**Definer info-disclosure note:** resolve_user_permissions / get_user_menus take p_user_id/p_tenant_id with NO caller-identity check. Acceptable ONLY because docslot_app is trusted middleware that derives those args from a validated JWT; any future direct-SQL or BI access to these functions would be a cross-tenant info-disclosure vector.
