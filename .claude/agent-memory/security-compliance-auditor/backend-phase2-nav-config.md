---
name: backend-phase2-nav-config
description: Phase2 nav-config slice — one partner_portal menu row gating /portal on commission.broker.read_self; clean PASS, no findings, IDOR-safe by construction
metadata:
  type: project
---

Branch `feat/phase2-nav-config` (uncommitted working tree on PR#20 merge base). Audited 2026-06-29 — **clean PASS, zero findings**. Surfaces the already-built Care Partner self-service portal (`/portal`, PR#20 React screen) in REAL-mode backend-driven nav. Purely additive: one `platform.navigation_menus` seed row + its `menu_permissions` gate, stale-comment fixes, one regression test. No new tables/permissions/PHI/payout/encryption.

**The menu row (live-verified, source+bundle parity confirmed):** `menu_key='partner_portal', menu_url='/portal', icon='wallet', display_order=85 (clean between care_partners=80, team=90), applies_to_tenant_types=NULL, parent_menu_id=NULL, badge_source=NULL, is_section_header=false`. Label/route/icon ONLY — carries no data. Bilingual present (`मेरा पोर्टल`). Gated on `commission.broker.read_self` (NOT the tenant-wide `commission.broker.read` that gates admin Care Partners screen).

**Why "admin also sees My Portal" is BENIGN, not a finding (the central question):** `read_self` is held by `broker`, `tenant_owner`, `super_admin` (+ QA/smoke custom roles), so admins see the label. But the menu row is just a label+route — ALL data flows through the existing `read_self`-gated portal endpoints in `CommissionController.cs` (`me/wallet` :60, `me/links` :72, `me/bookings` :83). Each resolves broker via `RequireOwnBroker()` (:231) = `currentUser.BrokerId` = the **server-signed `broker_id` JWT claim** (set at login `LoginCommandHandler.cs:66`, minted `JwtTokenService.cs:49` "the only trusted broker identity", read from validated token `RequestContext.cs:54`). NEVER a path/query/body param → IDOR-safe. An admin with no broker record has `BrokerId=null` → `RequireOwnBroker()` throws ForbiddenException (403). So admin clicking "My Portal" gets a 403 on data calls — sees NOTHING, not even an empty portal. No path reaches another broker's wallet/PII.

**Gate enforcement:** `get_user_menus` (08:298) filters menus against `resolve_user_permissions()` (deny-wins resolver, per slice-08). Menu with gating perms surfaces only if user holds one; deny-override on read_self → menu disappears automatically. No new bypass.

**Idempotency:** Live DB has exactly 1 row / 1 gate (migration ran twice, no dup). `UNIQUE(menu_key, tenant_id)` does NOT dedup NULL tenant_id in Postgres — hence the live migration `scratchpad/apply_portal_nav.sql` used an explicit existence guard. Fresh-build seed block uses `INSERT ... RETURNING menu_id INTO m_portal` (single exec on empty DB) — correct for fresh build only (bundle is non-idempotent by design, fine).

**Test:** `RbacNavigationTests.PartnerPortal_Surfaces_For_ReadSelf_Holder_And_Gate_Hides_It_From_ZeroPermUser` — positive (read_self holder sees /portal, asserts route/icon/labelHi) + negative (zero-perm custom role does NOT). Reuses existing `SeedZeroPermissionUserAsync`/`CleanupZeroPermissionUserAsync` helpers. Full suite 197/197.

No security surface touched beyond nav seeding. Carryover items unchanged.
