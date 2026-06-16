---
name: commission-live-wiring
description: Care Partners (commission) + Calendar live-API wiring quirks — DTO serialization gaps, 204 responses, RBAC execute-gating, calendar client-side rollup
metadata:
  type: project
---

Care Partners (commission, Slice 07) and Calendar are wired to the live .NET API behind `VITE_USE_REAL_API` (frontend `lib/backend/real.ts` + `index.ts`). The screens/contracts/mock adapters mirror the DTOs 1:1, so live READS are pure pass-throughs.

**Non-obvious facts to carry forward (verified 2026-06-16 against API on :5054, seed priyanka@apollocare.in/reception, tenant 11111111-1111-1111-1111-111111111111):**

- **CommissionRuleDto omits optional keys when unset** — `minCommissionInr`/`maxCommissionInr`/`maxMonthlyPerBrokerInr`/`firstBookingOnly` are absent from the JSON (NOT `null`). `real.listCommissionRules` fills them with `null`/`false` before zod-parse. If the backend later serializes them as explicit null/false, that shim can be removed.
- **Several commission WRITES return 204 No Content** — `setBrokerStatus`, `blacklistBroker`, `resolveDispute` (and `approveRule`). The live adapter synthesizes the mock's `CommissionCreated {id}` result so the invalidate-only hooks are mode-blind.
- **`raiseDispute` POST body REQUIRES `raisedBy`** (broker|tenant_staff|platform_audit) but the app-facing `RaiseDisputeRequest` has no such field — the dashboard is a fixed staff context, so the live adapter injects `raisedBy:'tenant_staff'`.
- **approve ≠ execute is real RBAC, not just UI.** The reception seed user has `commission.payouts.approve` but NOT `commission.payouts.execute`, so PayoutsTab's in-memory `can()` gate correctly hides the execute action on the approved payout. Don't "fix" this by always showing execute — it's the intended separation-of-duties.

**Why:** these were discovered while wiring the live adapters; the DTO gaps would silently fail zod-parse and the 204s would crash a naive pass-through.

**How to apply:** when extending commission live wiring or debugging a zod-parse failure on `/commission/*`, check for omitted optional keys and 204 bodies first. See [[api-contracts]] PASS 3 notes for the full endpoint map.

**Calendar** has NO week-aggregate endpoint — `real.getCalendarGrid` rolls one up from `GET /doctors` + per-(day×doctor) `GET /doctors/{id}/slots?date=` (~56 calls/load, concurrent, per-call failure tolerated). A dedicated heatmap endpoint is an open contract gap.

**Cleanup gotcha:** broker phone is stored UNCANONICALIZED ("+91 91234 50007", spaces preserved) in `commission.brokers`. Delete test brokers by `full_name`, and delete `commission.broker_tenant_links` first (FK).
