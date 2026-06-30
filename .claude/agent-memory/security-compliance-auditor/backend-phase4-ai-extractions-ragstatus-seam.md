---
name: backend-phase4-ai-extractions-ragstatus-seam
description: Phase-4 slice-14 .NET→Python AI OPERATIONAL read proxies (OCR extraction LIST + RAG STATUS); clean PASS — establishes the "operational/non-PHI AI read" pattern (no purpose gate, no IDoNotCacheResponse) vs slice-11's individual-PHI reads
metadata:
  type: project
---

Phase-4 slice 14 `feat/phase4-ai-extractions-ragstatus`: two NEW read-only GET endpoints on ClinicalController extending the slice-11 AI seam ([[backend-phase4-ai-ocr-rag-seam]]). Pure .NET HTTP proxies; AI service UNCHANGED; no schema, no new permission, no DI change. Build 0 err, 290/290 (2 new ClinicalPhiTests).

**Verdict: PASS — no conditions, no blockers.**

Endpoints: `GET /api/v1/ai/extractions?limit=` (perm `docslot.report.read`) → AI `GET /ai/v1/extractions?limit=`; `GET /api/v1/ai/rag/status` (perm `docslot.medical_history.read`) → AI `GET /ai/v1/rag/status`. Files: ClinicalController.cs (+2 endpoints), AiAbstractions.cs (IAiOcrClient.ListExtractionsAsync + OcrExtractionSummaryResult; IAiRagClient.GetStatusAsync + RagStatusResult/RagKnowledgeBaseResult), AiDocumentFeatures.cs (ListAiExtractionsQuery/GetRagStatusQuery — IQuery + ISelfManagedTransaction), AiOcrClients.cs + AiRagClients.cs (Http*/Stub* impls), AiDocumentDtos.cs (OcrExtractionSummaryDto/OcrExtractionListDto/RagKnowledgeBaseDto/RagStatusDto).

**THE KEY DISTINCTION this slice establishes — "operational/non-PHI AI read" vs slice-11's individual-PHI read:**
- These return SUMMARIES/COUNTS only, no individual PHI → so deliberately NO consent gate, NO X-Purpose-Of-Use forward, NO IDoNotCacheResponse (correct — slice-11 extract/ask needed all three because they return decrypted analytes/answer). Verified the AI side matches: `list_extractions` (extractions.py) and `status_` (rag.py:200) have NO `phi_access.record_purpose_of_use` call (unlike `/rag/ask` which does). The .NET absence of a purpose gate is consistent with the AI absence — not a gap.

**VERIFIED SOUND (every coordinator decision A–E checked at source):**
- A. Cross-tenant CLOSED by delegation to JWT. .NET does ZERO tenant query/check; forwards ONLY the caller's own `Authorization` header (`context.HttpContext?.Request.Headers.Authorization`) — adds no client-controllable tenant input. AI `get_principal` (auth.py) derives tenant STRICTLY from the JWT `tenant_id` claim; there is NO X-Tenant-Id / header override path on the AI side. AI repo scopes by `principal.tenant_id` (`list_extractions(tenant,limit)`, `status_for_tenant(tenant)`). Forging cross-tenant needs the HS256 signing key (server-held). Same boundary as slice-11. List is tenant-WIDE (all patients) but `report.read` scope='tenant' (03_docslot.sql:845) so listing matches the grant — no over-exposure.
- B. ISelfManagedTransaction on BOTH queries verified: TenantScopeQueryBehavior (Behaviors.cs:57-58) returns next() without BeginTenantScopeAsync for the marker → no pooled DB conn held across the AI HTTP hop. Handlers do ZERO .NET DB work (pure proxies), so marker is correct.
- C. Perm altitude correct: report.read + medical_history.read are BOTH is_dangerous=true (03_docslot.sql:845,849), scope='tenant'. extraction-list on report.read = the natural perm (abnormalCount is a clinical signal; gated by the same perm that reads the full report → no escalation). rag-status on medical_history.read = slightly conservative (just counts) but the natural related perm — safe direction. Test: tenant_admin lacks report.read (is_dangerous excluded from auto-grant) → 403.
- D. 4xx not masked: AiErrorMapper.ThrowIfClientError maps 401/403→ForbiddenException(403), 400/422→ValidationException(422), 404→KeyNotFound(404); 5xx/_→null→Available=false. catch filter `when (ex is not AppExceptionBase and not KeyNotFoundException)` lets all typed exceptions escape. (These endpoints have no AI-side gate beyond JWT validity, so the realistic 4xx is 401/403 token/tenant-claim failure — propagated, not masked.)
- E. No PHI: DTOs carry NO analyte values, NO patient identifier in the row (extractionId is opaque; sourceType/status/createdAt/abnormalCount aggregate). RagStatus = embeddings/patientsIndexed/KB counts (tenant aggregates). Adapters log `(int)StatusCode` + ex message only. limit clamped 1..200 both .NET (Math.Clamp) and AI (ge=1,le=200) — no resource exhaustion / injection (int in URL).

Gates: tenant/RLS ✅ (JWT-delegated, no new table); permission ✅ (2 existing is_dangerous perms, deny-wins untouched, no new perm); PHI-purpose ✅ N/A (operational, not individual PHI); payout ✅ N/A; audit ✅ N/A (no writes); encryption/secrets ✅ N/A (no secrets, no PHI-at-rest); regulatory ✅ N/A; webhook ✅ N/A.

INFO (no action): HS256 shared-key + forwarded-JWT-in-transit (ensure HTTPS/in-cluster in prod) is a pre-existing carry-forward (RS256/KMS is external-cred-gated future work), not new here. Negative authz test covers the extraction-list 403 only, not rag/status 403 (test-coverage nit). slice-11's sourceUrl MEDIUM condition is untouched (these are GET, no sourceUrl) — remains an open slice-11 carry-forward, not re-raised.
