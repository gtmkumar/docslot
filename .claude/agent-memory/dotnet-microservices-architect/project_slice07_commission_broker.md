---
name: slice07-commission-broker
description: DocSlot .NET slice 07 — broker (Care Partner) referral economy. Compliance-heavy: PCPNDT CHECKs, discount↔attribution exclusivity trigger, MCI 6.4, PAN encryption, TDS/GST payouts, payout approve≠execute RBAC.
metadata:
  type: project
---

Slice 07 (`commission`) implements the broker referral economy — the most compliance-heavy slice. Builds on [[slice03b-clinical-phi]] (field encryption, docslot_app role) + [[slice02-platform-api]] (webhooks).

## Compliance enforcement points (DB-enforced — surface as clean errors)
- **PCPNDT (criminal)**: `commission.brokers.can_refer_pndt` CHECK-forced false; `commission_rules.excludes_pndt` CHECK-forced true. Never write violating values (test proves the CHECK rejects). `PndtComplianceException` (defensive).
- **Discount↔attribution exclusivity**: DB trigger `trg_no_attribution_on_discounted` (`fn_block_attribution_on_discounted`, lives in 09_chat_identity.sql) raises SQLSTATE `23514` (check_violation) when a booking has `direct_discount_inr > 0`. `AttributionRepository.AddAsync` catches `PostgresException.SqlState=="23514"` → `AttributionOnDiscountedBookingException` → `DomainExceptionTranslationBehavior` → 422.
- **MCI 6.4**: tenant (business) pays the broker — doctors NEVER in money flow. Customer-facing label is **"Care Partner"** (NOT "Referral Partner") — in DTOs (`BrokerDto.CarePartnerLabel`) + event/audit text.
- **DPDP**: PAN encrypted at rest via slice-05 `IFieldEncryptionService` (registry data_class=tax_id, legal_obligation). Brokers see patients as first-name+masked-phone only. PAN NEVER in any DTO/log (BrokerDto has only `PanVerified` bool). Webhook events carry IDs+amounts ONLY.

