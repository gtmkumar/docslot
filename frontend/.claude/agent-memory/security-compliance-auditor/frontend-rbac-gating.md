---
name: frontend-rbac-gating
description: SPA RBAC gating is backend-driven via usePermissions().can() over /me/permissions; mock seed grants are mock-only; broker self-service paths are IDOR-safe.
metadata:
  type: project
---

**Permission gating (the enforced pattern):**
- `usePermissions()` in `src/lib/permissions.tsx` returns `can(key)` backed by a once-per-session TanStack Query over `getPermissions()` → `/me/permissions`. `staleTime/gcTime: Infinity`. Fail-closed: while loading, `can` returns `false`. It is a `Set.has(key)` lookup — NEVER `role === 'x'` in JSX.
- New surfaces all gate on `can(...)`: PayoutsTab (approve/execute/tds.issue), CampaignsTab (commission.campaign.manage), PortalScreen + ReferralLinks (commission.broker.*_self). Verified NO `role ===` / `.role` / `isAdmin` / `isBroker` anywhere in src/features/portal or src/features/commission.
- Approve vs Execute are separate mutations gated on separate keys (`commission.payouts.approve` vs `.execute`) — UI never collapses them (PayoutsTab.tsx:142-157). Frontend mirror of the server SoD gate.

**Mock seed is mock-only (cannot weaken real RBAC):**
- `SIGNED_IN_PERMISSIONS` (src/lib/mock/index.ts:123) gained commission.tds.issue, commission.broker.read_self / generate_link_self / create_booking_self. This constant is read ONLY by `mock.getPermissions` (mock/index.ts:205).
- The seam `src/lib/backend/index.ts` selects `USE_REAL_API ? real.getPermissions : mock.getPermissions`. With the real-API flag on, the live `/me/permissions` set is authoritative; the mock grants are inert.

**Broker self-service IDOR-safe:**
- All `/commission/me/*` calls (getBrokerWallet, listReferralLinks, createReferralLink, createPortalBooking in real.ts ~1197-1242; portal hooks in src/features/portal/api.ts) carry NO broker id in the path. Server resolves broker_id from the JWT `broker_id` claim. createReferralLink/createPortalBooking send `tenantId/targetDoctorId: null` (server-resolved). POSTs carry Idempotency-Key. Book-on-behalf result status is `awaiting_patient_consent` (patient WhatsApp OTP, DPDP).

**How to apply:** any new gated UI must use `can()`; flag any `role ===` branch as a defect. Mock-only RBAC seed additions are fine and not a finding. Reject any `/commission/me` path that interpolates a broker id.
