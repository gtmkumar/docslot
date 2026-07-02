// Consultation composer feature — co-located queries/mutations, all through the
// backend seam (@/lib/backend): real .NET HTTP when VITE_USE_REAL_API is on, the
// mock seam otherwise (identical signatures).
//
// ACCESS MODEL:
//  - get-or-create + finalize are patient-bound → they carry the declared
//    X-Purpose-Of-Use (the composer declares it on entry via the purpose gate; the
//    draft query stays DISABLED until a purpose exists, so no read hits the wire
//    without one — a read without it is a 422).
//  - The two POSTs carry a stable caller-generated Idempotency-Key.
//  - Autosave is a PATCH → 204 (no PHI echoed); it is NOT a query key.

import { useMutation, useQuery } from '@tanstack/react-query';
import {
  finalizeConsultation,
  getBooking,
  getOrCreateConsultation,
  saveConsultation,
} from '@/lib/backend';
import type { SaveConsultationRequest } from '@/lib/mock/contracts';

/** Get-or-create the draft for a booking. Purpose-gated (disabled until declared).
 *  The Idempotency-Key is DERIVED from the booking id so a re-mount reuses the same
 *  key and the server returns the same draft (get-or-create is idempotent). */
export function useConsultation(bookingId: string, purpose: string | undefined) {
  return useQuery({
    queryKey: ['consult', 'draft', bookingId, purpose] as const,
    queryFn: () => getOrCreateConsultation(bookingId, purpose, `consult-create-${bookingId}`),
    enabled: Boolean(bookingId) && Boolean(purpose),
    retry: false, // a consent 403 shouldn't be retried — surface it for break-glass
    staleTime: Infinity, // the draft is the source of truth in-session; autosave owns writes
  });
}

/** Booking demographics for the composer header (age / sex / masked phone). Shares
 *  the bookings-detail cache key with the bookings feature. */
export function useConsultBooking(bookingId: string) {
  return useQuery({
    queryKey: ['bookings', 'detail', bookingId] as const,
    queryFn: () => getBooking(bookingId),
    enabled: Boolean(bookingId),
  });
}

/** Debounced autosave (PATCH → 204). The screen calls mutate() on a debounce; the
 *  mutation carries no PHI back. */
export function useSaveConsultation(consultationId: string | undefined) {
  return useMutation({
    mutationFn: (req: SaveConsultationRequest) => saveConsultation(consultationId ?? '', req),
  });
}

/** Finalize (draft → finalized). result.finalized:false ⇒ blocked by unoverridden
 *  high/critical drug alerts — surface them, collect an override reason, retry. The
 *  Idempotency-Key is caller-generated (stable across the alert→override retry). */
export function useFinalizeConsultation(consultationId: string | undefined) {
  return useMutation({
    mutationFn: ({ overrideReason, idempotencyKey }: { overrideReason: string | null; idempotencyKey: string }) =>
      finalizeConsultation(consultationId ?? '', { overrideReason }, idempotencyKey),
  });
}
