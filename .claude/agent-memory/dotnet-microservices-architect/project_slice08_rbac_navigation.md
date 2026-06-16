---
name: slice08-rbac-navigation
description: Slice 08 consolidation — super_admin universal permission sweep (lives in 10, not 08), 2 new docslot keys + endpoint re-gate, full backend-driven menu tree. Non-obvious resolver scope-filtering gotcha.
metadata:
  type: project
---

Slice 08 (`08_rbac_navigation`) was the consolidation wave: close the super_admin authorization hole, add finer permission keys + re-gate endpoints, and seed the full navigation menu tree the frontends render. COMPLETE, security-relevant invariants verified (audit chain intact, app still runs as docslot_app non-super/non-bypassrls, no MediatR). Builds 0 err; 41/41 integration tests green across 3 consecutive parallel full runs (was 38 + 3 new in `RbacNavigationTests.cs`).

**Why:** super_admin held only ~55/125 perms (platform + commission only; 0 docslot, 0 ai, 0 future-products) — a real authz hole where the platform owner couldn't exercise product permissions. Frontends mock screens (Developers/Security/Care Partners/Analytics/Team) that had no menu rows, so `get_user_menus()` couldn't drive them.

**How to apply (durable decisions for future slices):**

- **super_admin universal sweep lives at the END of `10_roles_grants.sql`, NOT in 08.** Bundle run order is `01,02,03,05,06,07,08,09,04,10` — `04_future_products.sql` inserts permissions AFTER 08. A sweep in 08 misses them. 10 is the true terminal file and is re-runnable. The sweep is `INSERT INTO role_permissions SELECT super_admin, permission_id FROM permissions ON CONFLICT DO NOTHING` + a `DO` block that RAISEs EXCEPTION if super_admin's grant count != registry count (self-verifying: bundle prints "super_admin holds ALL N permissions"). **Any future product that adds permissions is auto-covered** — never hand-grant to super_admin again; rely on the sweep.

- **GOTCHA — `resolve_user_permissions(user, tenant)` filters role grants by `vup.tenant_id = p_tenant_id OR scope='platform'`.** super_admin's role assignment has `tenant_id = NULL` (platform-wide), so for a request scoped to a specific tenant, ONLY platform-scoped perms surface (plus any tenant-specific assignments that user also holds). This means "super_admin resolves all 127 keys via /me/permissions" is FALSE by design — the per-request set is a correct subset. Assert the universal-grant invariant at the `role_permissions` TABLE level (grants == registry), not via the resolved API set. [[slice01-platform-core]] established resolve-once-per-request.

- **New domain keys (added in 08, granted by inheritance):** `docslot.patient.create` (tenant, dangerous) and `docslot.booking.no_show` (tenant). Granted to every role that already held the parent key (`docslot.patient.update` → create; `docslot.booking.complete` → no_show) via an INSERT...SELECT off existing role_permissions, so re-gating locks nobody out. Old keys KEPT (FE interim gates still reference `.update`/`.complete`). Re-gated `PatientsController` POST `/patients` → `docslot.patient.create`; `BookingsController` no-show → `docslot.booking.no_show`. Rule when splitting a key: ADD new + inherit-grant from parent + re-gate backend; never remove the old key out from under the FE.

- **Menu tree (21 menus: 12 top-level + 9 children) is canonical in 08's `$seed_menus$` DO block.** Top-level: dashboard(Overview), bookings(+today/upcoming/history), calendar, patients(+clinical), doctors, lab, analytics, care_partners(+directory/payouts), team, developers, security, settings(+brokers/users/tenant). Bilingual (`menu_label`/`menu_label_hi`), tenant_type-aware (`applies_to_tenant_types`: doctors→hospital/clinic/diagnostic_center; lab→pathology_lab/...; else NULL=all), `badge_source` on bookings. Menu gates: developers→`platform.api_clients.manage`, security→`platform.audit.read`, care_partners→`commission.broker.read` (customer-facing label per MCI 6.4 — never "broker/commission" to patients), analytics→`docslot.analytics.read`, team→`tenant.roles.assign`. **bookings/calendar gated on BOTH `docslot.booking.read` AND `docslot.booking.read_self` (ANY-of)** so a doctor (self-scope only) still sees their schedule.

- **Live-DB alignment without re-running the non-idempotent bundle:** applied idempotent deltas (new keys, inherited grants, super_admin sweep) + `DELETE FROM navigation_menus WHERE is_system AND tenant_id IS NULL` then re-ran the `$seed_menus$` block. Safe because live had only seeded system menus, no tenant customizations. Live now matches canonical: super_admin 127/127, 21 menus.

- Fresh-DB bundle certification: 131 tables (incl. 04 future-product tables), 0 errors. Throwaway DB always dropped after.
