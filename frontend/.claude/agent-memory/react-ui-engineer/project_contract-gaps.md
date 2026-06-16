---
name: contract-gaps
description: Backend contract gaps discovered building the doctors/calendar/analytics screens — endpoints that don't exist yet
metadata:
  type: project
---

Building the 4 missing admin screens surfaced backend endpoints that do not exist yet. The mock seam stands in for them; flagged to the orchestrator in `.agents/memory/api-contracts.md`.

**Why:** the .NET API is an early skeleton; these screens were built from the schema/prototype ahead of their endpoints. **How to apply:** when the backend wave for these lands, the real DTOs must match these zod schemas (in `frontend/src/lib/mock/contracts.ts`) for a no-op swap.

- **Doctors directory** — no doctor-card / OPD-load DTO. Frontend `DoctorCardSchema` needs: profile (name, qual, fee, room, rating, initials) + today's `todayCount`/`todayCapacity` aggregate + `nextSlot` (Asia/Kolkata) + `hours` window + a dept `colorKey` (token KEY, not hex). Maps to `GET /api/v1/doctors` (`docslot.doctor.read`) plus a per-doctor today-load rollup. Add-doctor currently gated on `docslot.patient.update` because there's no `docslot.doctor.create` in the permission seed.
- **Calendar week heatmap** — no slot-capacity-grid endpoint. `CalendarGridSchema` needs a per-(day,timeslot) rollup: cell.state ∈ open|tight|full|blocked|off + booked/capacity, columns flagged `isToday`. Maps to an aggregate over `GET /doctors/{id}/slots?date` (`docslot.slot.read`).
- **Analytics** — no analytics endpoint. `AnalyticsSchema` needs KPIs (pre-formatted value strings + numeric deltaPct + higherIsBetter flag), weekly volume split (whatsapp vs direct), top departments, and a WhatsApp conversation funnel. Aggregate-only, NO PHI. Screen gates on `docslot.analytics.read` (already in seed).

Status: OPEN — awaiting backend waves. No `docslot.doctor.create`/`docslot.report`-style finer keys exist for doctors yet (same FLAG already noted for patients in api-contracts.md).
