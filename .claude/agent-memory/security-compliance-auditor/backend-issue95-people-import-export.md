---
name: backend-issue95-people-import-export
description: #95 People CSV export + bulk user-provisioning import — R3 per-row, tenant scope, CSV-injection, batch bounds; PASS-WITH-CHANGES
metadata:
  type: project
---

#95 (Phase D of epic #80) People export + bulk import. Files: `mediq.Application/Features/Admin/PeopleImportExport.cs`, controller `AdminController.cs` (`/tenants/{id}/users/export` GET tenant.users.read, `/users/bulk-import` POST tenant.users.create), DTOs `PeopleImportExportDtos.cs`, tests `PeopleImportExportTests.cs`.

**Verdict: PASS_WITH_CHANGES — no blockers.** All four asks verified sound:
- **R3 per-row**: bulk import routes each role through the SAME `platform.assign_role_to_user` definer (11_rbac_hardening.sql) the single path uses → identical no-escalation guard (42501→ForbiddenException). Per-row SAVEPOINT (`bulk_row_N`) isolates a rejected/escalating row; rollback-to-savepoint un-aborts the PG tx so valid rows still commit. Tested: super_admin row errors, valid row created, no orphan minted.
- **Tenant scope**: both pipelines `SET LOCAL app.tenant_id = currentUser.TenantId` (Behaviors.cs); handlers ALSO reject `command/query.TenantId != ctx.TenantId`; route id is address-only. Export dir filters by tenantId + RLS on utr. Import creates global `platform.users` row + membership only in ctx tenant.
- **CSV injection**: `PeopleCsv.Csv()` neutralizes leading =,+,-,@ with apostrophe + RFC-4180 quoting (mirrors SecurityFeatures.AuditCsv). Staff PII only (name/email/roles/branch/dept/status/2FA/last-active) — phone deliberately excluded, no PHI.
- **Batch bounds**: MaxBatch=500, oversize→422 wholesale (validator, tested). Provisioning links existing email (AlreadyExisted), never overwrites.

**Recommended changes issued (none blocking):**
1. MEDIUM — rejected/errored bulk rows write NO audit. Audit writer (`AuditTrailWriter`) is on a DEDICATED connection (survives savepoint rollback), so a blocked bulk escalation attempt COULD+SHOULD emit a Success:false audit. Currently silent (matches single path, but bulk is a higher-volume escalation surface).
2. LOW — bulk rows skip the single path's `.EmailAddress()`/MaximumLength(200) validation (only checks non-empty). Malformed email/overlong name can reach provisioning.
3. LOW — handler catches bare `Exception` and returns `ex.Message` to client per row; fine for translated domain msgs but leaks raw text for unexpected exceptions.
4. LOW — CSV neutralization omits leading TAB/CR and leading-whitespace-then-formula (OWASP CSV guidance). Same nit exists in AuditCsv.

Incidental in same working tree (NOT #95): `MfaEnrollmentRequiredException` + ExceptionHandler 403 mapping (#91), `UserTenantRole.BranchId/Department` display-only cols (#90). Benign.
