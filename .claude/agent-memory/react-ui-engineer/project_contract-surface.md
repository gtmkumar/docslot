---
name: frontend-contract-surface
description: Mock adapter contract the dashboard consumes (lib/mock) and the menu/permission/badge/summary shapes the .NET API must mirror.
metadata:
  type: project
---

Wave 1 (Frontend Foundation) established the data seam in `frontend/src/lib/mock/`.
All shapes are zod schemas in `lib/mock/contracts.ts`; the mock adapter (`lib/mock/index.ts`) parses through them, so the real `apiFetch` swap reuses the same schemas.

**Why:** the backend (dotnet-clean-arch wave) must mirror these 1:1 so feature `api.ts` files change one import (mock fn → apiFetch) and nothing else.

**How to apply:** when a backend endpoint lands, validate its response against the matching zod schema; if shapes diverge, that is a contract gap to route through the orchestrator.

**RECONCILED with Slice 01 backend (`platform_core`) — these now match the real .NET DTOs 1:1 (camelCase wire). Verified against mediq.SharedDataModel.** The mock→real swap is now a no-op for zod on these three.

Contract functions (all return Promises, all zod-parsed):
- `getMenus()` → **bare `MenuNode[]`** (NOT `{tenantType, items}` — that wrapper was wrong; the API serves an assembled `MenuNodeDto[]`). MenuNode = `{ id, parentId|null, key, label, labelHi|null, icon|null, route|null, sortOrder, isSectionHeader, badgeSource|null, children: MenuNode[] }`. Section headers: `isSectionHeader===true` (NOT the old `route===null` convention). `icon`/`labelHi` are NULLABLE — Sidebar falls back (`iconForKey(icon ?? '')`, `labelHi ?? label`). Route → `GET /api/v1/me/menus`.
- `getPermissions()` → **`{ userId, tenantId, permissionKeys: string[] }`** (PermissionSetDto — NOT `{keys}`). `usePermissions` reads `.permissionKeys`. Deny-wins applied server-side. Route → `GET /api/v1/me/permissions`. **RESOLVED: keys ARE `docslot.`-prefixed** (verified against `database/03_docslot.sql` `permission_key` values). All `can('...')` call sites + the mock seed now use canonical keys: `docslot.booking.{read,create,approve,cancel,reschedule}`, `docslot.patient.{read,update}`, `docslot.doctor.read`, `docslot.slot.read`, `docslot.analytics.read`. Mappings applied: `calendar.read`→`docslot.slot.read`; add-patient gated on `docslot.patient.update` (no `docslot.patient.create` exists — **gap to flag: backend may need a patient-register permission**); manage/conversation panels gate on `docslot.booking.read`.
- `getBadges()` → **`{ counts: Record<badgeSource, number> }`** (BadgesDto — NOT a bare record). `useBadges` unwraps via `select: dto => dto.counts`. Route → `GET /api/v1/me/badges`. **RESOLVED: badge key is `pending_bookings_count`** (the seeded `navigation_menus.badge_source` in `database/08_rbac_navigation.sql`); mock + the bookings menu node's `badgeSource` use it.
- Auth (Slice 01, not yet wired in FE): `POST /api/v1/auth/login {email,password,tenantId?,deviceInfo?}` → `TokenResponse {accessToken, refreshToken, expiresInSeconds, userId, activeTenantId, mfaRequired}`; `/auth/refresh {refreshToken}`; `/auth/logout {refreshToken?}`; `GET /api/v1/me` → `MeDto {userId, email, fullName, preferredLanguage, timezone, mfaEnabled, activeTenantId, tenants: [{tenantId, tenantCode, displayName, tenantType, isPrimary}]}`. `tenantType` lives on MeDto, not on menus.
- `getDashboardSummary()` → `{ liveQueue, liveQueueWhatsapp, liveQueueWalkIn, confirmedToday, revenueToday, noShowRate, activeConversations }`.
- `listBookings()` → `BookingRow[]` (id, token, patient, **maskedPhone**, doctorName, dept, date, time, status, source, note, createdAgo). PHI: phone is masked at the adapter — raw phone NEVER in a list/aggregate payload (DPDP). Mirrors `BookingListItemDto.MaskedPhone`. Full number = separate detail call.
- `getConversation(bookingId)` → `ChatMessageDTO[]` (from, text, at, interactive?, system?).

