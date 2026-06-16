---
name: frontend-phi-idempotency
description: DocSlot React SPA — established PHI-masking, idempotency, and RBAC-gating seams and where they live
metadata:
  type: project
---

DocSlot frontend (`frontend/src/`) security-relevant seams as of Wave 3:

- PHI masking lives in `lib/format.ts` `maskPhone()` — keeps +CC head + last 2 digits, replaces rest with `·`. Use it for any list/aggregate/command-palette surface.
- RBAC gate is `lib/permissions.ts` `usePermissions().can(key)` — fetched once/session (staleTime Infinity), fail-closed (returns false while loading). No `role===` branches exist anywhere (verified by grep). Nav is backend-driven via `features/navigation/api.ts` → mock `getMenus()` mirroring `platform.get_user_menus()`.
- Idempotency seam is `lib/api-client.ts` `idempotencyKey()` + `apiFetch` (attaches `Idempotency-Key` header when `req.idempotency` set). The mock adapter is `lib/mock/index.ts`; feature hooks call mock today, swap to apiFetch later.
- PatientChip (`features/bookings/components/PatientChip.tsx`) shows UNMASKED phone — intentional, it's a deliberate opened detail panel (accepted purpose-of-use context).
- ApproveCollectPanel surfaces amount + UPI link in a WhatsApp preview before send — no silent charge (good, keep this invariant).

**Why:** these are the load-bearing controls a frontend audit must re-verify each wave.
**How to apply:** when auditing new frontend surfaces, confirm list/aggregate phone goes through maskPhone, money/booking POSTs flow an idempotencyKey, and gating uses can() not roles. See [[drift-watchlist]] for the specific places these have broken.
