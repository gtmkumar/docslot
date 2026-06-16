// Security & Compliance console feature (Slice 05). Queries + mutations for the
// audit chain, DPDP rights, breaches, review queue, and key health. Mutations
// take a stable caller-generated Idempotency-Key.
//
// The export/erase RESULTS (download token / deletion certificate) are handed
// straight to a panel; they are NEVER written into any query cache.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  anchorAuditChain,
  eraseSubjectData,
  exportSubjectData,
  listAnchors,
  listBreaches,
  listDpdpRequests,
  listKeyStatus,
  listReviewQueue,
  recordBreakGlass,
  reportBreach,
  verifyAuditChain,
} from '@/lib/mock';

export const auditVerifyQueryKey = ['security', 'auditVerify'] as const;
export const anchorsQueryKey = ['security', 'anchors'] as const;
export const dpdpQueryKey = ['security', 'dpdp'] as const;
export const breachesQueryKey = ['security', 'breaches'] as const;
export const reviewQueueQueryKey = ['security', 'reviewQueue'] as const;
export const keyStatusQueryKey = ['security', 'keys'] as const;

export function useAuditVerify() {
  return useQuery({ queryKey: auditVerifyQueryKey, queryFn: verifyAuditChain });
}
export function useAnchors() {
  return useQuery({ queryKey: anchorsQueryKey, queryFn: listAnchors });
}
export function useDpdpRequests() {
  return useQuery({ queryKey: dpdpQueryKey, queryFn: listDpdpRequests });
}
export function useBreaches() {
  return useQuery({ queryKey: breachesQueryKey, queryFn: listBreaches });
}
export function useReviewQueue() {
  return useQuery({ queryKey: reviewQueueQueryKey, queryFn: listReviewQueue });
}
export function useKeyStatus() {
  return useQuery({ queryKey: keyStatusQueryKey, queryFn: listKeyStatus });
}

export function useAnchorChain() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ anchorType, anchorReference, idempotencyKey }: { anchorType: string; anchorReference: string; idempotencyKey: string }) =>
      anchorAuditChain({ anchorType, anchorReference }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: anchorsQueryKey }),
  });
}

export function useExportSubject() {
  return useMutation({
    mutationFn: ({ subjectPhone, idempotencyKey }: { subjectPhone: string; idempotencyKey: string }) =>
      exportSubjectData({ subjectPhone }, idempotencyKey),
  });
}

export function useEraseSubject() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ deletionRequestId, subjectPhone, idempotencyKey }: { deletionRequestId: string; subjectPhone: string; idempotencyKey: string }) =>
      eraseSubjectData({ deletionRequestId, subjectPhone }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: dpdpQueryKey }),
  });
}

export function useReportBreach() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ breachType, severity, description, affectedRecordCount, idempotencyKey }: { breachType: string; severity: string; description: string; affectedRecordCount: number; idempotencyKey: string }) =>
      reportBreach({ breachType, severity, description, affectedRecordCount }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: breachesQueryKey }),
  });
}

export function useRecordBreakGlass() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ resourceType, resourceId, justification, idempotencyKey }: { resourceType: string; resourceId: string; justification: string; idempotencyKey: string }) =>
      recordBreakGlass({ resourceType, resourceId, justification }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: reviewQueueQueryKey }),
  });
}
