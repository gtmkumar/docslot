# Handoff — 2026-07-16: Tenant onboarding, geo capture, platform-scope polish, E2E

**Commit: `767bb4f` on `main`** (local, not pushed) — 40 files, +2080/−48. Working tree clean.
Sits on top of `276d82d` (Microsoft.OpenApi pinned to 2.11.0 — 3.x needs net11.0, don't re-upgrade).

## What shipped

### 1. Tenant onboarding (platform console → first clinic login)
- `POST /api/v1/tenants` (gated `platform.tenants.create`, super_admin): creates the clinic **and** its one-time `tenant_owner` invitation in ONE transaction. Token hash-at-rest, plaintext revealed exactly once, never idempotency-cached.
- UI: **Impersonate → New clinic** slide-over → invite-link reveal panel → owner opens public **/accept-invite**, sets their own password, signs in as Tenant Owner, manages staff via /team.
- Security auditor: **PASS**. Hardenings applied: audit rows written after both business writes; `?token=` stripped from the accept-page URL after capture. Audit rows use `TenantId: null` (dedicated-connection FK gotcha).
- No email dispatch yet (StubInvitationNotifier) — admin copies the link manually; the panel says so.

### 2. Location capture + geo tagging (New-clinic form)
- **Country → State → City cascade**: `frontend/src/features/tenants/india-geo.ts` (36 states/UTs, district-level cities, "Other (not listed)…" free-text escape).
- **PIN-code auto-fill**: `GET /api/v1/geo/pincode/{pin}` (`GeoController`, auth-only) = India Post directory (authoritative; decides 404) + OSM Nominatim centroid (best-effort; UA header per usage policy). `IMemoryCache` 24h hit / 10min miss.
- Typing 6 digits fills State+City and geo-tags the clinic; **removing/shortening the PIN un-fills only what the lookup wrote** (manual choices survive; slow-response race guarded; stale coords never submitted).
- Persistence: `pin_code` real column; coordinates in **`settings.geo` JSONB** (lat/long/source/tagged_at). No coordinate columns exist and remote `platform.tenants` is owned by `postgres` (password user-held) — to promote to real columns run as postgres:
  `ALTER TABLE platform.tenants ADD COLUMN latitude numeric(9,6), ADD COLUMN longitude numeric(9,6);` then ask Claude to move the write path.

### 3. Platform-scope (no-tenant super_admin) chrome
- Role chip → "System admin"; search placeholder → "Search actions…"; Settings + Overview queries gated on an active tenant (zero null-tenant 403s left).

### 4. Live E2E pass — Developers + Security & Compliance: ALL FLOWS PASS
- Network-verified against remote dev DB: client register/approve/suspend/rotate/scopes/rate-limits, webhooks create/edit/deliveries, request logs, audit-chain verify ("Chain intact"), DPDP/erasure/breach panel guardrails. Destructive compliance actions (anchor, erasure/breach submit) deliberately NOT executed.
- All 4 cosmetic findings **fixed in this commit**: pending-vs-suspended badge precedence (+ one-click Approve), secret-reveal titles by intent (registered/rotated/webhook-created), "1 scope" plural, dashboard-summary 403 gate.

## Verification (all at commit time)
- .NET integration: **399/399** (local DB; `env "ConnectionStrings__platform-db=Host=localhost;...;Username=docslot_app" dotnet test`)
- AI service: **51/51** (`ai_service/.venv/bin/pytest`)
- Frontend: `tsc` clean, `vite build` ✓
- 5 pre-existing EF1002/EF1003 warnings in `SecurityReadService.cs` (untouched, full-rebuild only)

## Known gotchas discovered
- **EF `SqlQueryRaw`**: a literal `{}` ANYWHERE in the SQL string (even a comment) throws `FormatException: Expected an ASCII digit` — use argless `jsonb_build_object()` for empty JSONB.
- **Uncontrolled selects** silently drop programmatic `setValue` when the target option renders in the same commit — the state/city selects are controlled (`value={watch(...)}`).

## Running stack (dev)
- API :5054 (`dotnet run --project backend/mediq/mediq.Api --no-build`, remote dev DB via user-secrets), AI service :8000, Vite :5173 (`VITE_USE_REAL_API=1`). Superadmin: `superadmin@docslot.io` (remote).

## Open items
1. Commit `767bb4f` is **not pushed**.
2. Test residue on remote dev DB (labeled, disabled): API client `e2e-test-client` (suspended) + webhook "E2E Test Hook" (inactive) — removable via SQL on request.
3. Tenants are born `status='active'` (no trial/billing gate) — revisit when billing lands.
4. Remote superadmin password was pasted in chat — rotation offered.
5. Optional: promote geo tag to real columns (needs the postgres-owned ALTER above).
