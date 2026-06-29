---
name: backend-phase4-ai-triage-seam
description: Phase-4 .NET→Python AI TRIAGE proxy (POST /api/v1/triage); PHI complaint egress. FINAL PASS — Finding 1 (purpose-of-use not forwarded for patient/booking-bound triage) CLOSED in-PR, re-verified build 0 + 212/212
metadata:
  type: project
---

Phase-4 slice: .NET proxy `POST /api/v1/triage {complaint, patientId?, bookingId?, patientAge?}` → AI sibling LangGraph `/triage`. Follows the no-show seam ([[backend-phase4-ai-noshow-seam]]) but MORE sensitive: it egresses a free-text symptom complaint (PHI). No schema/RBAC-perm/PHI-storage/payout/encryption change.

**Verdict: FINAL PASS** (was PASS-WITH-CONDITIONS; the one HIGH condition is now CLOSED & independently re-verified).

**Finding 1 CLOSED (re-verified in-PR, 2026-06-29).** Preferred remediation applied & checked against actual source (not coordinator claim):
- `TriageRequestInput` += `DeclaredPurpose` (AiAbstractions.cs:43-44); `SubmitTriageCommand` += `DeclaredPurpose` (TriageFeatures.cs:14).
- Validator REQUIRES it `.When(PatientId is not null || BookingId is not null)` → 422 with DPDP message (TriageFeatures.cs:27-29). This condition EXACTLY mirrors the AI-side gate (`routers/triage.py`: purpose required when `body.patientId OR body.bookingId`), so there's no path where .NET passes but AI 400s on purpose → the gate is no longer masked as Available=false.
- Controller reads `Request.Headers["X-Purpose-Of-Use"]` and passes to the command (TriageController.cs:29-30); HttpAiTriageClient forwards it as a header alongside Authorization, only when non-blank (AiTriageClients.cs:34-37). So the AI service's purpose_of_use_log + audit fire on the patient-bound path.
- Pure free-text triage (no patient/booking) still needs no purpose — correct, matches the AI side's "free-text complaint needs ONLY auth."
- Tests (Finding 2 also closed): patient-bound w/o X-Purpose-Of-Use → 422; patient-bound WITH header → 200; free-text emergency/routine/empty unchanged (DocslotBookingTests.cs:228-241, uses factory.PatientId).
- INDEPENDENTLY VERIFIED: dotnet build 0 errors; full integration suite 212/212. The ISelfManagedTransaction / IDoNotCacheResponse markers and complaint-not-logged posture are unchanged by this remediation (re-confirmed in source).

Files: AiTriageClients.cs (Http + Stub), TriageFeatures.cs (SubmitTriageCommand), TriageDtos.cs, TriageController.cs; modified AiAbstractions.cs, DependencyInjection.cs (shared ConfigureAiHttp), 1 test.

**THE condition (HIGH, DPDP purpose-of-use bypass):** The AI-side router `ai_service/app/routers/triage.py` REQUIRES `X-Purpose-Of-Use` (HTTP 400) AND logs purpose_of_use + audit (best-effort) AND verifies patient_tenant_links / booking_in_tenant — but ONLY when `patientId` or `bookingId` is bound. The .NET `HttpAiTriageClient` (AiTriageClients.cs:31-33) forwards ONLY the Authorization header, NOT `X-Purpose-Of-Use`. TriageController reads no purpose header at all (grep: NONE). So when the intake desk binds a patientId/bookingId, the AI side 400s → .NET fail-safe returns null → DTO Available=false → the desk silently sees "triage unavailable" with NO purpose log and NO audit row. Two problems: (1) the established DocSlot pattern (ClinicalController/PatientsController/PrescriptionFeatures all require+record X-Purpose-Of-Use for patient/booking-bound PHI) is broken here; (2) the fail-safe MASKS a compliance-gate rejection as an availability blip. FIX: TriageController must read X-Purpose-Of-Use (require it when PatientId/BookingId present, mirroring PatientDetail validator) and the client must forward it as a header; OR if triage is to stay a pure free-text path, strip PatientId/BookingId from the request contract so no PHI-resource binding ever reaches the AI side. Owner's call which.

What's SOUND (verified):
- **Complaint never logged**: HttpAiTriageClient logs status-only (AiTriageClients.cs:39) + ex-message-only (:58, no complaint); StubAiTriageClient in-memory keyword match, no log; handler/controller don't log; Program.cs:86 UseSerilogRequestLogging logs no body/headers; appsettings has no RequestBody/Destructure enrichment. Verified by grep.
- **No plaintext cache of clinical result**: SubmitTriageCommand is IDoNotCacheResponse (TriageFeatures.cs:14-15) → IdempotencyBehavior bypasses store entirely (Behaviors.cs:138 `if IDoNotCacheResponse return await next()`, no serve/no save). Correctly NOT IRequireIdempotency (advisory). Same posture I blessed for Form16A.
- **No DB conn held across HTTP**: SubmitTriageCommand is ISelfManagedTransaction → UnitOfWorkBehavior skips the wrapping tx (Behaviors.cs:206). Handler does ZERO .NET DB work — only `triage.TriageAsync(...)`. So no pooled connection pinned across the network call. This is the no-show MEDIUM (closed there) NOT reintroduced — verified clean.
- **Onward egress governed by AI side**: slice 5B ([[ai-service-phi-egress-governance]]) gates triage `_extract_llm` on the `triage` use_case allows_phi/BAA config; default allows_phi=false → no external-LLM egress. This .NET hop doesn't bypass it (it can't reach the LLM directly).
- **Fail-safe**: any AI failure (unreachable/timeout/non-2xx/null) → null → DTO Available=false, no 500, no fabricated assessment (TriageFeatures.cs:37-38). Confirmed. (Caveat: also masks the PoU-400 above.)
- **Input bound**: complaint capped 4000 chars by validator (TriageFeatures.cs:22). Adequate DoS/payload bound.
- **SSRF-safe**: path is hardcoded literal `/triage`; BaseUrl is operator config. No user URL.
- **Permission gate `docslot.booking.create`** (TriageController.cs:23): is_dangerous=false in catalog (03_docslot.sql:825). Reasonable — intake/reception triages as part of booking. RULING: acceptable, NOT under/over-privileged for an advisory routing suggestion (no PHI stored, no clinical record written). If triage ever WRITES a clinical assessment, revisit with a distinct clinical perm.

INFO: the new test only exercises the no-PHI-binding path (free-text only, no patientId/bookingId), so the purpose-of-use gap is untested. Add a test for the patientId-bound path once the PoU forwarding is wired.