## Canonical SQL edits (deliberate, re-validated — 115 tables)
- `10_roles_grants.sql`: GRANTed `docslot_app` USAGE + SELECT/INSERT/UPDATE on schema **commission** (was missing → app couldn't touch commission tables; the #1 blocker). + narrow DELETE on referral_clicks/broker_tenant_links, sequences, functions, default privileges.
- `07_commission_broker.sql`: **`brokers.pan_number` + `bank_account_last_4` changed VARCHAR(10)/(4) → TEXT** — the encrypted envelope (hundreds of chars) doesn't fit a 10-char column (the canonical "encrypted via app layer" comment conflicted with the narrow type). LIVE DB needed DROP VIEW v_ready_payouts → ALTER → recreate view (it depends on pan_number) + re-GRANT to docslot_app.
- `07_commission_broker.sql`: **grant super_admin ALL commission perms** — the original super_admin seed (01) granted only perms existing THEN; product perms added by later files (commission) were never granted to super_admin → super_admin lacked `commission.payouts.execute`. Added an explicit super_admin × commission_product cross-join grant.

## Payout RBAC — APPROVAL ≠ EXECUTION (CLAUDE.md critical)
Two DISTINCT seeded permission keys: `commission.payouts.approve` (scope=tenant; tenant_admin/owner get it) vs `commission.payouts.execute` (scope=platform; only super_admin). Two-step flow: `POST payouts/{id}/approve` (gated approve) → `POST payouts/{id}/execute` (gated execute, requires prior approval `Payout.CanExecute`). Test proves a tenant_admin can approve but gets 403 on execute; super_admin can do both.

## Attribution engine (AttributionEngine.cs)
3 mechanisms → verification status: referral_link/broker_portal_booking/qr_scan/manual_admin → 'auto_verified'; post_hoc_claim/whatsapp_template → 'pending' (patient confirms later). UNIQUE(booking_id, broker_id). Rule matching by `priority DESC`, first match wins (`CommissionRule.Matches` on tier/type/service/value); `CommissionCalculator` = flat/percentage + floor/ceiling + monthly-per-broker cap (vs `BrokerEarnedThisMonthAsync`) + first_booking_only. Fraud (`FraudScorer`): self_referral (broker phone == patient phone, +0.6), rapid_burst (≥10 in 5min, +0.4); >0.5 flagged.

## Payout math (PayoutCalculator — pure)
gross → TDS 5% (194H, deducted) → GST 18% (ADDED if broker gst_verified) → net. ₹100 minimum floor (`MeetsMinimum`). E.g. gross 1000 GST-registered → net 1130; not registered → net 950.

## Tenancy
Brokers are PLATFORM-level by phone (like patients) — `broker_tenant_links` mediates tenant access. Attributions/rules/payouts ARE tenant-scoped. Commission tables have NO RLS (tenant-scoped in queries; brokers cross-tenant by design).

## Gotchas
- Encrypted columns that are narrow VARCHAR in canonical SQL can't hold the envelope → widen to TEXT (same class of fix as 03b jsonb).
- super_admin perm coverage: later product files MUST explicitly grant super_admin their perms.
- Parallel-test flake: `Dashboard_Summary` (slice-03) occasionally fails under parallel runs (session-scoped app.tenant_id on pooled connections / timing); passes in isolation + on rerun. Benign; consider per-test connection isolation if it recurs.

## Auditor remediation (post-veto, platform-wide fixes)
- **BLOCKER 1 — tenant-GUC pool bleed (was cross-tenant PHI hazard).** `UnitOfWork.SetTenantScopeAsync` used session-scoped `set_config(...,is_local=false)` on a pinned pooled connection with NO reset → `app.tenant_id` from request A leaked to request B (Dashboard_Summary flake was the canary). FIX: `IUnitOfWork.BeginTenantScopeAsync` returns an `ITenantScope` (IAsyncDisposable) that opens a TRANSACTION + `SET LOCAL app.tenant_id` (is_local=true); read path (`TenantScopeQueryBehavior`) disposes→rollback (GUC clears); command path (`UnitOfWorkBehavior`) commits. Reuses an ambient command tx if present. **Consequence**: write-then-throw security records that MUST survive a command rollback now use a DEDICATED connection (`IDedicatedConnectionFactory`/`DedicatedConnectionFactory`): `AuditTrailWriter`, `LoginAttemptService`, `UserRepository.UpdateLoginStateAsync` (lockout), `SessionStore.RevokeAllForUserAsync` (refresh-reuse chain-revoke), `PurposeOfUseWriter` (clinical reads are queries → run in the rolled-back read tx). Proven by a 24-parallel two-tenant read test (no bleed) + 3× clean full-suite runs (flake gone).
- **BLOCKER 2 — broker self-service IDOR.** `me/wallet`/`me/links` took `[FromQuery] brokerId` with no ownership check. FIX: server-resolved `broker_id` JWT claim — `IBrokerIdentityResolver`/`BrokerIdentityResolver` looks up `commission.brokers.user_id == userId` at login/refresh/switch-tenant; `JwtTokenService.CreateAccessToken(user, tenant, brokerId?)` emits `broker_id` claim; `ICurrentUserContext.BrokerId` surfaces it; `me/*` endpoints use `RequireOwnBroker()` (claim or 403) — query param removed entirely. Proven: broker A only reaches A; non-broker → 403.
- **Corruption-preventer — widened encrypted columns to TEXT** (AES envelope ~250-300 chars overflows narrow VARCHAR): `docslot.patient_medical_history.title` VARCHAR(200)→TEXT (03b already encrypted into it — latent corruption), `docslot.patients.aadhaar_last_4` VARCHAR(4)→TEXT, `platform.users.mfa_secret` VARCHAR(255)→TEXT, `platform_api.webhook_subscriptions.secret_hash` VARCHAR(255)→TEXT. Canonical edits in 01/02/03; bundle revalidated; live DB aligned. (client_secret_hash stays VARCHAR(255) — bcrypt, not an envelope.)

## Verify
`dotnet build` 0 errors (2 transitive MessagePack warnings). `dotnet test` 38/38 (36 prior + pool-safety no-bleed + broker IDOR 403), PARALLEL, 3× clean. Bundle revalidated on fresh DB (115 tables). Chain intact (0 broken). App = docslot_app (non-super/non-bypassrls).
