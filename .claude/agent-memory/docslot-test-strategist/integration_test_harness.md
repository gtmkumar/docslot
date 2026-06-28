---
name: integration-test-harness
description: How DocSlot .NET integration tests are structured — live DB, factories, worker-disable, owner/app roles, idempotency, cutoff seeding
metadata:
  type: reference
---

DocSlot integration tests live in `backend/mediq/tests/mediq.IntegrationTests`, run with
`dotnet test backend/mediq/mediq.sln`. They run against the LIVE local Postgres `docslot_platform`
(NOT Testcontainers in this tree), so every factory/fixture seeds and cleans up its own rows.

Key conventions:
- **Worker-disable suite-wide**: `TestHostConfig.cs` (a `[ModuleInitializer]`) sets
  `WhatsApp__OutboxWorkerEnabled=false` and `Booking__MaintenanceWorkerEnabled=false`. Do NOT rely on a
  background worker running; drive sweeps directly (resolve the store from `factory.Services` and call
  `RequeueStrandedAsync` / `ExpireStaleAsync`, or call the SQL function over a connection).
- **Two DB roles**: owner `Username=gtmkumar` (RLS-exempt; seed cross-tenant arrangement rows) and
  least-privilege `Username=docslot_app` (NOBYPASSRLS; mirrors the running API for RLS assertions). Set
  tenant context with `SELECT set_config('app.tenant_id', @t, false)`. Pattern lives in `BookingRlsTests.cs`.
- **Cutoff seeding**: `DocslotWebAppFactory` seeds `appointment_settings.bookingCutoffHours=2`. `SlotDate` is
  today+3 so seeded slots clear the cutoff. To test cutoff REJECTION, seed a slot today within 2h.
- **HTTP mutations require an `Idempotency-Key` header** (see the `PostAsync` helper pattern in
  `DocslotBookingTests.cs` / `SlotManagementTests.cs`).
- **WhatsApp inbound** is driven by signing the raw JSON body with HMAC-SHA256 over
  `WhatsAppWebAppFactory.AppSecret` into `X-Hub-Signature-256` (see `WhatsAppInboundTests.cs`). The
  `phone_number_id → tenant` map is injected via in-memory config in `WhatsAppWebAppFactory`.
- **Confirm-step retry pattern**: WhatsApp "YES" booking creation can transiently fail under parallel-suite
  load (global audit-chain advisory lock); existing tests resend "YES" up to 3x WHILE no booking exists yet.
  A real logic break fails every attempt, so this doesn't mask bugs. Mirror it for behalf flows.

Baseline as of 2026-06: 162 integration tests pass clean (was 116; +18 from the Phase-2 commission
money-pipeline suite — see [[phase2-commission-pipeline-tests]]). Full `dotnet test` runs ~6m against the
single local Postgres; `dotnet test` buffers all stdout until completion (no streaming).
