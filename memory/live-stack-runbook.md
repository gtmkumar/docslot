---
name: live-stack-runbook
description: How to run the full live DocSlot stack (DB + .NET API + React) and the demo login
metadata:
  type: project
---

The frontend can run against the LIVE .NET API (not just mock). Verified working 2026-06.

**Run order:**
1. PostgreSQL 18 on :5432 (already a brew service) with DB `docslot_platform` (schema loaded; `vector`/`pgcrypto`/`uuid-ossp`/`pg_trgm` present).
2. Seed demo data (idempotent, run as a superuser e.g. `gtmkumar` — bypasses RLS): `database/seed_demo_login.sql` (tenant Apollo Care + user), then `seed_demo_doctors.sql`, `seed_demo_patients.sql`, `seed_demo_bookings.sql`.
3. API: `ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend/mediq/mediq.Api --urls http://localhost:5054` (connects as `docslot_app`; health at `/health`, OpenAPI at `/openapi/v1.json`).
4. Frontend live mode: `VITE_USE_REAL_API=1 npm run dev` (Vite proxies `/api` → :5054, configured in vite.config.ts — no CORS needed). Default `npm run dev` stays on the mock seam.

**Demo login (real + mock both work):** `priyanka@apollocare.in` / `reception` (tenant_owner in Apollo Care).

**Seeds (idempotent, run as superuser, in order):** `seed_demo_login.sql`, `seed_demo_doctors.sql`, `seed_demo_patients.sql`, `seed_demo_bookings.sql`, `seed_demo_schedules_reviews.sql`, `seed_demo_slots.sql` (available bookable slots — wizard + calendar need these), `seed_demo_commission.sql` (4 brokers incl. 1 blacklisted, 3 rules, 4 attributions, 2 payouts). `reset_demo_state.sql` restores the canonical baseline (bookings 4 pending/3 confirmed/2 completed/1 no_show, revenue ₹3,550; only booking-backed slots are 'booked', the rest 'available' — do NOT blanket-mark today's slots booked or the Calendar shows today full).

**Now wired live (as of 2026-06, ALL verified end-to-end):** auth, `/me`, permissions, backend-driven menus + badges, bookings/patients/doctors/dashboard reads, `/analytics`, the full booking write surface (create via wizard w/ live doctors+slots, approve/cancel/complete/no-show), add patient, add doctor (`POST /doctors`), Manage/Approve/Conversation panels (fetch `GET /bookings/{id}`), **Care Partners/commission** (brokers/attributions/rules/payouts/disputes — reads + writes; approve≠execute is enforced live: tenant_owner sees Approve but not Execute), and **Calendar** (week capacity heatmap rolled up client-side from `GET /doctors/{id}/slots`). Live seam: `src/lib/backend/`. Screens still on mock (no live endpoint): Settings, Developers writes, Security writes.

**WhatsApp inbound conversational booking (Step 13, .NET, built+audited 2026-06):** `GET/POST /api/v1/whatsapp/webhook` (anonymous, `[AllowAnonymous]`). GET = Meta verify (echoes `hub.challenge` if `hub.verify_token`==`WhatsApp:VerifyToken`). POST = signed inbound: HMAC-SHA256 of raw body (EnableBuffering) vs `X-Hub-Signature-256`, constant-time, 401 before processing. Tenant resolved ONLY from server-side `WhatsApp:PhoneNumberIdToTenant` map (dev: `PNID_APOLLO`→Apollo). Idempotent via `processed_messages` + deterministic `wa-{conv}-{slot}` key. State machine in `conversations` (who_for→dept→doctor→slot→confirm) over real doctors/slots; replies enqueued to `outbox_messages` (send STUBBED — no drain worker yet); booking created via the audited `CreateBookingCommand` (`booked_via='whatsapp'`). 52/52 integration tests. Security audit: **PASS-WITH-FINDINGS** (low/medium hardening: per-tenant dedup key, RLS on wa_* tables, dev-default secrets in `WhatsAppOptions`, outbox drain-worker). Dev secrets: VerifyToken `docslot-verify`, AppSecret `docslot-app-secret`. Files: `mediq.*/**/WhatsApp/*`, `WhatsAppController.cs`, `TenantScopeOverride.cs`. Drive a test convo by POSTing Meta payloads with the HMAC sig (seeds create NO wa rows — all wa data is from testing; safe to delete by tenant).

Still spec-only/needs external creds: LLM-dependent AI workflows (have an API key → enable in Python service), WhatsApp OUTBOUND send (needs Meta creds + an outbox drain worker).

**Resolved since first pass:**
- Enums are now STRINGS everywhere — added `[JsonConverter(typeof(EnumMemberJsonConverter))]` (in `mediq.SharedDataModel/Json/`) to BookingStatus/BookingSource/Gender/Language. Attribute-based so API + integration-test client both honor it. The frontend int-shim was removed.
- Added endpoints: `GET /analytics?period=`, populated `GET /me/badges`, enriched `DoctorDto` (departmentName/todayBooked/todayCapacity/rating/todayHours/nextAvailableSlot).
- Dev rate limit raised to 5000/min in Development (prod stays 100) in mediq.Api/Program.cs — full QA sweeps no longer 429.

**Gotchas:**
- Tenant comes from the JWT claim ONLY (no X-Tenant-Id fallback) — user must be a tenant member.
- `docslot.bookings` has NO `deleted_at` (cancellation is a status, not soft-delete) — don't filter by it.
- `docslot.time_slots` unique key is `(doctor_id, slot_date, start_time)` — seed available slots with `ON CONFLICT (doctor_id, slot_date, start_time)`.
- Integration tests need the DB to themselves: running `mediq.Api` on :5054 concurrently saturates Postgres `max_connections` and the suite shows spurious 500s. Stop the API before `dotnet test` → 48/48 green.
- AddDoctorPanel sends department NAME as `specialization` + `departmentId:null` (panel dept ids are mock tokens, not GUIDs); `GET /bookings/{id}` returns masked phone + no doctorId (harmless — actions key off booking id).
- See `.agents/memory/api-contracts.md`, [[docslot-qa-harness]], [[repo-state-vs-claudemd]].
