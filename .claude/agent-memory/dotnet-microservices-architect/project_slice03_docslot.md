---
name: slice03-docslot
description: DocSlot .NET slice 03 (docslot booking core) — Booking aggregate/state-machine, slot holds, durable idempotency, integration events, PHI masking + purpose-of-use. Clinical PHI deferred to 03b/05.
metadata:
  type: project
---

Slice 03 (`docslot`) implements the booking-lifecycle product surface. Builds on [[slice01-platform-core]] + [[slice02-platform-api]].

**Why:** Core product (bookings/doctors/patients/slots) + the two auditor-required prerequisites (durable idempotency, consent/purpose-of-use). Feeds slice-02 webhooks with REAL event sources.
**How to apply:** Reuse the booking aggregate/state-machine, slot-hold, and durable-idempotency patterns for any docslot mutation work.

## Tables mapped vs deferred
MAPPED (booking core, schema `docslot`): bookings, doctors, departments, patients (+patient_tenant_links — cross-tenant by phone), time_slots, opd_tokens, booking_status_history (trigger-written), wa_message_log (conversation read), healthcare_facilities (test seed). DEFERRED to 03b/05 (RLS-live, encryption/consent needed): prescriptions, lab_reports, abdm_health_records, patient_medical_history, drug_alerts — NOT served this slice. (Confirmed RLS enabled on exactly those 5 in the live DB.)

## Booking aggregate + state machine (mediq.Domain/Docslot/Booking.cs)
6 states EXACTLY per SQL CHECK: pending, confirmed, cancelled, completed, no_show, rescheduled (NOT the brief's "checked_in/requested" — SQL wins, ADR-007). Transitions: pending→{confirmed,cancelled,no_show,rescheduled}; confirmed→{completed,cancelled,no_show,rescheduled}; the other 4 terminal. Illegal transition → `InvalidBookingTransitionException`. **booking_number (BKG-YYYY-MM-NNNNN) assigned by DB trigger `trg_booking_number`** — never in C#. **booking_status_history written by DB trigger `trg_booking_status_log` on UPDATE** — NEVER insert history manually (double-log). Status stored via `_status` backing field mapped to the string column (`HasField`/`UsePropertyAccessMode(Field)`, `Ignore(Status)`).

## Slot hold + durable idempotency (APP-OWNED tables — FLAGGED)
The canonical schema has NO slot-hold table and NO idempotency table. Created via `CREATE TABLE IF NOT EXISTS` at startup by `OperationalSchemaInitializer` (IHostedService): `docslot.slot_holds` (TTL hold, FR-BOOK-02, 5-min) and `platform.idempotency_keys` (UNIQUE(tenant_scope,endpoint,key)). These are app-owned INFRA additions, NOT mutations of canonical product tables (no ALTER/DROP). FLAGGED as candidates to promote into canonical SQL. `SlotHoldService.HoldAsync` = a conditional INSERT...SELECT that only succeeds if slot available + capacity + no live hold (concurrency-safe). `DurableIdempotencyStore` replaces the slice-01 in-memory store; survives restart/scale-out (proven by a test that reads via a NEW store instance against the same DB). Idempotency store is now endpoint-aware: `IIdempotencyContext.Endpoint` = "METHOD path"; `IIdempotencyReplayMarker` lets the API re-stamp `WasReplayed=true`. `IRequireIdempotency` marker → behavior throws 422 if a booking/money command lacks the header.

## Booking-action ordering gotcha
`IBookingRepository.AddAndSaveAsync` FLUSHES immediately (not deferred to UoW) because the DB trigger assigns booking_number on insert AND the hold-conversion + OPD-token raw SQL reference the booking by FK. Action mutations (approve/cancel/etc.) ARE committed by the UnitOfWork behavior (tracked entity) and the status trigger logs history.

## Integration events (feeds slice-02 webhooks)
`IBookingEventPublisher` (Infra: BookingEventPublisher) translates booking transitions → `IntegrationEvent` and calls slice-02 `IWebhookPublisher` (sign→retry→outbox). Event tokens map to the SEEDED `platform_api.api_event_types`: created/**approved**(=confirmed)/cancelled/completed/no_show. Domain events stay inside; translate at the Application boundary.

## Consent / purpose-of-use (DPDP — auditor condition)
Full patient-record read (`GET /patients/{id}`) REQUIRES `X-Purpose-Of-Use` header (→ logged to `platform.purpose_of_use_log`) AND an active patient consent (else 403 via `Patient.HasActiveConsent`). NO clinical PHI returned (booking-core demographics only). List read-models carry ONLY masked phone (`PhoneMasker` — prefix+xxxx+last2); raw phone never serialized. Slice-02 `/public/patients|bookings` still return empty arrays (no PHI served) pending 03b.

## Permission-key flags
All `docslot.booking.*` / `docslot.doctor.read` / `docslot.slot.read` / `docslot.patient.read|update` keys EXIST in seed. **`docslot.patient.create` does NOT exist** → POST /patients gated on `docslot.patient.update` (FLAGGED). **No separate no-show permission** → no-show gated on `docslot.booking.complete`.

## Dashboard read-model
`BookingReadService` projects the exact SharedDataModel Dashboard DTOs the frontend already consumes (DashboardSummaryDto/BookingListItemDto). "Today" computed in Asia/Kolkata (`AT TIME ZONE 'Asia/Kolkata'`); slot instants emitted at +05:30; revenue = sum of doctors.consultation_fee for today's confirmed/completed; no-show rate = no_shows/terminal-today.

## Verify
`dotnet build` 0 errors (2 warnings = transitive MessagePack/Aspire). `dotnet test` 19/19 (8+6+5). Canonical 26 docslot tables + triggers unmodified; 2 app-owned operational tables added. Separate `DocslotWebAppFactory` seeds the full booking graph + swaps `IWebhookPublisher` for a recorder.
