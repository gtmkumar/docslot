---
name: integration-test-db
description: mediq.IntegrationTests run against a LIVE local Postgres (docslot_platform / user gtmkumar); full-suite parallel runs are flaky due to connection exhaustion.
metadata:
  type: project
---

mediq.IntegrationTests (backend/mediq/tests/mediq.IntegrationTests) boot real `WebApplicationFactory<Program>` hosts against a live local PostgreSQL: `Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar` (see each `*WebAppFactory.cs`, const `ConnectionString`). Each factory seeds its own tenant/user graph with per-instance GUIDs and soft-cleans on dispose.

**Why:** No Testcontainers — the suite expects the canonical schema already applied to a local `docslot_platform` DB. Parallelism is enabled (`AssemblyInfo.cs`).

**How to apply:** When running the FULL suite, multiple factory hosts each open their own Npgsql pool; against a single local Postgres (`max_connections` ~100) this intermittently exhausts connections → cascading `500` on `/api/v1/auth/login`, sub-20ms fast failures, and `53300: remaining connection slots are reserved for roles with the SUPERUSER attribute`. Each test CLASS passes reliably in isolation (`--filter "FullyQualifiedName~<Class>"`). Leaked idle backends from prior `dotnet test` runs accumulate (seen 59–76 idle) and worsen it; they reap on idle timeout. To demonstrate green, run per-class or wait for idle connections to drain first. Do NOT pg_terminate_backend broadly — it was denied as out-of-scope. This flakiness is environmental, not a code regression.

**Autonomous background loops must be OFF in the test host.** `TestHostConfig.cs` (a `[ModuleInitializer]`) sets env `WhatsApp__OutboxWorkerEnabled=false` for the whole integration assembly. The WhatsApp `OutboxDrainWorker` polls every 5s opening a DbContext scope per host; with ~8 parallel factory hosts that extra churn was enough to TIP the already-tight pool into the 53300 / cascading-500 failures (proven: worker on ⇒ 2–4 failures/run; worker off ⇒ 54/54 three runs straight). The worker stays default-ON for the real app in Development. Lesson for any future `BackgroundService`: disable it in the test host unless a test specifically drives it, and have such tests exercise the store/handler directly rather than racing the live loop.