**Canonical wire enums (Wave 4 — mirror SQL CHECK constraints, snake_case; in `contracts.ts` as `BookingStatusSchema`/`BookingSourceSchema`, also `lib/types.ts`):**
- `status`: pending, confirmed, cancelled, completed, no_show, **rescheduled** (6).
- `source` (=`booked_via`): whatsapp, dashboard, api, walk_in, phone_call (5). NOT `walk-in`/`phone`.
- Display labels live in i18n only (`status.*` / `source.*`), never in the enum. StatusPill maps all 6 (rescheduled = info tint + CalendarClock).

**Wave 3 (Dashboard) added to `lib/mock`** (schemas in `contracts.ts`):
- `getAgentPanel()` → `{ activeConversations, sparkline:number[], avgResponseMins, selfServedPct, handedPct, dropOffPct, funnel:[{key:'greeted'|'selectedDept'|'pickedSlot'|'confirmed', count, pct}] }`.
- `getDepartmentLoad()` → `DepartmentLoad[]` `{ id, name, colorKey, booked, capacity }`. **colorKey is a TOKEN key** ('primary'|'accent'|'info'|'warn'|'muted'), never a hex — backend should return the same token-key vocabulary so the UI stays hex-free.
- `getFloorDoctors()` → `FloorDoctor[]` `{ id, name, spec, room, nextSlot(24h IST), seenToday, initials }`.
- `listPractitioners(deptId?)` → `Practitioner[]` `{ id, name, spec, deptId, fee, room, next, initials }`.
- `listSlots(doctorId)` → `Slot[]` `{ time(24h IST), state:'open'|'tight'|'full'|'blocked' }`.
- Mutations (Wave 4: EVERY one takes an `idempotencyKey: string`): `approveBooking(id, key)`, `cancelBooking(id, reason, key)`, `createBooking(draft, key)` → `{id, token, status:'confirmed'}`, `sendPaymentLink({bookingId, amount, expiresInMins, idempotencyKey})` → `{bookingId, link(upi://…), amount, expiresInMins}`. The mock de-dupes by key (`idemCache`), mirroring server idempotency.

**Mutation routes the backend must provide** (all POST, all carry Idempotency-Key):
`POST /api/v1/bookings/{id}/approve`, `/cancel` (body: reason), `POST /api/v1/bookings` (create), `POST /api/v1/bookings/{id}/payment-link`.

api-client (`lib/api-client.ts`): `apiFetch<T>(path, {method, tenantId→X-Tenant-Id, body, idempotency, signal})` + `idempotencyKey()` helper. Base URL from `VITE_API_BASE_URL` (default `/api/v1`). POSTs must attach Idempotency-Key.

**Idempotency rule (Wave 4 — security VETO fix):** the key is a STABLE key generated ONCE per logical action by the CALLER (component, on action start), passed in as `input.idempotencyKey` — NOT inside the mutationFn (that regenerates per retry and defeats de-dup). For the ApprovalQueue deferred-undo flow the key is created in `handleApprove` and captured in the 5s-timer closure so the eventual fire reuses it. Mutation hooks in `features/bookings/api.ts`; inputs `ApproveBookingInput`/`CancelBookingInput`/`CreateBookingInput`/`SendPaymentLinkInput` all include `idempotencyKey`.

**PHI rule:** `maskPhone()` is applied at the list adapter; CommandPalette patient search `value` uses `${name} ${id}` (non-PHI), only the masked phone is rendered. Full phone shows only in detail panels (PatientChip) where the staff action needs it.
