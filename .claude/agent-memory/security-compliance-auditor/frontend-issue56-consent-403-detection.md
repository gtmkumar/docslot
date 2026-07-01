---
name: frontend-issue56-consent-403-detection
description: Issue #56 real-API consent/forbidden UI-state fix — cleared PASS; the broad-403=consent-denied approximation on the ABDM detail panel is safe ONLY under a specific 403/404/422 taxonomy + no-break-glass invariant; re-audit if those change
metadata:
  type: project
---

Issue #56 (branch fix/realapi-consent-error-states) — real-API mode mis-detected consent/forbidden states → wrong empty/blocked UI. Cleared PASS (no conditions). Pure display-correctness; grants no access, weakens no server gate, egresses no PHI.

Two changes:
1. `AbdmDetailPanel.tsx`: swapped mock-only `error instanceof ConsentRequiredError` for shared `isConsentDenied(error)` (in `patients/components/ConsentBlocked.tsx`) = ConsentRequiredError OR ApiError.status===403.
2. `ManageUserPanel.tsx` OverridesSection: 403 on the overrides read used to collapse to the "no overrides" empty state (reads as clean slate when actually forbidden); now precise `ApiError && status===403` → "no access" EmptyState, other errors → generic+retry. Does NOT leak whether overrides exist (fixed message regardless of count). Overrides read is server-gated on `platform.overrides.read` (SoD-distinct from dangerous `platform.overrides.grant`).

**Why the broad-403=consent approximation is safe on the ABDM DETAIL read** (`GET /abdm/records/{recordId}`, ClinicalController + ReportAndAbdmFeatures GetAbdmRecordQueryHandler): its realistic 403 taxonomy is —
- consent denial → 403 ForbiddenException (the intended consent-blocked path)
- cross-tenant / not-found → 404 KeyNotFoundException (RLS-scoped; NOT mislabeled as consent)
- missing X-Purpose-Of-Use → 422 ValidationException (NOT mislabeled as consent)
- RBAC (`docslot.abdm.records.read` missing) → 403, BUT the ABDM LIST uses the SAME permission, so a user who rendered the list to click into a record already passed that gate → RBAC-403-on-detail is unreachable in normal flow.
- Also key: the AbdmDetailPanel deliberately renders a PLAIN consent EmptyState, NOT the `ConsentBlocked` component → NO break-glass affordance → worst case is a cosmetically-wrong message, never an actionable wrong recovery path. Consent path renders strictly LESS (static i18n, no metadata); success path already only shows metadata + integer FHIR count (bundle never serialized, #54).

**Re-audit trigger (forward guardrail):** the safety rests on that taxonomy. Re-review if a future change (a) adds a break-glass affordance to AbdmDetailPanel, (b) makes the detail reachable without the list read (deep-link) , or (c) diverges the detail's permission from the list's — any of those turns a non-consent 403 into a potential wrong-recovery-path / misleading-state issue. Contrast: other clinical surfaces DO use `ConsentBlocked` (offers break-glass gated on `docslot.medical_access.break_glass`); mislabeling there is still bounded because break-glass unlocks CONSENT only, never RBAC.

Related: [[frontend-phase3-clinical-wiring]], [[clinical-read-dtos-joins]].
