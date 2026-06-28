# Handoff — Issue #3: Support Impersonation (end-to-end)

_Last updated: 2026-06-25. Branch: `main`._

## 1. TL;DR

Issue #3 (“audited-by-construction guard for `app.impersonated_tenant`”) is **complete and merged** end-to-end: DB guard → per-transaction GUCs → `begin`/`end` lifecycle functions → HTTP endpoints → oversight read surface → frontend banner. A **Begin-impersonation UI trigger** was added afterward and is **working but not yet committed**. All four backend slices passed `security-compliance-auditor` review.

The full local stack is currently **running** and the feature is **clickable in the browser**.

## 2. What’s merged vs. uncommitted

**Merged to `main`:**
- `2719470` + PR **#4** (`1fb6e31`) — DB guard, `begin`/`end` functions, begin/end endpoints, oversight read surface, **frontend banner**, WhatsApp test-flake fix.
- `9f07bbf` + PR **#5** (`7d8c597`) — auditor memory record.

**Uncommitted (working tree) — the Begin-impersonation trigger UI:**
- New: `frontend/src/features/impersonation/components/BeginImpersonationPanel.tsx`
- Modified: `frontend/src/app/{i18n,router}.ts(x)`, `frontend/src/components/layout/{TopbarActions,SlideOverHost}.tsx`, `frontend/src/features/impersonation/api.ts`, `frontend/src/lib/backend/{index,real,mutations-mock}.ts`, `frontend/src/lib/mock/contracts.ts`, `frontend/src/stores/ui.ts`
- Agent memory: `.agents/memory/api-contracts.md`, `.claude/agent-memory/react-ui-engineer/project_impersonation.md`
- `frontend/.env.local` (`VITE_USE_REAL_API=1`) — **gitignored**, local-only dev toggle (see §5).

➡️ **Action:** commit the trigger via a branch + PR (frontend `tsc` + `vite build` already pass).

## 3. Running services (local)

| Service | URL | Start command |
|---|---|---|
| Web app (SPA) | http://localhost:5173 | `cd frontend && npm run dev -- --port 5173 --strictPort` |
| .NET API | http://localhost:5054 (OpenAPI `/openapi/v1.json`) | `ASPNETCORE_ENVIRONMENT=Development dotnet run --project backend/mediq/mediq.Api --no-launch-profile --urls http://localhost:5054` |
| AI service (FastAPI) | http://localhost:8088 (Swagger `/docs`) | `cd ai_service && .venv/bin/uvicorn app.main:app --host 0.0.0.0 --port 8088` |
| PostgreSQL | localhost:5432 / `docslot_platform` | already running |

- The SPA proxies `/api` → `:5054` (Vite config). The API connects to `docslot_platform` as `docslot_app`.
- Logs (this session): `…/scratchpad/{api,ai,frontend}.log`.
- To stop a service: `kill $(lsof -nP -iTCP:<port> -sTCP:LISTEN -t)`.
- **Gotcha:** stale instances from prior sessions can squat ports (an old `:5054` API caused “address already in use”; Vite silently bumps to `:5174/5175`). If something behaves oddly, check `lsof -nP -iTCP -sTCP:LISTEN | grep -E ':(5054|5173|8088)'` for duplicates and kill the strays.

## 4. How to test the impersonation feature

**Credentials (live API):**
| Email | Password | Role | Use for |
|---|---|---|---|
| `admin@docslot.io` | `admin` | super_admin | the feature (holds `platform.users.impersonate`) |
| `priyanka@apollocare.in` | `reception` | tenant_owner | the permission-gate / 403 path |

**UI flow:** hard-refresh http://localhost:5173 → log in as `admin@docslot.io` → **Topbar → “Impersonate”** → pick a target tenant → enter a reason (optional break-glass) → **“Start impersonation”** → warning **banner** appears at top of content → **“Exit impersonation”** restores your session.

- The “Impersonate” action only renders for a login holding `platform.users.impersonate` (admin yes, priyanka no).
- Oversight: `GET /api/v1/security/impersonation-sessions` (perm `platform.anomalies.review`) lists active/expired/ended sessions, actor masked to initials.

## 5. The mock-vs-live gotcha (important)

The SPA ships in **mock mode by default** (`lib/backend` seam; the login screen’s “Demo: priyanka…” hint is the mock fixture). In mock mode only `priyanka` logs in and there is no real backend. To exercise the real platform you must set **`VITE_USE_REAL_API=1`** (done here via `frontend/.env.local`) **and restart Vite** — env vars are read at startup, not via HMR. Screens not yet wired to the live API fall back to mock by design; that’s expected.

## 6. Feature architecture (the audited-by-construction chain)

1. **DB guard** — `platform.current_impersonated_tenant()` (`database/11_rbac_hardening.sql`) is `SECURITY DEFINER` and returns the GUC tenant **only** when a live, non-expired `impersonation_sessions` row exists for `app.user_id`. `05_security_hardening.sql` keeps a fail-closed bootstrap reader.
2. **Session GUCs** — `UnitOfWork.BeginTenantScopeAsync` sets `app.user_id` + `app.impersonated_tenant` per transaction (from validated JWT claims only; no header fallback).
3. **Lifecycle functions** — `begin_impersonation()` (writes the hash-chained `audit_log` row + creates the session; the **only** INSERT path) and `end_impersonation()` (symmetric audited close; idempotent + atomic). `docslot_app` has no INSERT on the table.
4. **Endpoints** — `POST /api/v1/auth/impersonation/begin` (`[RequirePermission("platform.users.impersonate")]`) and `/end` (`[Authorize]`). Actor bound to the authenticated principal; impersonating token minted **only after** the audit row; end re-mints a clean token. `ImpersonationRepository` translates SQLSTATE `42501` → `403`.
5. **Oversight** — `GET /api/v1/security/impersonation-sessions` + `list_impersonation_sessions()` (SECURITY DEFINER, metadata-only, actor masked).
6. **Frontend** — claim-driven `ImpersonationBanner` (in `AppShell`) + the Begin slide-over trigger (Topbar, permission-gated). Exit clears the token + react-query cache.

**Guarantee:** a bare `docslot_app` session that sets `app.impersonated_tenant` itself reaches **no** cross-tenant PHI and emits **no** audit — the GUC is inert without a `begin_impersonation()`-created session.

## 7. Loose ends / follow-ups

- **Commit the Begin-trigger UI** (uncommitted; see §2) via branch + PR.
- **Demo hint string** on the login screen still says `priyanka@apollocare.in / reception`; consider updating or noting it’s the mock hint.
- **Surface the oversight list in the Security console UI** (the endpoint exists; no screen yet).
- **Auditor INFO notes (non-blocking):** discourage PII in the impersonation `reason` field (input guidance/validation); both recorded in `.claude/agent-memory/security-compliance-auditor/backend-issue3-impersonation-wiring.md`.
- **Test-suite note:** the WhatsApp E2E flake was fixed via an idempotent confirm-retry; full suite was 75/75 across 3 consecutive runs.
- Harmless residue: a few expired impersonation sessions in the DB from manual smoke tests (they auto-expire; rows are append-only by design).

## 8. Quick verification commands

```bash
# all services healthy
for p in 5054 5173 8088; do curl -s -o /dev/null -w "$p %{http_code}\n" http://localhost:$p/$([ $p = 5173 ] && echo '' || echo health); done

# live login through the proxy (what the browser does)
curl -s -X POST http://localhost:5173/api/v1/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"admin@docslot.io","password":"admin"}'

# backend integration tests
dotnet test backend/mediq/mediq.sln
```
