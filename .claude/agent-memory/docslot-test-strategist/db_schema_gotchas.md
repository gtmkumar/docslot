---
name: db-schema-gotchas
description: DocSlot DB schema specifics that bite tests — wa_message_log columns, consent OTP table, reaper function return values
metadata:
  type: reference
---

Schema facts learned while writing Phase-1 booking tests (verify against current SQL before relying on these):

- **`docslot.wa_message_log`**: PK column is `log_id` (NOT `wa_message_log_id`). There is NO `created_at`
  column; the timestamp is `sent_at` (NOT NULL, default `now()`). Status CHECK accepts
  `{received,queued,sent,delivered,read,failed}` (NULL allowed). Direction CHECK: `{inbound,outbound}`.
- **`docslot.booking_consent_otps`** (file 09): behalf-booking DPDP consent. Columns: code_salt, code_hash
  (salted SHA-256, code never stored), status `{pending,confirmed,denied,expired,failed}`, attempts/max_attempts
  (default 5), expires_at. RLS policy `tenant_isolation_booking_consent_otps` is
  `USING (tenant_id = platform.current_tenant_id() OR tenant_id = platform.current_impersonated_tenant())` —
  not `USING(true)`. Cascades from bookings (ON DELETE CASCADE) and tenants.
- **`docslot.bookings`**: behalf/consent columns are `booked_by_type` ('self'|'behalf'), `behalf_relation`,
  `behalf_booker_phone`, `patient_consent_status`, `rescheduled_from_booking_id`, `checked_in_at`,
  `patient_phone_at_booking`. `booking_number` is assigned by BEFORE-INSERT trigger `trg_booking_number`, so a
  direct owner INSERT can omit it.
- **Reaper functions** (file 03, both SECURITY DEFINER, `docslot_app` has EXECUTE):
  - `docslot.requeue_stranded_outbox(interval)` → returns count of 'processing' rows older than the interval
    requeued to 'pending'.
  - `docslot.expire_stale_consent_otps()` → expires lapsed pending OTPs, cancels the awaiting behalf booking
    (status→cancelled, patient_consent_status→expired), frees the slot. **Returns the number of SLOTS FREED**
    (final UPDATE ROW_COUNT), not the OTP count — assert `>= 1`, don't assume it equals the OTP count.
- **`docslot.outbox_messages`** is NOT under RLS (operational queue; carries tenant_id for attribution). Tests
  can seed/read it as owner freely. Behalf OTP is enqueued with `message_intent='consent_otp'`, the patient
  phone in `payload->>'to'`, and the 6-digit code embedded as `*<code>*` in `payload->>'text'`.
