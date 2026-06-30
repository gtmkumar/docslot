// AI document-assist hooks for the patient clinical view: OCR lab-report extraction
// + RAG ask over the patient's indexed history. Both surface PHI, so both are
// MUTATIONS (never queries) — the analyte values, the RAG answer, AND the RAG
// question all stay OUT of the TanStack Query cache (query keys persist to cache).
//
// PHI discipline (the PR#36 auditor lesson, applied to documents):
//  - usePatientRag: the `question` is PHI → a MUTATION VARIABLE only; the result's
//    `answer` is PHI but lives only in the transient mutation result (not keyed).
//  - useExtractLabReport: the analyte values are PHI → same transient mutation
//    result (never a query key).
//  - Both are patient-bound → the caller passes the declared purpose-of-use, which
//    the seam forwards as X-Purpose-Of-Use (the server 422s without it).
//  - Nothing here is logged, persisted, or echoed into a toast / Zustand / URL.
//
// Co-located in the patients feature (the clinical records screen renders them),
// kept separate from api.ts (clinical CRUD) since these surface an advisory AI
// capability.

import { useMutation } from '@tanstack/react-query';
import { askPatientRag, extractLabReport } from '@/lib/backend';
import { idempotencyKey } from '@/lib/api-client';
import type { OcrExtraction, PurposeOfUse, RagAnswer } from '@/lib/mock/contracts';

/**
 * Run OCR extraction for a patient (+ optional booking). The result (analyte
 * values — PHI) lives only in the mutation result. The PERSISTED extraction POST
 * carries a fresh Idempotency-Key per run, and X-Purpose-Of-Use is forwarded (the
 * extraction is patient-bound). A consent 403 surfaces via `mutation.error` so the
 * caller can offer break-glass.
 */
export function useExtractLabReport(patientId: string, purpose: PurposeOfUse, bookingId?: string) {
  return useMutation<OcrExtraction, unknown, void>({
    mutationFn: () =>
      extractLabReport(
        { relatedPatientId: patientId, relatedBookingId: bookingId, purposeOfUse: purpose },
        idempotencyKey(),
      ),
  });
}

/**
 * Ask a natural-language question over the patient's indexed history. The question
 * (PHI) is the mutation VARIABLE — it is never placed in a query key, logged, or
 * echoed. X-Purpose-Of-Use is forwarded (the ask is patient-bound). Advisory, so
 * no Idempotency-Key.
 */
export function usePatientRag(patientId: string, purpose: PurposeOfUse) {
  return useMutation<RagAnswer, unknown, string>({
    mutationFn: (question: string) => askPatientRag({ patientId, question, purposeOfUse: purpose }),
  });
}
