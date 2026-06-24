---
name: backend-rbac-super-admin-guc
description: PR#2 feat/rbac-super-admin-guc — wires app.is_super_admin GUC. Re-reviewed after fix 10b1554; PHI bypass closed, cleared-by-fail-closed. APPROVE (conditional on deferred wiring).
metadata:
  type: project
---

PR #2 `feat/rbac-super-admin-guc` (orig commit a1e17f8, fix 10b1554, MERGED to main). Wires `app.is_super_admin` GUC in `UnitOfWork.BeginTenantScopeAsync` (set_config, is_local=true), resolved server-side via `platform.is_super_admin(currentUser.UserId)` — never from JWT.

**Spoof/pool/null: SAFE** (unchanged from first review; see git history). JWT-only UserId, both read+write paths set both GUCs at tx start, SET LOCAL clears on tx end, NULLIF guards fail to false/NULL.

## Re-review of fix 10b1554 (2026-06-24) — all 3 conditions

**Condition 1 (Finding 1, HIGH) — CLEARED.** The 5 PHI policies in `05_security_hardening.sql:645-687` now `OR tenant_id = platform.current_impersonated_tenant()` and NO LONGER reference `current_is_super_admin()`. Verified grep: only refs to super_admin in 05 are the func def (617) + a comment (640), zero in policies. drug_alerts EXISTS subquery (678-687) correctly aliases `p.tenant_id` for both active+impersonated. New accessor `current_impersonated_tenant()` canonical home moved to 05:626 (pure GUC reader on `app.impersonated_tenant`, NULLIF→NULL when unset). Fail-closed: no session ⇒ NULL ⇒ NULL=tenant_id never true. RBAC tables in 11 STILL honor super_admin via `rls_can_see/write_tenant` (11:793-814) — intended.

**Condition 2 (Finding 2, HIGH) — CLEARED-BY-FAIL-CLOSED, with a requirement on deferred wiring.** `begin_impersonation()` (11:376-428) writes a hash-chained audit_log row (impersonator_user_id + purpose + legal_basis) + break-glass alert. BUT it does NOT itself `set_config('app.impersonated_tenant',...)`. CRITICAL: nothing in DB couples the GUC to the audit — it is **audited by CONVENTION, not by construction**. A `docslot_app` session could `set_config('app.impersonated_tenant', <any tenant>)` directly and read cross-tenant PHI with NO audit row. Safe TODAY only because the app-side impersonation wiring is deferred (NOT in this PR) — grep confirms NOTHING anywhere calls set_config on app.impersonated_tenant, so the GUC is never set ⇒ fail-closed ⇒ no cross-tenant PHI reachable at all. **CARRY-FORWARD CONDITION for the deferred impersonation-wiring wave:** the app must set `app.impersonated_tenant` ONLY via a path that has first called `begin_impersonation()` in the same tx (audited-by-construction), and the GUC value must be tied to an active non-expired session. Re-audit that wave before it ships.

**Condition 3 (Finding 3, MEDIUM) — CLEARED.** `PhiImpersonationRlsTests.cs` (DB-level, runs as `docslot_app` = NOBYPASSRLS per 10_roles_grants.sql:31). Proves: (1) super_admin flag alone sees only home tenant A, not B/C; (2) impersonated_tenant=B opens A+B not C (scoped); (3) GUCs tx-local, same pooled connection, no bleed. RbacSuperAdminGucTests (RBAC half) untouched and target `platform.roles` (11 RBAC policy) → no regression.

**Finding 4 (INFO) — documented** at 11:805-808: super_admin satisfies rls_can_write_tenant for any tenant; safe because sanctioned RBAC writes route through R3 SECURITY DEFINER funcs. Latent footgun if a direct app-write path is ever added under super context.

**Bundle parity: OK.** `docslot_complete.sql` PHI policies (2923-2962) match source 05. Build order safe: 05 defines current_impersonated_tenant before its policies; 11 keeps idempotent CREATE OR REPLACE.

**Updated verdict: PASS (was APPROVE-WITH-CONDITIONS).** All 3 conditions cleared. One carry-forward condition on the deferred impersonation-wiring wave (audited-by-construction). No new blockers.

See [[backend-slice03b-clinical-phi]], [[backend-slice08-rbac-navigation]], [[backend-slice05-security-hardening]].
