# React UI Engineer — Agent Memory

- [Screen build conventions](project_screen-conventions.md) — how DocSlot list/grid screens are structured (states, filters, slide-over CRUD, perms).
- [Mock seam + zod contracts](project_mock-seam.md) — where contracts/adapters live and how the mock→real swap stays a no-op.
- [Contract gaps from screen wave](project_contract-gaps.md) — doctor-card, calendar-heatmap, analytics endpoints don't exist yet.
- [Live API seam](project_live-api-seam.md) — VITE_USE_REAL_API flag, lib/backend, and verified .NET DTO quirks (int enums, dashboard field names, /dashboard→/ menu route).
- [IAM privilege matrix](project_iam-matrix.md) — Team & Roles Slice 2: live /api/v1/iam matrix grid, seam fns, optimistic cell toggles, duplicate + effective-access panels, gating.
- [User management revamp](project_user-management.md) — Users-side /team: editUser slide-over, lifecycle actions (deactivate/reset/edit), list toolbar (search+status filter), perm keys, UserListItem quirks.
- [Clinical live-wiring](project_clinical-live-wiring.md) — Phase-3 s4: clinical/ABDM/consent on the live seam, X-Purpose-Of-Use on ALL reads, medical-history CRUD UI, contextual break-glass→re-fetch.
