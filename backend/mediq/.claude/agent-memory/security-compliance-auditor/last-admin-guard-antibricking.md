---
name: last-admin-guard-antibricking
description: The tenant last-active-admin anti-bricking guard — where it lives, why super_admin bypasses it, and how the issue #79 test pins its blocking branch.
metadata:
  type: project
---

Last-active-admin (anti-tenant-bricking) guard. Permission-based, NOT role-name-based.

**Where (database/11_rbac_hardening.sql):**
- `platform.tenant_has_other_active_admin(p_tenant_id, p_excluding_user_id)` :1448 — EXISTS over `user_tenant_roles` (active, not-expired, user<>excluded) whose user holds `tenant.users.update` OR `tenant.roles.assign`. Scans MEMBERSHIP ROWS only.
- Enforced in TWO places, EACH with its own copy of the check:
  - `set_tenant_user_active` deactivate branch :1523-1530 → `RAISE 'cannot deactivate the last administrator of tenant %'`.
  - `revoke_role_assignment` :808-826 → `RAISE 'cannot revoke the last administrator''s access in tenant %'`.
- Both RAISE with `ERRCODE = integrity_constraint_violation` (SQLSTATE 23000). Repos (UserLifecycle.cs, RoleAssignmentRepository.cs) catch 23000/23505 → `ConflictException(pg.MessageText)` → 409, VERBATIM guard text in body. 42501/P0002 → ForbiddenException → 403.

**Critical gotcha — super_admin BYPASSES the guard.** Both guards are gated behind `NOT platform.is_super_admin(p_actor_user_id)`. A genuine platform super_admin gets 200, never 409. So you cannot use a super_admin actor to observe the blocking branch.

**How to reach the BLOCKING branch (issue #79 test pattern, UserLifecycleTests.cs).** You need an actor that (a) clears the DEFINER function's permission re-check but (b) is NOT counted as "another admin." The construction: a user with ZERO `user_tenant_roles` rows anywhere, empowered only by a GLOBAL (`tenant_id IS NULL`) grant override in `user_permission_overrides` for `tenant.users.update`/`tenant.roles.assign`. The global-override branch (`OR o.tenant_id IS NULL`) in both `user_has_permission` :163 and `resolve_user_permissions` :119 satisfies the perm check for ANY tenant (and is NOT serviceability-gated), so the actor clears `[RequirePermission]` + the function re-check; but having no membership row, it is invisible to `tenant_has_other_active_admin`'s utr scan. Note: the middleware `[RequirePermission]` check is a plain in-memory set membership test — it does NOT re-scope to the route `{tenantId}`; real tenant-scoping is the DEFINER function's own re-check.

**Confound-proofing that makes the 409 a true pin (not a coincidental 23xxx):**
- SoD trigger `enforce_role_sod` (:589) short-circuits `IF NEW.revoked_at IS NOT NULL THEN RETURN NEW` — so revoke-style UPDATEs (which set revoked_at) can NEVER raise the SoD 23000. In the blocking tests the guard also raises BEFORE any UPDATE, so the trigger never runs.
- No INSERT happens on either request path → no 23505.
- Message-text assertions ("cannot deactivate the last administrator of tenant" / "cannot revoke the last administrator" + "access in tenant") are exact distinct substrings, differing from the SoD message ("SoD violation: role % is incompatible ...").
- Positive control (add ONE permission-only custom-role holder → same call now 200) proves single-admin state was real and the guard is permission-based.

**Audit verdict (2026-07-01):** PASS. Test faithfully pins the blocking branch; a regression removing/inverting `tenant_has_other_active_admin` flips both to 200 (+ membership-mutated), failing the status AND the membership-untouched assertions. See [[prior-audit-decisions]].
