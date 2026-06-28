---
name: rbac-catalog-plane
description: Catalog-plane create funcs (create_resource_type/create_permission) authz model + the 409 catch-broadening, audited & cleared
metadata:
  type: project
---

Catalog plane (modules + permissions creation) audited 2026-06-26 — now CLEARED PASS (condition satisfied & independently verified).

RESOLUTION (2026-06-26): The clearance condition was applied and verified by me, not taken on trust:
- REVOKE INSERT, UPDATE FROM docslot_app on platform.permissions / resource_types / action_types added to `database/11_rbac_hardening.sql` (L1319-1321) + regenerated into `docslot_complete.sql` (L7617-7619), placed after the EXECUTE-grant block, before POST-CONDITIONS.
- Confirmed no EF write path (grep: zero .Add/.Update/.Remove/SaveChanges on those DbSets); IamReadService reads them AsNoTracking only — SELECT retention is safe.
- Live DB (information_schema.role_table_grants) shows docslot_app = SELECT-only on all three. Definer funcs run as owner, unaffected.
The advisory-vs-boundary gap is CLOSED: a stray app-role INSERT into the catalog is now DB-denied, so the permissions.manage authz check can no longer be bypassed. Catalog write path == role_permissions write path (definer-only), symmetric with R1.

Facts established:
- `platform.create_resource_type` / `platform.create_permission` live in `database/11_rbac_hardening.sql` (~L1056, L1090), just after `duplicate_role`. SECURITY DEFINER, `SET search_path = platform, pg_temp`. Gate: `is_super_admin(actor) OR user_has_permission(actor,'platform.permissions.manage',NULL)` else `insufficient_privilege` (42501). EXECUTE granted to docslot_app.
- `platform.permissions.manage` is defined in `database/01_platform_core.sql:220` as scope=platform, is_dangerous=true. By default only `super_admin` holds it (file 10 universal sweep). So creating a permission does NOT escalate: `grant_permission_to_role` independently blocks any non-super actor from conferring a platform-scoped permission.
- Created permissions are `is_system=false` (deletable custom entries). Action_type upsert is `ON CONFLICT (action_key) DO NOTHING`.
- Actor is ALWAYS `ctx.UserId` (authenticated principal), never request body — confirmed across all IamFeatures handlers.

**Why:** [[catalog-tables-no-rls]] — the catalog tables (permissions, resource_types, action_types) have NO RLS and docslot_app holds direct INSERT/SELECT/UPDATE. The definer funcs add an authz CHECK but are NOT a hard boundary; a different app-role code path could INSERT directly. This differs from role_permissions/roles/user_tenant_roles which DO have RLS (R1).

**How to apply:** Recommended (not blocking for this catalog slice) — REVOKE direct INSERT/UPDATE on permissions/resource_types/action_types from docslot_app so the definer funcs become the sole write path, matching the RLS-protected entitlement tables. Track whether this is honored in a later wave.

The 23000→{23000,23505} catch broadening in `RoleAssignmentRepository.ScalarAsync/ExecAsync`: benign. grant/assign/override use ON CONFLICT (never raise 23505). create_custom_role/duplicate_role do raw INSERT into roles UNIQUE(role_key,tenant_id) with no ON CONFLICT — for those a duplicate now returns 409 (was a leaky 500); that is an improvement, not a regression. SoD trigger fires 23000 (mapped via SQLSTATE class, still 409).
