# Known Issues & Open Decisions

## Slice 02 backend — security conditions to track (auditor PASS, not Slice-02 blockers)
- **⚠️ CONSENT ENFORCEMENT — becomes a DPDP BLOCKER at Slice 03.** Scopes `docslot.patients.read`/`prescriptions.*`/`reports.*`/`abdm.*` are `requires_consent=true`. Slice 02 `/public/patients` returns empty stubs so it's fine now, but consent MUST be enforced before Slice 03 serves any real PHI through those scopes. **Slice 03 build + gate must enforce consent + purpose-of-use on patient/clinical reads.**
- AES webhook-signing-secret key is a committed dev passphrase (`mediq.Api/appsettings.json`) → move to KMS/secret-manager + register the field in `encrypted_fields_registry` (→ slice 05). **✅ PARTIAL (slice 05): field registered in `encrypted_fields_registry`. STILL OPEN: committed dev passphrase → KMS/secret-manager (pre-deploy).**
- Webhook **replay protection**: add signed timestamp/nonce + tolerance window.
- Webhook **SSRF**: add egress allowlist / private-range (169.254.x, link-local, internal) deny on subscriber URLs.
- Finer `platform.api.*` permission keys (currently single coarse `platform.api_clients.manage`) — needs canonical seed addition.

## Slice 01 backend — security conditions to track (auditor, not Slice-01 blockers)
- **Prod auth hardening (pre-deploy):** move JWT from HMAC-SHA256 + committed dev key (`mediq.Api`/`mediq.Gateway` appsettings) to **RS256/JWKS** with signing key from env/secrets, never committed.
- **DB-level audit append-only:** **✅ RESOLVED (slice 05 guard trigger + slice 03b grant-layer REVOKE).** **✅ Least-privilege app DB role RESOLVED (slice 03b):** app now connects as `docslot_app` (NOSUPERUSER/NOBYPASSRLS); escape-hatch GUC confined so the app role can't bypass append-only. STILL OPEN (pre-deploy): set a `docslot_app` password from a secret manager (dev uses localhost trust auth).
- **RLS was decorative (app = superuser/bypassrls):** **✅ RESOLVED (slice 03b): app runs as `docslot_app` (NOBYPASSRLS); RLS actively blocks cross-tenant clinical reads (proven by test).**
- **tenant-GUC pool bleed (slice 07 auditor BLOCKER):** **✅ RESOLVED.** session-scoped `app.tenant_id` GUC on pooled connections leaked across requests (Dashboard_Summary flake = canary, cross-tenant PHI hazard under concurrency). Now `BeginTenantScopeAsync` → transaction-scoped `SET LOCAL` that auto-clears on commit/rollback; write-then-throw security records (audit/login-attempt/lockout/chain-revoke/purpose-of-use) moved to a dedicated connection so they survive command rollback. Proven: 24-parallel two-tenant read no-bleed test + 3× clean parallel full-suite.
- **broker self-service IDOR (slice 07 auditor BLOCKER):** **✅ RESOLVED.** `me/wallet`/`me/links` took a spoofable `brokerId` query param. Now a server-signed `broker_id` JWT claim (resolved from `commission.brokers.user_id` at login/refresh/switch) is the only trusted broker identity (`RequireOwnBroker()`); param removed. Proven: broker A→only A, non-broker→403.
- **Durable idempotency store (→ slice 03):** ~~currently `InMemoryIdempotencyStore`~~ **✅ RESOLVED (slice 03 durable store; slice 05 promoted `platform.idempotency_keys` into canonical SQL 01_platform_core.sql; app no longer creates the table at runtime).**
- **audit-chain trigger `trg_audit_chain` concurrency:** **✅ RESOLVED (slice 03b): `append_to_audit_chain` takes `pg_advisory_xact_lock(8675309001)` before reading the head → serialized; chain stays intact under parallel writes; test parallelization re-enabled.** Repair (if ever broken): disable trigger, TRUNCATE audit_chain RESTART IDENTITY, re-append in occurred_at order.
- **NEW (slice 05): anomaly_events worker deferred** — no audit_log→anomaly consumer built; the table + v_security_review_queue exist. Build a background worker in a later slice.
- **MessagePack 2.5.192** transitive via Aspire AppHost SDK (GHSA-hv8m-jj95-wg3x) — bump when feasible.
- BLOCKER (in remediation now): `X-Tenant-Id` header overrode JWT tenant claim → cross-tenant RLS/PHI path. Fix: tenant from validated JWT only; switch-tenant must validate membership + mint new token, fail-closed.

## Slice 03b — tracked (auditor PASS, live-verified)
- **Pool-safety watch-item (latent cross-tenant PHI leak):** clinical read path uses session-scoped `set_config(app.tenant_id, …, is_local=false)` on a pinned connection (query handlers run outside the command tx). Safe TODAY only because Npgsql default `DISCARD ALL` resets on connection return — there is NO explicit reset in code. If `No Reset On Close` or multiplexing is ever enabled, `app.tenant_id` leaks across tenants on pooled connections. FIX (→08 hardening): add an explicit reset at request scope + a guard test. Consider `FORCE ROW LEVEL SECURITY` / dedicated table-owner role as extra hardening.
- **access_policies column gate**: service exists but not called by clinical read handlers; endpoint-level permission gating covers the coarse "who reaches clinical data" boundary. Wire column policy before any mixed-sensitivity single-endpoint read ships. TRACKED.

