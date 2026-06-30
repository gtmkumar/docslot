---
name: service-token-phi-wall
description: The token_use=service identity (worker→AI auth) and the allow-by-default PHI-wall anti-pattern on the AI service that lets triage leak PHI.
metadata:
  type: project
---

Slice-16 introduced a NON-HUMAN service identity: `JwtTokenService.CreateServiceToken` mints a short-TTL (clamp 1-15min, default 5) JWT with sub=fixed subject ("svc:no-show-predictor"), `token_use=service`, tenant_id, jti — NO scope/role/email — on the SAME HS256 key/iss/aud as user+client tokens (no new external credential). The no-show backfill worker presents it to the Python AI sibling because a background worker has no live caller JWT.

**The PHI wall is ALLOW-BY-DEFAULT (deny-per-endpoint), which is fragile.** A service token is refused only where `enforce_phi_gate(token_use=...)` is explicitly called (it raises 403 FIRST, before the consent/break-glass check, with detail "A service identity may not access patient data."). Wired in `ai_service/app/routers/rag.py` (index+ask) and `extractions.py` (lab-report). `predictions/no-show` is intentionally OPEN (non-PHI booking features).

**KNOWN GAP (Slice-16 condition #1, HIGH, merge-blocking):** `ai_service/app/routers/triage.py` was NOT updated — `GET /ai/v1/triage/runs/{id}` returns `inputData.complaint` (stored free-text symptom PHI) and only checks get_principal, so a service token (valid tenant) reads it. POST /triage + GET /triage/runs + GET /extractions (list) + GET /rag/status are likewise ungated. The robust fix is to INVERT to a global default-deny for token_use=service with an explicit non-PHI allow-list (no-show), so future PHI endpoints can't silently become reachable. `run_triage` itself does NOT read stored patient PHI (works over the caller-supplied complaint) — the exposure is the run-HISTORY read endpoints.

**.NET-side replay is inert:** sub is a non-UUID string → `CurrentUserContext.UserId` is null → `PermissionResolutionMiddleware` skips resolution (guarded `UserId:{}` pattern, no 500) and `ScopeResolutionMiddleware` only resolves token_use=client → no permissions/scopes → every [RequirePermission]/[RequireScope] → 403.

**Audit attribution is BROKEN for service tokens** (Slice-16 condition #2, MEDIUM): `repository.write_audit_best_effort` writes `platform.audit_log.user_id` = the string subject, but that column is `UUID REFERENCES platform.users` (01_platform_core.sql:518) → insert fails → swallowed → worker scoring is unaudited despite comments claiming attribution. Does NOT break the hash chain. See [[prior-audit-decisions]] and [[definer-sweep-pattern]] (the 2 new cross-tenant SECDEF fns list_due_noshow_bookings/mark_noshow_predicted follow the established sweep hygiene; PUBLIC retains EXECUTE as with all siblings).
