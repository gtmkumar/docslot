---
name: slice95-people-import-export
description: Issue #95 People CSV export + bulk import — per-row savepoint batching gotcha, CSV neutralization reuse, tenant-from-JWT guard
metadata:
  type: project
---

Issue #95 (Phase D of epic #80): People export + bulk import backend. Endpoints on `AdminController`:
`GET /api/v1/tenants/{id}/users/export` (gated `tenant.users.read`, `text/csv`) and
`POST /api/v1/tenants/{id}/users/bulk-import` (gated `tenant.users.create`). Feature in
`mediq.Application/Features/Admin/PeopleImportExport.cs`; DTOs in `SharedDataModel/Docslot/Admin/PeopleImportExportDtos.cs`.

**Why / How to apply:**
- **Per-row atomic batch = SAVEPOINT per row inside the single command UoW transaction.** The bulk-import
  command runs under the normal `UnitOfWorkBehavior` (one wrapping tx). Each row's role assignment goes through
  `assign_role_to_user` (the R3 no-escalation definer guard); a non-conferrable role raises SQLSTATE 42501 →
  `ForbiddenException`, which ALSO aborts the PostgreSQL tx. A C# catch alone cannot un-abort it. Use
  `IUnitOfWork.CreateSavepointAsync($"bulk_row_{n}")` before each row and `RollbackToSavepointAsync` in the catch
  to un-abort — the failed row's provisioning + assignment roll back together (no orphan user) and the batch
  continues. Same pattern the WhatsApp referral-attribution handler uses. Record audit only AFTER row success
  (audit writer is on a dedicated connection, so it survives rollback anyway).
- **CSV-injection-safe export reuses the `SecurityFeatures.AuditCsv` approach** (RFC-4180 quoting + neutralise
  leading `= + - @` with a `'` prefix). New `PeopleCsv` static builder mirrors it. Export query runs on the READ
  chain, pages `IUserDirectory.ListByTenantAsync` (clamps take to 200) up to a 20k cap.
- **Tenant bound from signed `ICurrentUserContext.TenantId`; route id is cross-checked, not trusted** — a
  mismatch throws `ForbiddenException`. Batch cap 500 (validator → 422). Row statuses: created|linked|skipped|error.
- Tests: `PeopleImportExportTests` + `PeopleImportExportWebAppFactory` (7 tests). No-access principal = member of
  an EMPTY custom role (resolves to zero perms → 403 on both gates). Conferrable role = custom tenant role holding
  only `docslot.booking.read` (owner holds it WITH grant option); non-conferrable = system `super_admin`.
  See [[slice08_rbac_navigation]] for the escalation-guard semantics.
