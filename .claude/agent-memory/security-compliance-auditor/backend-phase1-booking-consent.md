---
name: backend-phase1-booking-consent
description: Phase-1 WhatsApp booking + DPDP behalf-consent OTP audit — both findings REMEDIATED & re-verified (PASS); only a LOW failed-path scrub-gap residual remains
metadata:
  type: project
---

Phase-1 "WhatsApp booking end-to-end with OTP consent" security audit. ORIGINAL VERDICT: PASS WITH CONDITIONS. REMEDIATION VERIFIED 2026-06-28: both conditions closed → FINAL VERDICT PASS (no BLOCKER/HIGH). 20/20 targeted tests (6 new WhatsAppLogRlsTests + consent + drain) green; full suite reported 149/149; build clean.

**REMEDIATION (verified live + source, NOT just coordinator claim):**
- Finding 1 closed: PatientConsentService.cs:54-56 now logs literal "[patient consent code sent]" to wa_message_log (code never enters journal). Outbox payload carries real text pre-send (delivery vehicle), then SECURITY DEFINER `docslot.mark_outbox_sent(uuid,text,tstz)` scrubs payload.text→'[redacted after send]' ONLY for message_intent='consent_otp' via jsonb_set. LIVE-PROVEN: consent row '*483920*'→'[redacted after send]'; non-consent 'Choose a department' untouched.
- Finding 2 closed: RLS enabled live on docslot.wa_message_log + docslot.outbox_messages with tenant_isolation_* policies = `tenant_id=current_tenant_id() OR =current_impersonated_tenant()` (no god-flag, not USING(true)). Grants to docslot_app stay SELECT/INSERT/UPDATE (no DELETE). Cross-tenant drain worker (no app.tenant_id) routes through SECURITY DEFINER fns: `claim_due_outbox(int)` (FOR UPDATE SKIP LOCKED), `mark_outbox_sent`, `mark_outbox_failed`, existing `requeue_stranded_outbox` — all prosecdef=t, search_path=docslot,pg_temp, parameterized, no dynamic SQL. OutboxDrainStore.cs rewritten to call them.

**RESIDUAL (LOW — tracked, not blocking):** `mark_outbox_failed` does NOT scrub the consent_otp payload. LIVE-PROVEN: a failed consent send leaves '*777111*' in payload and re-queues 'pending'; on terminal 'abandoned' it is NEVER scrubbed (scrub only happens in mark_outbox_sent). So on the SEND-FAILURE path the live code lingers in the (now RLS-isolated, single-use, attempt-limited) queue past the 15-min TTL. Suggested fix: also scrub consent_otp payload in mark_outbox_failed when status→'abandoned' (and ideally on each retry). Downgraded to LOW because table is now tenant-isolated + code is single-use/attempt-limited + window only on failure.
INFO: OutboxDrainStore.cs:16-17 class comment is now stale ("canonical schema does not put it behind RLS") — RLS was added; update the comment.

**What is solid (verified live + in code):**

**What is solid (verified live + in code):**
- `docslot.booking_consent_otps` (defined database/09_chat_identity.sql:115): carries tenant_id+FK, RLS enabled, policy `tenant_isolation_booking_consent_otps` = `tenant_id=current_tenant_id() OR =current_impersonated_tenant()` — mirrors the canonical booking policy in 05_security_hardening.sql:716, NO god-flag, NOT USING(true). Grant to docslot_app = SELECT/INSERT/UPDATE, NO DELETE. Live `polpermissive=t` but USING is restrictive (same as bookings table — established pattern).
- OTP code NEVER stored plaintext in the consent table: only base64(SHA256(salt+":"+code)), per-row 16-byte CSPRNG salt. Verify is constant-time (`CryptographicOperations.FixedTimeEquals`), attempt-limited (max_attempts=5 → status 'failed' + cancel), expiry-bounded (checked in SQL + again in code). See PatientConsentService.cs.
- SECURITY DEFINER fns (database/03_docslot.sql): `requeue_stranded_outbox(interval)` @1122 and `expire_stale_consent_otps()` @1151. Both `SET search_path=docslot,pg_temp`, owner=gtmkumar, NO dynamic SQL, parameterized. Live `prosecdef=t`. expire fn over-cancellation guard is tight: only cancels bookings WHERE status IN(pending,confirmed) AND patient_consent_status='pending'; floors capacity at GREATEST(count-1,0); re-opens only 'booked'→'available'. No double-free vs in-band cancel (WHERE clause won't re-match a cancelled row).
- Approval guard (BookingActionCommand.cs:50): behalf booking with AwaitingPatientConsent → BusinessRuleException (422). `AwaitingPatientConsent = BookedByType=='behalf' && PatientConsentStatus!='confirmed'` (Booking.cs:72). Consent laundering via reschedule BLOCKED (RescheduleBookingCommand.cs:64 rejects AwaitingPatientConsent; only confirmed consent carries forward).
- Inbound handler routes consent reply BEFORE booking state machine (ProcessInboundWhatsAppMessageCommandHandler.cs:69, short-circuits at step 4).
- DB CHECK constraints (cannot be app-bypassed): bookings.status includes 'checked_in'; patient_consent_status enum {not_required,pending,confirmed,denied,expired}; booked_by_type {self,behalf}; PAIRED constraint behalf⟺behalf_relation NOT NULL. wa_message_log.status CHECK {received,queued,sent,delivered,read,failed}.
- BookingListItemDto exposes only MaskedPhone — new fields (BookedByType/BehalfRelation/PatientConsentStatus/DoctorId) are operational, not PHI.
- Cutoff enforced in BookingCreationService.CreateAsync (authoritative for create AND reschedule).

**OPEN CONDITIONS issued this wave (must verify in a later wave):**
1. MEDIUM — Plaintext OTP code is written into `docslot.outbox_messages.payload` and `docslot.wa_message_log` (PatientConsentService.cs SendForBehalfBookingAsync logs the rendered template containing `*{code}*`). Defeats the hash-only intent: anyone with read on those 2 tables recovers a live code within the 15-min TTL. Condition: redact the code in the wa_message_log/outbox copy (store a templated placeholder, or null content for consent_otp intent), OR justify+document the residual risk.
2. MEDIUM — `outbox_messages` and `wa_message_log` have NO RLS (relrowsecurity=f; verified live) though they carry tenant_id. PRE-EXISTING (not introduced this wave) but now material because (1) lands the live OTP there. They are not in the file-05 RLS set. Condition: add tenant_isolation RLS to both (mirror booking policy) — relevant to the broader webhook-PHI posture too.

**Acceptable-but-noted (NOT findings):**
- Patient record + tenant_link created at behalf-booking time BEFORE consent (BookingCreationService.cs:37-41). Judged acceptable: the patient is a real data principal being contacted for consent; the booking is 'pending'/un-approved and auto-cancels on deny/expiry. Aligns with DPDP "create then seek consent for processing" so long as no PHI processing/approval happens pre-consent (guard holds).
- Consent-reply interception hijacks any message from a number with a pending consent — correct security behavior (patient must resolve consent first), minor UX cost.

INFO: build emits NU1903 MessagePack 2.5.192 high-sev advisory (AppHost) — out of wave scope, track separately.
