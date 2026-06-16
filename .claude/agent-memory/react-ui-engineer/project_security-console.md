---
name: security-console
description: Slice 05 frontend — Security & Compliance console (/security): audit chain, DPDP export/erasure, breach register, break-glass review queue, encryption keys. Irreversible-erasure UX + no-PHI rules.
metadata:
  type: project
---

Slice 05 (security_hardening) frontend shipped the Security & Compliance console at `/security` (super_admin / DPO scope). Mirrors `SecurityController` + the result records in `mediq.Application/Features/Security` (camelCase). SENSITIVE SURFACE — be conservative.

## Screens (`features/security/`, route `/security`, lazy-loaded)
- `SecurityScreen.tsx` — Radix Tabs: Audit integrity | DPDP rights | Breach register | Review queue | Encryption keys. `api.ts` has all hooks. Shared `SecurityBadge`/`SensitiveTag` in `components/SecurityBadges.tsx`.
- Tabs: `AuditTab` (trust indicator intact/broken + Verify now + anchor history + Anchor head now), `DpdpTab` (requests table + Export/Erase actions), `BreachesTab` (72h DPB clock, overdue highlighting), `ReviewTab` (break-glass + anomaly queue + Record break-glass), `KeysTab` (rotation status, NO key material).
- Panels: `DataExportPanel` (result shown inline), `ErasurePanel`, `DeletionCertificatePanel` (once-shown), `ReportBreachPanel`, `BreakGlassPanel`.

## Permission-key gates (all REAL slice-05 keys, verified in 05_security_hardening.sql / SecurityController attrs)
- Verify chain → `platform.audit.verify_chain`; Anchor → `platform.audit.anchor`
- Export → `platform.export_requests.process`; Erase → `platform.deletion.certify`
- Report breach → `platform.breach.read` (the controller's own gate); Break-glass → `docslot.medical_access.break_glass`
- Read tabs gated by the menu server-side: review queue `platform.anomalies.review`, keys `platform.encryption_keys.read`, audit-read `platform.audit.read`. All 9 added to the `SIGNED_IN_PERMISSIONS` demo seed.

## IRREVERSIBLE ERASURE UX (the key conservative pattern — don't weaken it)
`ErasurePanel`: danger warning explaining cryptographic erasure destroys the subject's keys (data permanently unrecoverable). Submit DISABLED until ALL of: valid subject phone + a reason + the user typed the exact confirm word `ERASE` (`security.erase.confirmWord`). No one-click path. On success the deletion certificate is handed to `DeletionCertificatePanel` via `openPanel({type:'deletionCertificate', result})` — the SAME once-shown pattern as `clientSecret`: NOT URL-addressable (excluded from router enum + `UrlPanelType`), NEVER written to React Query, `ui` store not persisted → gone on refresh. Audit greps clean (no setQueryData/persist of cert/signature).

## NO PHI on screen (hard rule for this surface)
Subject identity is a MASKED phone only (`maskPhone`, field `subjectMaskedPhone`) — never a name/email. Review-queue actor is a short label (`Dr. A.S.`), never a name/email. KeysTab shows metadata + rotation status only, NO key material. The export/erase panels take a raw `subjectPhone` as an OPERATOR INPUT (the lookup key, matching the backend request field) — that's input, not rendered PHI.

## Mock contracts (lib/mock/contracts.ts) + adapter (lib/mock/security.ts, re-exported from index)
`AuditChainVerify(+breaks[], lastVerifiedAt), AuditAnchorResult, AuditAnchor, DataExportResult(+downloadToken; no bundle contents), ErasureResult(+cert metadata), DpdpRequest, Breach, ReviewQueueItem, KeyStatus, SecurityCreated`. Audit chain seeded BROKEN at one link to surface the known concurrency finding (Verify re-runs the same mock). Mutations: verify/anchor/export/erase/reportBreach/recordBreakGlass — all take a stable `idempotencyKey` (de-duped via idemCache); the cert/secret generated at call time, never seeded.

## Missing-GET / missing-key flags to orchestrator
- **Missing GETs (built to spec):** SecurityController has only the 6 action endpoints. NO list GETs exist for: anchor history, DPDP requests, breach register, review queue (`v_security_review_queue`), key status (`v_key_rotation_status`), deletion certificates. Built all read views to spec — reconciliation pass needed (like the api-requests Logs tab).
- **UI-convenience fields not in raw DTOs (flag):** `AuditChainVerify.lastVerifiedAt`, `DataExportResult.downloadToken`, the enriched cert fields on `ErasureResult` (signatureAlgorithm/digitalSignature/certifiedAt/deletedRecordCounts) — confirm against the real DTOs on reconciliation.
- **MENU SEEDING TODO (flagged):** no nav row for `/security` in 08_rbac_navigation.sql — mocked a `security` menu node (icon `shield-check`) in lib/mock/index.ts; backend needs the navigation_menus row + menu permission map.

## Misc
- New `relativeTime(iso)` helper in `lib/format.ts` (compact "2h ago"/"in 3d") for the audit last-verified + breach clock.
- New `security.*` i18n namespace (en+hi, compiler-enforced parity). Build: SecurityScreen ~25kB own chunk, main ~391kB, no warnings.
