---
name: slice89-invitations
description: Token-based invitations subsystem (issue #89) — schema in 11_rbac_hardening.sql, SECURITY DEFINER pattern, and three PostgreSQL gotchas that cost real debugging time.
metadata:
  type: project
---

Issue #89 (epic #80 Phase C) added `platform.invitations` — a token-based tenant-onboarding flow that sits ALONGSIDE the direct-add invite (Admin CreateUser). Table + 4 SECURITY DEFINER functions (create/resend/revoke/accept) live at the END of `database/11_rbac_hardening.sql`, before the POST-CONDITIONS `$verify$` block. Only a SHA-256 hash of the token is stored; plaintext returned once. Accept is unauthenticated (the token IS the authorization) so it MUST be a definer fn (RLS would otherwise hide/block the row). Backend: `InvitationsController`, `Features/Invitations/InvitationFeatures.cs`, `IInvitationRepository`/`IInvitationTokenFactory`.

**Why:** three non-obvious PostgreSQL/EF failures surfaced, each masquerading as a generic 500:
- **text→citext function overload resolution FAILS.** A driver-sent string param is `text`; PostgreSQL will not implicitly coerce it to a `citext` function parameter during overload resolution → `function ... does not exist` (even with a single overload). Fix: declare definer-fn params as `TEXT` (the column can stay `CITEXT`; the INSERT assignment-casts). Applies to any new definer fn taking a citext-backed value.
- **OUT param names collide with column refs → SQLSTATE 42702 (ambiguous column).** A `RETURNS TABLE(user_id …, tenant_id …)` fn whose body has `INSERT … ON CONFLICT (user_id, tenant_id, role_id)` throws "column reference user_id is ambiguous". Prefix OUT columns (`out_user_id`, …) and select them by that name from the caller. (Only triggered on the code path that actually references the colliding column — a NULL-role accept passed, a with-role accept failed.)
- **Audit of a just-provisioned user must use a NULL actor.** `IAuditTrailWriter` writes on a DEDICATED connection that commits independently, so it cannot reference a user row created inside the still-uncommitted request transaction → `audit_log.user_id` FK violation. For accept, audit with `UserId: null` and put the new user id in the resource fields.

**How to apply:** reuse these when writing ANY new `platform.*` SECURITY DEFINER function or auditing newly-created users. See also [[slice01-platform-core]] (inet converter gotcha, resolve-once RBAC) and the append-only audit_log teardown rule [[audit-log-append-only-teardown]] — accepted test users LOG IN (appending a login audit row), so they must be SOFT-deleted in teardown, never hard-deleted.
