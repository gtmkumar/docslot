---
name: local-integration-test-db-override
description: How to run the .NET live-DB integration suite locally when user-secrets point at the remote dev DB
metadata:
  type: reference
---

The integration suite (`backend/mediq/tests/mediq.IntegrationTests`) seeds via a hardcoded LOCAL owner
connection (`Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar`, see
`RbacSuperAdminGucWebAppFactory.OwnerConnectionString`), but the app-under-test reads its connection from
`ConnectionStrings:platform-db`, which via `dotnet user-secrets` points at the REMOTE Hostinger dev DB
(`187.127.154.205/docslot_platform_dev`). That mismatch makes every login-based test fail with **401** (the
factory seeds the user into local, the app authenticates against remote).

To run locally, override the app connection to the same local DB the factory seeds, using the RLS-bound
`docslot_app` role (NOT the owner — the suite exercises RLS/`app.is_super_admin` GUC wiring):

```
env "ConnectionStrings__platform-db=Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app" \
  dotnet test backend/mediq/mediq.sln --filter "FullyQualifiedName~SomeTests" --no-build
```

Notes:
- zsh can't do `ConnectionStrings__platform-db=...` inline (hyphen in name) — use `env "…=…"`.
- Local Postgres uses trust auth, so both `docslot_app` and `gtmkumar` connect passwordless.
- The local `docslot_platform` DB is loaded from a bundle build; it may lag the numbered SQL source files
  (the bundle is NOT idempotent). Tests that assert on newly-seeded nav/permission rows need a fresh reload.

Related: [[hostinger-vps-docslot-databases]] (user auto-memory) records the remote-DB repoint.
