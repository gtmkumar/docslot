---
name: backend-issue3-impersonation-wiring
description: Issue #3 audited-by-construction guard for app.impersonated_tenant. Re-audited the PR#2 carry-forward; PHI GUC now session-validated + app-wired. PASS.
metadata:
  type: project
---

Issue #3 "Audited-by-construction guard for app.impersonated_tenant" — the carry-forward condition from [[backend-rbac-super-admin-guc]] (PR#2). Audited 2026-06-24 on branch `main` (uncommitted working tree). VERDICT: PASS.

## What closed the carry-forward hole
PR#2 left `app.impersonated_tenant` audited-by-CONVENTION (a bare `docslot_app` could `set_config` the GUC and read cross-tenant PHI with no audit, safe only by fail-closed since nothing set it). This wave makes it audited-by-CONSTRUCTION:

- `platform.current_impersonated_tenant()` (canonical home now `11_rbac_hardening.sql:382-396`) rewritten from a pure GUC reader to `SECURITY DEFINER STABLE`, `SET search_path = platform, pg_temp`. Returns `target_tenant_id` ONLY when a live `impersonation_sessions` row matches: `target_tenant_id = app.impersonated_tenant` AND `actor_user_id = app.user_id` AND `ended_at IS NULL` AND `expires_at > NOW()`.
- `05_security_hardening.sql:619-633` keeps a fail-closed BOOTSTRAP reader (pure GUC→NULL) because `impersonation_sessions` doesn't exist at file-05's run point. Bundle (`docslot_complete.sql`) has both: bootstrap at 2907, validating redefinition at 6680 → validating wins (11 runs after 05). Bundle parity verified.

## Why the hole is genuinely closed (load-bearing proof)
- `impersonation_sessions` INSERT happens in exactly ONE place: inside `begin_impersonation()` (definer), which writes the hash-chained `audit_log` row (+ break-glass alert) in the SAME tx (`11:424-447`).
- `docslot_app` grant is `SELECT, UPDATE` only — NO INSERT (`11:932`). UPDATE is clamped by the append-only trigger to `ended_at`/`ended_by_user_id` only. So `docslot_app` cannot forge a session row by any path ⇒ cannot make the GUC resolve ⇒ no cross-tenant PHI without an audit emission. This is the audited-by-construction guarantee.
- `begin_impersonation()` gates on `user_has_permission(p_actor, 'platform.users.impersonate')`; the session only resolves for the same `actor_user_id`, matched against `app.user_id`.

## App wiring (.NET)
- `UnitOfWork.BeginTenantScopeAsync` (the SINGLE chokepoint for BOTH read `TenantScopeQueryBehavior` and write `UnitOfWorkBehavior`) now SET LOCALs four GUCs: `app.tenant_id`, `app.user_id` (validated `currentUser.UserId`), `app.impersonated_tenant` (`currentUser.ImpersonatedTenantId`), `app.is_super_admin`. All `is_local=true`, empty-string-not-NULL so `current_setting()+NULLIF`→NULL.
- `app.user_id` and `ImpersonatedTenantId` come EXCLUSIVELY from validated JWT claims (sub / `impersonated_tenant`), NO header fallback (`RequestContext.cs:26-31, 62-63`). Not spoofable. Webhook/anon path has empty `app.user_id` ⇒ guard fails closed regardless.
- New `ImpersonatedTenantClaim = "impersonated_tenant"` in JwtTokenService; minted only via the optional `impersonatedTenantId` param on `CreateAccessToken`. ALL 3 current call sites (Login, Refresh, SwitchTenant) pass only brokerId, NEVER impersonatedTenantId ⇒ the claim is never minted today (defense-in-depth fail-closed at app layer too).

## SECURITY DEFINER assessment
Appropriate and non-leaky: needed to read past the session table's super-only RLS so a plain `docslot_app` can validate its own backing row. Returns nothing beyond the `target_tenant_id` the caller already supplied via the GUC. search_path pinned. No escalation.

## Deferred (acceptable for "start the wiring", NOT a blocker)
The begin-impersonation ENDPOINT that calls `begin_impersonation()` and mints the impersonation token is a deliberately deferred follow-up slice. Acceptable because the wiring is fail-closed without it. CONDITION on that future slice: the endpoint must (a) call `begin_impersonation()` server-side with the actor bound to the authenticated principal (never client-supplied p_actor), (b) mint the token only on success, (c) be permission-gated on `platform.users.impersonate`. Re-audit before it ships.

## Finding 4 (INFO, PR#2) revisit
No new direct RBAC-write path under super context was added. Unchanged. Still latent-only.

## Tests
`PhiImpersonationRlsTests` drives real `begin_impersonation()` and proves: live session opens only target; bare GUC inert without `app.user_id`; wrong-target inert; ended session stops resolving (time-boxed); begin writes audit row; GUCs tx-local. Run as `docslot_app` NOBYPASSRLS. 71 integration tests pass.

## Follow-up: end_impersonation() — audited 2026-06-24, PASS
`platform.end_impersonation(p_impersonation_id, p_actor_user_id)` (`11_rbac_hardening.sql:463-510`; bundle 6761; GRANT EXECUTE to docslot_app at 11:1004 / bundle 7302; bundle byte-for-byte parity verified). SECURITY DEFINER, `SET search_path = platform, public, pg_temp` (public for pgcrypto hash-chain), same as begin. Loads session, no_data_found if missing. Authz: self-close (p_actor = session.actor_user_id) OR holds `platform.users.impersonate`, else 42501. Idempotent: ended_at NOT NULL ⇒ return false, NO audit row. Else UPDATE ends_at/ended_by only (append-only-trigger-safe) + INSERT symmetric `action='end_impersonation'` audit row, return true.