## 🔴 SLICE 08 — platform-wide RBAC-seeding completeness hole (auditor, slice 07 finding)
The original `01_platform_core.sql` grants super_admin only the permissions that exist AT THAT POINT in run order; each later product file must backfill super_admin, but only `07_commission_broker.sql` did. **Live DB: super_admin is MISSING docslot 32/32 (incl. `medical_access.break_glass`, `prescription.read`, `patient.read`), ai 18/18, and 26/32 platform perms (incl. `platform.overrides.grant/read`, `menus.manage`)** + all future products. Real authz hole — super_admin currently can't do clinical reads or grant overrides. **FIX in slice 08:** add an end-of-bundle "grant super_admin EVERY permission" sweep (canonical SQL), and audit the other system roles (tenant_owner/admin/staff/doctor/broker) against their intended scope. Re-validate bundle.

## Slice 07 FE — ✅ DONE (Care Partners console)
5 tabs (partners/attributions/rules/payouts/disputes); Approve≠Execute as distinct gated buttons+states+keys+mutations; Care-Partner terminology (grep-clean, en+hi); PCPNDT as non-disableable enforced badge; no PAN/PHI in lists/palette/url/toast; build green. Pending reconciliation: no CommissionController GET-list endpoints yet (built to spec); invoice/Form-16A stubs; menu seeding (→08).

## Slice 07 BE — 🔴 VETO in remediation: (1) broker me/* IDOR (client brokerId, no self-binding); (2) pool-safety tenant-GUC bleed (session set_config on pooled conn, no reset → cross-tenant under concurrency; Dashboard_Summary flake = canary). + folding in: widen encrypted cols aadhaar_last_4 VARCHAR(4)/medical_history.title VARCHAR(200)→TEXT (latent corruption).

## CONSOLIDATED BACKLOG → fold into Slice 08 (RBAC nav) + a backend read-endpoints pass
**Menu seeding** (`08_rbac_navigation.sql`): add nav rows + menu→permission maps for `/developers` (gate `platform.api_clients.manage`) and `/security` (gate `platform.audit.read` etc.). Frontends currently mock these menu nodes.
**Missing permission keys** (canonical seed additions, then re-gate FE/BE off the closest interim key): `docslot.patient.create` (currently `.update`), `docslot.booking.no_show` (currently `.complete`), finer `platform.api.*` (webhooks.manage / api.logs.read vs single coarse `platform.api_clients.manage`), optional finer `platform.breach.*`.
**Read-list GET endpoints (Slice 08b) — ✅ DONE 2026-06-14. All built to FE mock contracts (camelCase) so FE reconciliation is a one-line queryFn swap. No schema edits.**
- Slice 02: `GET /api/v1/api-requests` paginated (`ApiRequestLogPageDto{items,total,page,pageSize}`), gate `platform.api_clients.manage`. GOTCHA: nullable filter params need `@p::uuid`/`@p::timestamptz` casts in `(@p IS NULL OR ...)` or Npgsql 500s on untyped NULL.
- Slice 05 security console: `GET /security/{audit-chain/anchors, dpdp/requests, breaches, review-queue, keys, deletion-certificates}` via `ISecurityReadService`. `verify` now returns `lastVerifiedAt` (= MAX(audit_anchors.anchored_at)). Subject phone MASKED; keys carry NO key material; review-queue actor = initials only (view has no subject phone → null). Gates: audit.read / export_requests.process / breach.read / anomalies.review / encryption_keys.read / deletion.certify.
- Slice 03b clinical: `GET /patients/{id}/{lab-reports, abdm-records, consent}` + `POST /patients/{id}/lab-reports/{reportId}/deliver` (gate `docslot.report.deliver`). Lists are headers-only (no decrypt); all require X-Purpose-Of-Use; abdm-list is consent-REQUIRED. consent DTO derives clinicalConsent from patients.consent_given_at, abdmConsent from abdm_consents active+granted.
- Commission: most GET lists ALREADY existed (brokers/rules/payouts/disputes/campaigns) — backlog note was stale. ADDED `GET /commission/attributions` (`AttributionListItemDto`, first-name+masked-phone). PHI FIX: `BrokerDto.Phone`→`MaskedPhone` (the broker list was leaking raw phone). Enriched PayoutDto+`BrokerName`, DisputeDto+`BookingRef`/`BrokerName`.
**Still-open drift (flagged, NOT in 08b scope):** `PrescriptionListItemDto` returns `doctorId` but FE PrescriptionListItemSchema wants `doctorName` (pre-existing 03b endpoint). Invoice/Form-16A PDF download still deferred (no stub built).

