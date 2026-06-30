// AI operational reads (non-PHI summaries) for the AI Operations screen.
// The extractions list (GET /ai/extractions) + RAG status (GET /ai/rag/status)
// carry NO PHI (header summaries / counts only), so — unlike the patient-bound
// ask/extract which are mutations — these ARE cacheable TanStack queries. Each is
// gated by the caller via usePermissions().can() and stays DISABLED until enabled,
// so a user lacking the permission never fires the request.

import { useQuery } from '@tanstack/react-query';
import { getRagStatus, listAiExtractions } from '@/lib/backend';

/** Recent OCR extractions (summaries only). Gate `enabled` on docslot.report.read. */
export function useAiExtractions(limit = 20, enabled = true) {
  return useQuery({
    queryKey: ['ai', 'extractions', limit] as const,
    queryFn: () => listAiExtractions(limit),
    enabled,
    staleTime: 30_000,
  });
}

/** RAG knowledge-base status. Gate `enabled` on docslot.medical_history.read. */
export function useRagStatus(enabled = true) {
  return useQuery({
    queryKey: ['ai', 'rag-status'] as const,
    queryFn: () => getRagStatus(),
    enabled,
    staleTime: 30_000,
  });
}
