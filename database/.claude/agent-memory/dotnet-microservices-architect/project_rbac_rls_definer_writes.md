---
name: project-rbac-rls-definer-writes
description: RBAC admin write paths route through platform.* SECURITY DEFINER functions under RLS; login membership read must bypass RLS via owner-rights view
metadata:
  type: project
---

database/11_rbac_hardening.sql enables Row-Level Security on the RBAC tables (roles, role_permissions, user_tenant_roles, user_permission_overrides) and ships SECURITY DEFINER functions in schema `platform` that enforce the privilege-escalation (grant-option) guard. The four admin WRITE paths now call these functions instead of direct EF inserts/updates:
- assign_role_to_user, revoke_role_assignment, set_user_permission_override, create_custom_role
Repo: mediq.Infrastructure/Persistence/Repositories/RoleAssignmentRepository.cs invokes them via `db.Database.SqlQueryRaw<T>` (enlisted in the ambient UnitOfWork tx that already did SET LOCAL app.tenant_id). PostgresException SqlState 42501 → ForbiddenException (403); 23000 (SoD trigger) → ConflictException (409, a house exception added in mediq.Utilities/Exceptions + wired in ExceptionHandler).

**Why:** RLS blocks docslot_app (the runtime DB role; NOBYPASSRLS) from direct writes to these tables; the definer functions are the only sanctioned write path and the actor passed is ctx.UserId.

**How to apply:** Never add direct EF writes to these four RBAC tables — extend the function-call repo pattern. The app runs as `docslot_app` (mediq.Api/appsettings.json "platform-db"); tests seed/cleanup as owner `gtmkumar` (RLS-exempt).

**Critical side-effect (deployment dependency the SQL file documents):** enabling RLS on user_tenant_roles broke the login-time cross-tenant membership read (ITenantRepository.GetMembershipsAsync). A tenant-scoped user could not resolve their tenant at login → activeTenant null → permissions resolved with tenantId=null → tenant-scoped perms (e.g. tenant.roles.assign, platform.overrides.grant) missing → spurious 403. Fix: GetMembershipsAsync now reads from the owner-rights view `platform.v_user_permissions` (no security_invoker ⇒ runs as schema owner, bypasses RLS, R2-serviceability-gated) joined to the RLS-free `platform.tenants` table. Trade-off: is_primary is no longer recoverable from that view (defaulted false) — affects only default-tenant preference when no explicit tenant is requested, not authz correctness.

**Resolver semantic gotcha (resolve_user_permissions in file 11):** a platform-level (tenant_id NULL) super_admin assignment confers ONLY scope='platform' permissions; tenant-scoped permissions require the assignment row's tenant_id to equal the queried tenant. PlatformWebAppFactory seeds super_admin (platform) PLUS tenant_owner (in tenant) so resolving WITH that tenant yields the tenant-scoped keys.

**`app.is_super_admin` GUC — NOW WIRED (2026-06, audit Finding 1 fully closed).** `UnitOfWork.BeginTenantScopeAsync` sets it in the SAME `set_config` round-trip as `app.tenant_id`: `set_config('app.is_super_admin', platform.is_super_admin(@userId)::text, true)`. Value is derived authoritatively from the DB (the JWT carries no super_admin claim) for the validated `ICurrentUserContext.UserId`; anon/background ⇒ null ⇒ false. This admits a platform super_admin to the R1 `*_write` and cross-tenant/global READ predicates (`rls_can_*_tenant` → `current_is_super_admin()`). The four RBAC admin writes still go through the definer funcs (escalation guard); the GUC covers the non-definer paths. **Latent bug it exposed + fixed:** `platform.current_is_super_admin()` (file 05) did `current_setting('app.is_super_admin', true)::BOOLEAN` WITHOUT `NULLIF(...,'')` — a SET LOCAL custom GUC reverts to '' (not unset) on a pooled connection after the first tx, so `''::BOOLEAN` threw 22P02 on ~half of all requests. Fixed to mirror `current_tenant_id()`'s NULLIF pattern; bundle regenerated + applied live. Proof: [[RbacSuperAdminGucTests]] (super_admin sees a foreign tenant's role via `GET /roles?tenantId=B`; tenant_owner is confined). 65/65 integration tests green.

**Live dev DB:** docslot_platform on localhost:5432 is PostgreSQL 18.4. File 11 was NOT initially loaded there; applying the unmodified file (idempotent, depends on 10_roles_grants.sql) installs the functions + RLS + role_incompatibility table. docslot_app has EXECUTE on all four functions.
