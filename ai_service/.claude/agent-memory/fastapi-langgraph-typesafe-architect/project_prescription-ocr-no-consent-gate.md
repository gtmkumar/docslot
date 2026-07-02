---
name: prescription-ocr-no-consent-gate
description: POST /ai/v1/extractions/prescription deliberately has NO consent/break-glass gate (ratified posture)
metadata:
  type: project
---

`POST /ai/v1/extractions/prescription` (ai_service) does NOT call `phi_access.enforce_phi_gate` — no consent/break-glass check — UNLIKE the lab-report path.

**Why:** the prescription image is CALLER-SUPPLIED (front-desk intake of a paper document the patient physically handed over), not a read of stored patient PHI. It feeds the paper-Rx IMPORT write, which the security auditor ratified as gate-free; the .NET proxy in front follows the same posture. Team-lead directed this on 2026-07-02, after the endpoint was first built WITH the gate.

**How to apply:** Do NOT re-add `enforce_phi_gate` to this endpoint (a future "consistency" refactor would break the ratified posture). What STAYS: JWT principal, `X-Purpose-Of-Use` required (400), `relatedPatientId` required (422) + `patient_linked_to_tenant` (404), a service-token refusal (403, orthogonal guard I kept — non-human identity may not do intake), `record_purpose_of_use(grant=None)` (first-class, is_break_glass=false), envelope-encrypted `raw_ocr_text`, and `requires_human_review=true` always. The lab-report endpoint KEEPS its consent gate. See [[ai-service-gates]].
