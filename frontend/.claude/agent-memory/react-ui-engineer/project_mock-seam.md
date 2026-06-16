---
name: mock-seam
description: The frontend mock seam — zod contracts + adapters that feature hooks consume, designed for a no-op swap to the real API
metadata:
  type: project
---

The frontend currently runs against a mock seam, NOT the .NET API (which is an early skeleton). Feature hooks call mock adapters today and a thin `apiFetch` wrapper tomorrow.

**How to apply:** when a screen needs data, add a zod schema to contracts, an adapter that parses through it, and a feature `api.ts` `useQuery` hook.

- Zod schemas: `frontend/src/lib/mock/contracts.ts`. Every response shape is a zod schema there; they mirror the C# DTO names so the backend can match 1:1. Camel-case wire. Wire enums are exact snake_case DB tokens.
- Adapters: `frontend/src/lib/mock/index.ts` — each function returns `Promise<T>` via `delay()` and `.parse()`s through the schema (so the app only ever sees validated data). Reuses domain mock data from `frontend/src/lib/data.ts` (`BOOKINGS`, `DOCTORS`, `DEPARTMENTS`, `DAYS`, `TIMES`, `buildSlotGrid()`, `PATIENTS`) and helpers from `frontend/src/lib/format.ts` (`maskPhone`, `inr`, `istSlot`). `DEPT_COLOR_KEY` (in index.ts) maps dept id → token color key — reuse it, never emit a hex.
- Feature query hooks: co-located in `features/<name>/api.ts` as `useQuery({ queryKey, queryFn: <adapter> })`. Mutations attach a STABLE `Idempotency-Key` generated ONCE per action by the caller (see `features/bookings/api.ts` + `lib/api-client.idempotencyKey()`), never inside the mutationFn.
- Permissions/nav: `getPermissions()` returns the signed-in demo user's effective set (`SIGNED_IN_PERMISSIONS` in index.ts) — add a key there if a new screen needs it to be exercisable in the demo. `getMenus()` returns the backend-driven nav tree (`HOSPITAL_MENUS`). Badges via `getBadges()`.
- The PHI rule holds in the adapter: list payloads carry masked phone only; raw phone never leaves the adapter in an aggregate. Analytics/calendar/doctors aggregates carry NO per-patient PHI at all.
