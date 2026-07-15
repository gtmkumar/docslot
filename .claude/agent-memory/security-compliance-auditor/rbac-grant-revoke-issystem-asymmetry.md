---
name: rbac-grant-revoke-issystem-asymmetry
description: platform.grant_permission_to_role LACKS the is_system guard that revoke_permission_from_role has — non-super can add held-with-grant perms to a GLOBAL system role
metadata:
  type: project
---

RBAC write-path asymmetry in `database/11_rbac_hardening.sql` (found auditing the 2026-07-03 role-matrix-unlock wave, commit 82dfbea):

- `platform.revoke_permission_from_role` (11_rbac_hardening.sql:939) DOES guard system roles: non-super actor + `v_is_system` → `RAISE insufficient_privilege` (line 967-972).
- `platform.grant_permission_to_role` (11_rbac_hardening.sql:648) does NOT. Its non-super branch only checks (a) permission not `platform`-scoped and (b) actor holds the permission WITH grant option. No `is_system` check at all.

**Why it matters:** `platform.role_permissions` has NO tenant_id (PK = role_id, permission_id) — system roles (doctor, receptionist, tenant_staff, broker…) are GLOBAL/shared across every tenant. So a non-super `tenant_admin` who holds a tenant-scoped permission with grant option can POST a grant (direct API — `GrantRolePermissionCommandHandler` in IamFeatures.cs has no is_system precheck) targeting a SYSTEM role id and inject that permission into the role platform-wide. Cross-tenant integrity + escalation-for-others (e.g. add `docslot.prescription.read` to `broker` → brokers in ALL tenants gain it). Exploit precondition is broadly met: live DB shows tenant_admin=50, tenant_owner=82, doctor=22 grantable perms.

**How to apply:** Not introduced by commit 82dfbea (that only changed the display `Editable` flag + frontend, which stays locked for non-super so the UI doesn't expose it). But the deliverable's own claim ("DB keeps built-in roles read-only regardless") is only half-true. Recommended fix = mirror revoke's guard in grant (block non-super on `is_system` role). Re-check this whenever role-matrix editing UX expands. Related: [[backend-iam-roles-admin]] (duplicate_role grant-option escalation fix).
