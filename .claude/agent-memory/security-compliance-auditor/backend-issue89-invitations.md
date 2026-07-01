---
name: backend-issue89-invitations
description: Issue #89 platform.invitations (token-based tenant onboarding) — PASS-WITH-CHANGES 2026-07-01; over-broad docslot_app INSERT/UPDATE grant (HIGH) + unconsented existing-identity link (MEDIUM)
metadata:
  type: project
---

Issue #89 (epic #80 Phase C) — token-based tenant onboarding alongside direct-add CreateUser. NEW table `platform.invitations` (11_rbac_hardening.sql:1756; bundle docslot_complete.sql:9354, parity confirmed). Audited 2026-07-01. **VERDICT: PASS WITH CHANGES** (no blocker).

## Architecture (all verified sound)
- Table: tenant_id NOT NULL FK→tenants ON DELETE CASCADE; RLS ENABLED; read=rls_can_see_tenant, write=rls_can_write_tenant (11:1787-1794). token_hash TEXT (SHA-256 hex), plaintext NEVER stored. status CHECK(pending/accepted/revoked/expired). Partial unique idx uq_invitations_pending_email = one live pending per (tenant,email) → 23505→409.
- 4 SECURITY DEFINER fns: create/resend/revoke re-check user_has_permission('tenant.users.create',tenant) + mint-time R3 no-escalation guard (identical to assign_role_to_user: platform-scope blocked, must hold tenant.roles.assign + EVERY perm the role confers). accept_invitation(token_hash,pwd_hash,name) UNAUTHENTICATED (token IS authz), definer bypasses RLS, derives tenant+role from the token row.
- Token: InvitationTokenFactory = 256-bit RandomNumberGenerator CSPRNG, base64url, SHA-256 hex hash. Plaintext returned ONCE (create/resend); commands marked IDoNotCacheResponse (no idempotency-cache leak). Lookup by hash. Garbage/expired/revoked/used → ONE generic no_data_found→422 (no enumeration).
- Expiry+single-use: accept SELECTs status='pending' AND expires_at>NOW(), flips to accepted; opportunistic age-out scoped to the token_hash. Revoke/resend invalidate prior token (hash rotated on resend).
- Gates: create/resend/revoke=tenant.users.create, list=tenant.users.read, accept=[AllowAnonymous]. Tenant bound from SIGNED JWT: PermissionResolutionMiddleware resolves against currentUser.TenantId (not route); ResolveTenant() rejects route≠JWT tenant (403); DB fn re-checks perm in p_tenant_id. Triple defense — cross-tenant create/resend/revoke blocked. List has explicit tenant_id predicate + RLS.
- Accept audit: NULL actor (unauthenticated) + dedicated audit conn (can't FK the still-uncommitted provisioned user). Correct.
- InvitationTests genuine: List_IsTenantIsolated seeds a REAL second tenant's pending invite (OtherTenantId) and asserts it's hidden. R3 test: limited inviter (tenant.users.create only) invite WITH role→403 nothing persisted, WITHOUT role→201.

## Findings
- **F1 HIGH (REQUIRED CHANGE):** `GRANT SELECT, INSERT, UPDATE ON platform.invitations TO docslot_app` (11:1797, bundle 9395) is OVER-BROAD. All writes go through SECURITY DEFINER fns that run as the table OWNER — docslot_app needs only SELECT (for ListAsync) + EXECUTE on the fns. The superfluous INSERT/UPDATE is a latent R3 bypass: a raw write as docslot_app (future code path / SQLi elsewhere) could INSERT an invite with an arbitrary role_id bounded only by rls_can_write_tenant (own-tenant, does NOT enforce R3) then accept→confer an un-conferrable role. IDENTICAL to the condition I issued+verified on the catalog plane ([[rbac-catalog-plane]]). Fix: `REVOKE INSERT, UPDATE ON platform.invitations FROM docslot_app;` keep SELECT + EXECUTE. Not a live blocker (no current raw-write path) → PASS-WITH-CHANGES not VETO. VERIFY applied in both 11_ and bundle.
- **F2 MEDIUM (owner decision):** accept's existing-identity branch LINKS any existing global user (matched by invited_email) into the inviting tenant with the pre-vetted role — no consent/notification, no proof the redeemer owns the mailbox. A tenant admin can mint an invite for a known email and self-redeem the returned token to unilaterally add ANY existing user to their tenant. Password/profile NOT overwritten (no takeover — good) and role is R3-bounded to actor's tenant (not a platform escalation of the target), but it IS a new unconsented-membership capability (direct-add would 409 on the unique email). DPDP: adds a data principal to a tenant's processing scope without consent. Recommend auth-as-existing-user or a notify/consent step.
- **F3 LOW:** accept lacks SELECT ... FOR UPDATE → concurrent double-accept both read pending; 2nd may hit an uncaught users unique-violation (23505→500; AcceptAsync catches only P0002) or re-run the idempotent role ON CONFLICT. Single-use practically holds. Add row lock or catch 23505.
- **F4 LOW:** role not re-validated at accept (7-day window). Soft-deleted role between mint+accept still INSERTs a dangling user_tenant_roles row (FK to soft-deleted row survives); resolve_user_permissions filters deleted roles so no effective grant. Add `AND deleted_at IS NULL` recheck at accept.
- **F5 INFO:** InvitationAbstractions doc says token hash is "constant-time compared on accept" — actually a plain DB index equality. Immaterial for a 256-bit-random hash (no timing-recovery surface); fix comment.

See [[backend-iam-roles-admin]], [[user-management-lifecycle]], [[rbac-catalog-plane]], [[doc-rbac-md-v2-audit]].
