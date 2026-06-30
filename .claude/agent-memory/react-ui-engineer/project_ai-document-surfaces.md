---
name: ai-document-surfaces
description: Slice 15 frontend — OCR lab-report extract + RAG ask (PHI mutations) + AI ops screen, behind VITE_USE_REAL_API; PHI-out-of-cache + purpose-of-use rules.
metadata:
  type: project
---

Phase-4 Slice 15 surfaced the slice-11/14 AI document capabilities (previously invisible) in the React app, behind `VITE_USE_REAL_API`, mirroring PR #36's no-show/triage seam + PHI discipline.

**Three surfaces:**
- RAG "Ask about this patient's history" — slide-over on the History tab (gated `docslot.medical_history.read`).
- OCR "Extract lab report" — slide-over on the Reports tab (gated `docslot.report.read`).
- AI Operations screen `/ai-ops` — non-PHI ops summaries (extractions list + RAG status), each section gated independently.

**Why:** the OCR + RAG backends shipped in slices 11 & 14 but had no UI; this exposes them with the same fail-safe/PHI posture as the AI-assist PR (#36).

**How to apply (load-bearing rules for future AI/PHI work here):**
- PHI (OCR analyte values, RAG answer, RAG question) flows ONLY through `useMutation` — never a TanStack query key (keys persist to cache). Ops summaries are non-PHI → ordinary queries.
- `X-Purpose-Of-Use` is forwarded for the two PHI POSTs (always — patient-bound; server 422s without it); never on the ops GETs.
- `extractLabReport` carries an Idempotency-Key (persisted artifact); `askPatientRag` does not (advisory, like triage).
- PHI panels are TRANSIENT slide-overs (carry purpose + PHI → never URL-encoded/restored) — same posture as the other clinical panels; added to `SlideOverHost` TRANSIENT_SET, NOT to the router panel search schema.
- `available:false` → render an "unavailable" state, never a fabricated result. Consent 403 → `ConsentBlocked` + break-glass.

**Open contract gap:** no `navigation_menus` row for AI Operations in `08_rbac_navigation.sql` (same gap as developers/security). Nav node mocked in `lib/mock/index.ts` (`ai_ops`, icon 'sparkles'); live nav needs a backend row + menu→permission map (`docslot.report.read` OR `docslot.medical_history.read`). Flagged to orchestrator. See [[contract-surface]], [[clinical-records]], [[live-api-seam]].

Detailed endpoint/zod/file notes live in `.agents/memory/api-contracts.md` ("AI OCR + RAG document surfaces wired LIVE").
