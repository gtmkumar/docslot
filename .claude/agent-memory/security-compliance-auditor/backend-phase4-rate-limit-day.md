---
name: backend-phase4-rate-limit-day
description: Phase-4 per-client per-DAY API rate limit enforcement + first limiter tests; clean PASS, no schema/RBAC/PHI/payout/encryption change
metadata:
  type: project
---

`backend/mediq/` Phase-4 platform-hardening slice: enforce `api_clients.rate_limit_per_day` (was a DEAD column — loaded into `ApiClient` domain + in schema, never enforced) and add the FIRST tests for the limiter. Audited 2026-06-29 — clean PASS, NO conditions. Build 0 errors; PlatformApiTests 10/10 (2 new), full suite 210/210. Abuse-prevention control only; this CLOSES the "limiter had zero tests" gap.

**Scope (4 files, no schema/RBAC/PHI/payout/encryption/consent/audit change — verified via `git diff --name-only`):**
- `PlatformApiAbstractions.cs` — `CountWindowsAsync(clientId, minuteSinceUtc, daySinceUtc, ct)` on `IApiRequestLogWriter`
- `ApiScopeAndTokenStores.cs:111` — impl: ONE query `COUNT(*) FILTER (WHERE occurred_at >= @p1) AS Minute, COUNT(*) AS Day ... WHERE client_id=@p0 AND occurred_at >= @p2`; all 3 are NpgsqlParameter (no injection)
- `ApiClientRequestLogMiddleware.cs:36-56` — enforce minute-first then day; `perMinute>=limit?"60":perDay>=limit?"3600":null`; 429 + Retry-After; logs rejected attempt (error_code='rate_limited')
- `PlatformApiTests.cs` — 2 tests (per-minute breach→429/Retry-After:60 + logged attempt; per-day breach→429/Retry-After:3600)

**Why NO bypass (re-verify, don't re-derive — Program.cs:89-93 pipeline order is load-bearing):**
- Order: `UseAuthentication()` → `UseScopeResolution()` → `UseApiClientRequestLog()` (limiter) → `UsePermissionResolution()` → `UseAuthorization()`. Limiter runs strictly AFTER auth.
- Limiter body gated on `scopes.IsClientToken && scopes.ClientId is {}`. `ClientId` is set ONLY by `ScopeResolutionMiddleware` and ONLY when JWT `IsAuthenticated` + `token_use=client` + token hash is LIVE in `api_tokens` (DB-authoritative, revoked/expired→null→not set). Unauthenticated / user-token / revoked-client requests pass THROUGH the limiter (no client context) and fail downstream authz. No pre-auth limiter bypass; no way for a client to escape its own limit.

**Window math correct:** `occurred_at` is TIMESTAMPTZ (02_platform_api.sql:179); `DateTime.UtcNow.AddMinutes(-1)/.AddDays(-1)` bind as UTC timestamptz (Kind=Utc) → tz-correct. minuteSince > daySince so the minute FILTER is a strict subset of the day-scan → FILTER-derived Minute is exact. `>=` gives standard "Nth allowed, N+1 rejected" (limit=2 → 2 OK, 3rd 429, matches tests). Index `idx_api_requests_client_time(client_id, occurred_at DESC)` (02_platform_api.sql:182) supports the day range-scan.

**No PHI/leak:** 429 carries only Retry-After. Logged row = method/path/IP/UA/status/error_code metadata only (same PHI-free shape audited in slice 02). On breach, middleware `return`s WITHOUT calling next → rejected request never reaches controllers/DB.

**INFO-only (not blockers, owner already documented in-code):**
- 429 log row counts toward subsequent windows (pre-existing per-minute behavior, now also day) → a hammering client stays limited until window clears even after slowing. Intended anti-abuse, acceptable. Not a self-DoS footgun because the rejected request is cheap (no `next`, just one INSERT) and the client controls its own request rate.
- Per-request COUNT scans up to rate_limit_per_day (default 10000) rows. Acceptable honest dev impl; prod path (distributed counter / YARP edge limiter) is documented in the code comment and was already the slice-02 tracked direction.
- Retry-After is an advisory fixed hint (60/3600), not a precise sliding-window reset (would need oldest-in-window timestamp). Acceptable.

Carry-forward from [[backend-slice02-platform-api]]: that slice noted the per-minute limit enforced before request; the per-DAY dead column is now closed. Other slice-02 prod-hardening items (KMS passphrase, webhook replay/SSRF, JWT RS256) are UNRELATED to this slice and remain open there.