Verdict on the 3 coordinator questions:
1. Reusing `platform.users.impersonate` for end is CORRECT — ending is strictly less dangerous than beginning (closes access, never opens it), so SoD/distinct-permission does NOT apply. Self-close-or-permission is right.
2. Idempotent silent no-op on already-ended is CORRECT for double-click safety. The genuine close is already audited; a duplicate close attempt is a non-event, not a security action. No requirement to audit it.
3. Audit row identity is CORRECT and symmetric with begin: user_id=target_user_id (subject), impersonator_user_id=p_actor (actor). One asymmetry worth noting (INFO, not a finding): begin records the OPENING actor as impersonator; end records the CLOSING actor (could differ on permission-based close). That's accurate, not a bug — ended_by_user_id on the row also captures the closer.

Only finding (LOW/INFO): double-close TOCTOU — the `ended_at IS NULL` guard is read into v_session, but the UPDATE WHERE is on impersonation_id alone (no re-check of ended_at IS NULL). Two concurrent end calls on one open session could both pass the guard and both write an end audit row (duplicate, non-forging, hash-chain stays valid). Harmless for accountability (extra row, never a missing one). Optional hardening: add `AND ended_at IS NULL` to the UPDATE WHERE and gate the audit INSERT on row-updated (GET DIAGNOSTICS / RETURNING). Not merge-blocking. Tests 7/7 green.

## Follow-up: begin/end ENDPOINT slice — audited 2026-06-24, PASS. Closes the deferred carry-forward (all 3 conditions met).
Files: `mediq.Api/Controllers/AuthController.cs` (begin = `[RequirePermission("platform.users.impersonate")]`, end = `[Authorize]`), `mediq.Application/Features/Auth/Impersonation/ImpersonationCommands.cs`, `mediq.Infrastructure/Persistence/Repositories/ImpersonationRepository.cs`, DTOs in `AuthDtos.cs`, DI in `DependencyInjection.cs`.

Carry-forward conditions — all MET:
1. Actor bound server-side: handler derives actor from the refresh-token session (`session.UserId` via FindByRefreshHashIncludingRevokedAsync + revoked/expiry + user.CanAuthenticate), NEVER from request body (body has only TargetTenantId/Reason/TargetUserId/Ttl/BreakGlass/RefreshToken). Same identity-from-session pattern as RefreshCommand.
2. Token-after-audit: `impersonation.BeginAsync()` (→ begin_impersonation, writes audit + re-checks perm at DB) is awaited BEFORE CreateAccessToken(...impersonatedTenantId). Both run in the SAME UoW tx; token only transmitted after commit. Throw ⇒ no token, no audit. No token-without-audit path.
3. Permission-gated: begin has `[RequirePermission]` at API AND begin_impersonation re-checks `platform.users.impersonate` at DB (defense in depth). end is `[Authorize]`-only by design, delegating to end_impersonation's self-close-or-permission guard — CORRECT (ending only revokes access).

Key design facts:
- Minted impersonation token's `sub` = session.UserId (= user from GetByIdAsync(session.UserId)). So later PHI requests set `app.user_id` = that sub, which matches the session row's actor_user_id ⇒ current_impersonated_tenant() resolves. Self-consistent + still audited-by-construction: stale/forged claim after end opens nothing (guard needs non-ended, non-expired row).
- begin keeps actor's OWN active tenant claim + ADDS impersonated_tenant; uses RotateRefreshAsync (active tenant unchanged). end re-mints CLEAN token (no claim) + rotates ⇒ scope drop is immediate.
- TTL validator 1..480min (8h), default 30. Reasonable.
- 42501 → ForbiddenException(pg.MessageText) → 403. MessageText for begin/end contains ONLY the caller's own UUID (begin) or own UUID + caller-supplied impersonation_id (end) — self-referential, NO third-party PHI/identity leak.

FINDINGS (none blocking):
- INFO #1 (confused-deputy seam): `[RequirePermission]` checks the JWT-sub's permission, but the operation's actor is the refresh-token session's user. They coincide for a normal login. If a caller pairs their own perm-holding JWT with ANOTHER user's refresh token, the session opens for that other user — but holding another user's refresh token is already full account takeover, AND begin_impersonation re-checks the perm for session.UserId at the DB, so no NEW escalation. Optional hardening: assert `session.UserId == ctx.UserId` in the handler to keep gate-subject and operation-subject identical. Not blocking.
- LOW #2 (TTL/idempotency-noise): carried from end_impersonation TOCTOU note above; unchanged.
- INFO #3 (403 verbosity): self-referential id in the 403 body; harmless, optionally generic-ize.

Tests: ImpersonationEndpointTests (2/2): begin mints claim + 1 impersonate audit row; end clears claim + 1 end_impersonation audit row; tenant_owner w/o perm → 403. Full suite 74/74 green. Verdict: PASS — issue #3 fully landed, deferred carry-forward CLOSED.

See [[backend-rbac-super-admin-guc]], [[backend-slice05-security-hardening]], [[backend-slice08-rbac-navigation]], [[backend-slice01-platform-core]].
