# DocSlot Test Strategist — Memory Index

- [Integration test harness conventions](integration_test_harness.md) — live-DB factories, worker-disable env vars, owner-vs-app roles, idempotency-key, slot cutoff seeding.
- [Phase-1 booking test map](phase1_booking_tests.md) — which test files/cases guard behalf-OTP consent, reschedule, check-in, cutoff, reapers, consent RLS.
- [Phase-2 commission pipeline tests](phase2_commission_pipeline_tests.md) — earning/settlement/payout-dryrun/reversal/dispute/tiered/authz/RLS files + dispute enum + wallet-reset gotchas.
- [Audit-log FK cleanup trap](audit_log_fk_cleanup.md) — never hard-DELETE users/tenants that have audit_log rows; soft-delete instead.
- [DB schema gotchas for tests](db_schema_gotchas.md) — wa_message_log PK/columns, consent OTP table, reaper function return semantics.
- [User profile: Goutam](user_goutam.md) — owns DocSlot QA/test strategy; DB-first, real-PG validation non-negotiable.
