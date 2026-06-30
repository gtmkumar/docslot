---
name: owner-rights-view-rls-bypass-iam-reads
description: Owner-rights views bypass RLS, so IAM admin reads through v_user_* are NOT tenant-bounded and leak cross-tenant via ?tenantId. Plain-table reads ARE bounded.
metadata:
  type: project
---

CRITICAL recurring pitfall: a Postgres VIEW with no `security_invoker=true` accesses its base relations as the VIEW OWNER. In this repo the migration/owner is a superuser (bundle applied via psql as the local superuser), so EVERY view bypasses RLS on its underlying tables. NO view in `database/` sets `security_invoker` (grep is empty). The dotnet architect's own memory relies on this: `v_user_permissions` is used for the login membership read precisely BECAUSE it bypasses RLS.

Consequence for IAM admin reads (`IamReadService.cs`):
- A read through `platform.v_user_effective_permissions` (built on `v_user_permissions` + `user_permission_overrides`) is NOT RLS-scoped. The view returns rows for ALL tenants (no `current_tenant_id()` self-scope). The ONLY tenant filter is the SQL WHERE clause.
- The handler passes `query.TenantId ?? ctx.TenantId`. `query.TenantId` is the client-controllable `?tenantId` query param. The GUC `app.tenant_id` (set by `TenantScopeQueryBehavior`/`UnitOfWork`) comes from the JWT claim and does NOT bound a view read. So `?tenantId=<otherTenant>` → reads another tenant's data. CROSS-TENANT LEAK.

Two IAM admin endpoints affected by this param-trust + view-bypass pattern:
- `GET /iam/users/{id}/effective-permissions` (Slice-13, NEW) — reads the view → LEAKS. VETOED in slice-13.
- `GET /iam/users/{id}/effective-access` (PRE-EXISTING, prior-passed) — uses `resolve_user_permissions` (SECURITY DEFINER, also bypasses RLS, filters only by the p_tenant_id param) → SAME leak class. Latent. Flag CERT-In if confirmed exploited.

What IS safe (contrast): a DIRECT table read by `docslot_app` (NOBYPASSRLS, not owner) IS RLS-bound. `GET /iam/users/{id}/overrides` reads `platform.user_permission_overrides` directly → `upo_read` policy `rls_can_see_tenant(tenant_id)` applies, using the server-trusted GUC (= JWT tenant). A `?tenantId=B` param can only NARROW within the RLS-permitted set, never widen. Safe.

Correct remediation pattern for any view-based or DEFINER-based admin read that takes a tenant arg: scope by the SERVER-TRUSTED context, not the client param. Either (a) ignore `?tenantId` and use `ctx.TenantId`, or (b) add `AND platform.rls_can_see_tenant(tenant_id)` to the WHERE (uses `current_tenant_id()`/super GUC, both server-signed) so a client param can never widen. Do NOT rely on "RLS scopes the view" — it does not.

See [[definer-sweep-pattern]], [[prior-audit-decisions]].
