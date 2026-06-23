# Agent Memory Index

- [Integration test DB + flakiness](project_integration_test_db.md) — tests hit live local Postgres; parallel WebAppFactory hosts exhaust connections (flaky in full-suite runs).
- [Write-slice conventions](feedback_write_slice_conventions.md) — how a create endpoint vertical slice is structured in this .NET solution.
- [WhatsApp inbound gotchas](project_whatsapp_inbound.md) — anonymous tenant-scoping override pattern, last_relation excludes 'self', bookings.booked_at, jsonb content.
- [ExecuteSqlRaw brace literals](feedback_execsqlraw_brace_literals.md) — raw SQL with params is string.Format'd; literal {} (e.g. '{}'::jsonb) throws FormatException → use jsonb_build_object().