## Slice 05 FE — ✅ DONE (security/compliance console)
5 tabs (audit integrity / DPDP rights / breach register / review queue / keys), irreversible-erasure UX (typed ERASE + reason + once-shown downloadable cert, never re-fetchable), mandatory break-glass justification, no PHI/key material rendered, all gated on real slice-05 keys, build green.

## Frontend follow-ups
- ✅ D4 (mobile drawer @768px + topbar overflow) + code-splitting (852kB→365kB, no chunk warning) — DONE 2026-06-14.
- ✅ D6/D7 — DONE 2026-06-14: all Radix dialogs have DialogTitle/Description (console clean); on-brand bilingual `notFoundComponent` in AppShell; **focus-return-to-trigger fixed** (slide-overs open programmatically from the store, so Radix had no trigger to restore to → focus fell to body; now capture activeElement on open + restore via onCloseAutoFocus). Tab-trap itself was never broken.

## Frontend (Wave 1)
- Build emits a single ~564 kB JS chunk — code-splitting deferred (route-level lazy loading) to a later wave. Benign warning.
- `npm install` reported 3 high-severity advisories in TRANSITIVE deps — no functional impact; revisit before production.

## Backend contracts (Wave 2) — product sign-off needed when endpoints get built
1. `DashboardSummaryDto.TodayRevenue` aggregation rule (which statuses count; fee vs paid; future payments table) — placeholder = doctor fees on realized bookings.
2. `NoShowRate` denominator window (today's terminal bookings vs rolling) — product decision.
3. `BookingListItemDto.TokenNumber` join key to `opd_tokens` (booking_id vs patient+date+doctor) — confirm FK when wiring query.
4. JSON enum serialization: API must configure a converter that round-trips snake_case `[EnumMember]` tokens; frontend mirrors the exact string tokens, not member names.
5. Phone masking helper belongs in API/Utilities layer (not added — contracts only). Full-phone reveal endpoint must carry purpose-of-use.

## Open contract decision — patient registration permission
- The canonical schema exposes `docslot.patient.read` and `docslot.patient.update` but **no `docslot.patient.create`/register permission**. Frontend "Add patient" is currently gated on `docslot.patient.update` as the closest fit. DECISION NEEDED (backend/RBAC): add a dedicated `docslot.patient.create` (or `.register`) permission, or keep update as the gate. Affects slice 03 backend + the frontend gate. Note: `docslot.patients` is cross-tenant by phone (global identity), which is part of why registration is modeled differently.

## Frontend ↔ Slice 01 contract reconciliation (done, react-ui-engineer)
Frontend mock contracts aligned to the real Slice 01 DTOs so mock→real swap is a one-line queryFn change per hook: `/me/menus` serves a bare `MenuNodeDto[]` (not `{tenantType,items}`); MenuNode carries `id/parentId/sortOrder/isSectionHeader`, nullable `labelHi`/`icon`; `/me/permissions` = `PermissionSetDto{userId,tenantId,permissionKeys}`; `/me/badges` = `BadgesDto{counts}` with seeded key `pending_bookings_count`; permission keys are `docslot.`-prefixed. `tenantType` now lives on `MeDto`.

## Schema corrections vs orchestrator brief (SQL wins, ADR-007)
- BookingStatus has 6 values incl. `rescheduled` (brief said 5).
- Source column is `booked_via` with values `whatsapp/dashboard/api/walk_in/phone_call` (brief said whatsapp/walk-in/phone).
- `preferred_language` has NO CHECK constraint (free VARCHAR).

## Slice 08b — read endpoints: DIRECT-VERIFIED SECURE (formal auditor ratification pending — platform rate-limit)
Orchestrator直接 code-verified (2026-06-14) since the auditor subagent was rate-limited: BrokerDto=MaskedPhone+PanVerified (no raw phone/PAN; raw only on register-request input); AttributionListItemDto=PatientFirstName+PatientMaskedPhone only; all SecurityController GETs RequirePermission-gated (audit.read/breach.read/anomalies.review/encryption_keys.read/deletion.certify); KeyStatusDto/ApiRequestLogDto carry NO key material/bodies/PHI; clinical lists (lab-reports/abdm-records/medical-history/consent) gated + Purpose() 422-without-header + ABDM consent-required. Architect's 47/47 tests cover gating/purpose/masking. → Re-run the formal `security-compliance-auditor` ratification when the platform rate-limit clears.

## ⏸️ PENDING (blocked on transient platform rate-limit 2026-06-14, NOT usage cap)
- **Comprehensive FE reconciliation** (react engineer aa93ef59f281d4ceb): mock menus → real seeded 21-menu tree; re-gate to docslot.patient.create / booking.no_show; sweep all mock contracts vs real SharedDataModel DTOs; clean per-hook mock→real swap. Failed 3× on rate-limit — RETRY when platform recovers.
- Formal 08b audit ratification (above).
- Remaining slices: 09 chat_identity (last product slice), 06 ai_services (Python/FastAPI — separate stack/specialist).
