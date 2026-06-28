---
name: phase1-booking-tests
description: Map of Phase-1 booking integration tests — which file guards behalf-OTP consent, reschedule, check-in, cutoff, reapers, consent RLS
metadata:
  type: reference
---

Phase-1 booking feature tests added 2026-06 (4 new files in `backend/mediq/tests/mediq.IntegrationTests`):

- **`BehalfConsentOtpTests.cs`** (uses `WhatsAppWebAppFactory`): DPDP behalf-OTP consent E2E over the signed
  WhatsApp webhook. Booker books for a separate patient number → behalf booking pending + consent OTP enqueued
  to the patient. Code recovered from `outbox_messages.payload->>'text'` (intent `consent_otp`), patient reply
  driven over the webhook. Covers: pending-consent persistence + OTP/outbox creation; approve BLOCKED 422 until
  confirmed then succeeds; wrong code reprompts (consent stays pending); 'NO' denies+cancels+frees slot;
  exhausting max_attempts denies (OTP→'failed'). NOTE: creates an on-demand tenant_owner admin for the approve
  path — clean it up via SOFT-delete (see [[audit-log-fk-cleanup]]).
- **`BookingLifecyclePhase1Tests.cs`** (uses `DocslotWebAppFactory`): reschedule (old→'rescheduled', new
  booking links via rescheduled_from_booking_id, old slot freed/new consumed; too-soon 422; checked_in/terminal
  422); check-in (confirmed→checked_in sets checked_in_at; checked_in→complete; pending→check-in 422); cutoff
  (within 2h 422; beyond succeeds).
- **`OutboxStrandedReaperTests.cs`** (uses `WhatsAppOutboxWebAppFactory`): outbox 'processing' reaper — stale
  (>5min) row requeued via `IOutboxDrainStore.RequeueStrandedAsync` resolved from host DI AND via the SQL fn
  directly; a fresh 'processing' row is left alone.
- **`ConsentOtpComplianceTests.cs`** (DB-level, owner+app roles, mirrors `BookingRlsTests`): wa_message_log
  canonical-status CHECK (accept 6 valid via Theory, reject bogus → 23514); consent-OTP RLS (cross-tenant
  invisible/insert-blocked, policy not USING(true)); consent-OTP expiry sweep (OTP→expired, booking→cancelled
  with consent expired, slot freed). COMPLIANCE-flagged: DPDP consent + tenant isolation → needs
  security-compliance-auditor sign-off.

Never-regress invariants these guard: behalf booking requires consent before confirm; tenant isolation
(RLS cross-tenant SELECT returns 0); reschedule lineage + slot capacity conservation.
