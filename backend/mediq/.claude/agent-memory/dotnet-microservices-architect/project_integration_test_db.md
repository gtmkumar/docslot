---
name: integration-test-db
description: mediq.IntegrationTests run against a LIVE local Postgres (docslot_platform / user gtmkumar); full-suite parallel runs are flaky due to connection exhaustion.
metadata:
  type: project
---

mediq.IntegrationTests (backend/mediq/tests/mediq.IntegrationTests) boot real `WebApplicationFactory<Program>` hosts against a live local PostgreSQL: `Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar` (see each `*WebAppFactory.cs`, const `ConnectionString`). Each factory seeds its own tenant/user graph with per-instance GUIDs and soft-cleans on dispose.

**Why:** No Testcontainers — the suite expects the canonical schema already applied to a local `docslot_platform` DB. Parallelism is enabled (`AssemblyInfo.cs`).

**How to apply:** When running the FULL suite, multiple factory hosts each open their own Npgsql pool; against a single local Postgres (`max_connections` ~100) this intermittently exhausts connections → cascading `500` on `/api/v1/auth/login` and sub-20ms fast failures. Each test CLASS passes reliably in isolation (`--filter "FullyQualifiedName~<Class>"`). Leaked idle backends from prior `dotnet test` runs accumulate (seen 59–76 idle) and worsen it; they reap on idle timeout. To demonstrate green, run per-class or wait for idle connections to drain first. Do NOT pg_terminate_backend broadly — it was denied as out-of-scope. This flakiness is environmental, not a code regression.
