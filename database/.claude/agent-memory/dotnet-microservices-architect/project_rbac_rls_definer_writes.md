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

**Resolver semantic gotcha (resolve_user_permissions in file 11):** a platform-level (tenant_id NULL) super_admin assignment confers ONLY scope='platform' permissions; tenant-scoped permissions require the assignment row's tenant_id to equal the queried tenant. PlatformWebAppFactory seeds super_admin (platform) PLUS tenant_owner (in tenant) so resolving WITH that tenant yields the tenant-scoped keys. There is NO app-side `app.is_super_admin` GUC set anywhere yet — cross-tenant super_admin WRITES (not the four definer paths) would still fail RLS until that GUC is wired.

**Live dev DB:** docslot_platform on localhost:5432 is PostgreSQL 18.4. File 11 was NOT initially loaded there; applying the unmodified file (idempotent, depends on 10_roles_grants.sql) installs the functions + RLS + role_incompatibility table. docslot_app has EXECUTE on all four functions.
