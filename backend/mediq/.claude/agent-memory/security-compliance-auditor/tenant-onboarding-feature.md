---
name: tenant-onboarding-feature
description: POST /api/v1/tenants onboarding — creates tenant + mints tenant_owner invite in one txn; token hygiene, status='active' birth, no-RLS tenants table.
metadata:
  type: project
---

Tenant onboarding (POST /api/v1/tenants, `CreateTenantCommand`, gated `platform.tenants.create`). Audited PASS (2026-07-16).

**Shape / invariants confirmed:**
- Role is hardcoded server-side to system `tenant_owner` (looked up, not from request) — request DTO `CreateTenantRequest` has NO role field, so the endpoint cannot be steered into minting a platform-scoped grant. `tenant_owner` is scope='tenant', is_system=true, and its perms are only scope IN ('tenant','self') (01_platform_core.sql:322) → cannot escalate.
- `platform.tenants.create` = scope 'platform', **is_dangerous=false** (01_platform_core.sql:214). Only super_admin holds it (super_admin=ALL perms; platform_support=read+impersonate; tenant_owner/tenant_admin=tenant/self only). Platform scope is the real gate, not the danger flag.
- `create_invitation` (11_rbac_hardening.sql:1822, SECURITY DEFINER) re-checks actor: super_admin passes, else needs tenant.users.create in the NEW tenant + R3 no-escalation on the pre-attached role. Consequence: a NON-super-admin holding platform.tenants.create would insert the tenant then ALWAYS fail create_invitation (nobody has perms in a brand-new tenant) → whole txn rolls back. Effectively super_admin-only.
- Tenant INSERT + create_invitation share ONE UoW transaction (InvitationRepository runs on DbContext conn to enlist) → atomic; the invitation FK to the still-uncommitted tenant resolves because same txn.

**platform.tenants is the sanctioned NO-RLS table** — it IS the tenant dimension, can't be tenant-scoped. Direct parameterised INSERT in TenantRepository.CreateAsync gated by the permission; 23505 → ConflictException (409). This is the tenant+RLS-gate exception.

**Audit (dedicated-connection writer, commits independently to survive rollback):**
- audit_log.tenant_id is FK→tenants; audit_log.resource_id is NOT an FK. Onboarding passes `TenantId: null` for both the tenant-create and invitation-create audit rows because the dedicated conn can't see the uncommitted tenant (FK would violate); the new tenant is identified via resource_id + change_summary instead. Justification is sound.
- Ordering quirk: handler does tenant-INSERT → audit(tenant) → create_invitation → audit(invitation). The audit(tenant) commits BEFORE create_invitation; if a later step fails, an orphan "create tenant" audit row survives for a rolled-back tenant. Over-records (safe direction), chain intact. Cleaner = both writes then both audits. LOW/INFO.

**status='active' at birth** (TenantRepository.CreateAsync:97) overrides schema DEFAULT 'pending'. tenants.status CHECK IN (pending,onboarding,trial,active,suspended,cancelled,archived) + trial_ends_at exist → intended lifecycle is pending→trial→active. Onboarding skips it (rationale: owner can sign in immediately on accept). No subscription row created. Not a security-gate violation but flag against billing expectations. See [[prior-audit-decisions]].

**Token hygiene (one-time plaintext owner invite token in CreateTenantResult):**
- `CreateTenantCommand : IDoNotCacheResponse` → IdempotencyBehavior bypasses entirely (Behaviors.cs:138, no serve/no save) so the token never hits the plaintext idempotency store. Note this SHORT-CIRCUITS before the key check, so the SPA's Idempotency-Key on POST /tenants is a no-op; double-submit dedup relies on the UNIQUE tenant_code (2nd → 409).
- Token never logged: onboarding handler takes no notifier/logger dep. Token 256-bit CSPRNG, only SHA-256 hash stored (InvitationTokenFactory).
- SPA: `tenantCreated` panel is in SlideOverHost TRANSIENT_SET → never URL-synced; token lives only in the in-store panel payload. By design the token DOES appear in the /accept-invite?token= capability link (readonly input + copy button, admin shares out-of-band). Accepted trade-off; mitigated by single-use + 7-day TTL + hash-at-rest + generic 422 (no enumeration). Hardening rec: history.replaceState scrub on the accept page.
- accept_invitation is unauthenticated (token IS the authorization); garbage/expired/revoked/used all raise one no_data_found → generic 422.
