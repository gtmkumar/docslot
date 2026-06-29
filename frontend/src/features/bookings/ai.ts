// AI-assist hooks for the bookings feature: no-show risk (query) + triage
// (mutation). Co-located per the feature-folder rule; kept separate from api.ts
// (booking CRUD) since these surface a different, advisory backend capability.
//
// PHI discipline (triage): the `complaint` is protected health information. It is
// passed as a MUTATION VARIABLE only — never placed in a query key (which TanStack
// Query persists in the cache), never logged. The intake path is the pure
// free-text triage (no patientId/bookingId), so no X-Purpose-Of-Use is sent; the
// submitTriage fn still forwards a purpose for the booking-bound case (mirrors the
// server's 422 gate) should a future caller bind it to a subject.

import { useMutation, useQuery } from '@tanstack/react-query';
import { getNoShowRisk, submitTriage } from '@/lib/backend';
import type { NoShowRisk, TriageRequestInput, TriageResult } from '@/lib/mock/contracts';

/**
 * Fetch the AI no-show risk for a booking. ON-DEMAND: only enabled while a booking
 * id is present (the manage/approve slide-over passes its id when open), so the
 * model isn't queried for the whole list. The query key carries ONLY the booking
 * id (no PHI). `available:false` in the result is a valid success — the component
 * renders the "unavailable" chip; a 404/network failure surfaces via isError.
 */
export function useNoShowRisk(bookingId: string | undefined) {
  return useQuery<NoShowRisk>({
    queryKey: ['bookings', 'no-show-risk', bookingId] as const,
    queryFn: () => getNoShowRisk(bookingId ?? ''),
    enabled: Boolean(bookingId),
    // Risk is cheap to recompute and changes as the appointment nears; let it go
    // stale quickly but don't refetch aggressively on window focus.
    staleTime: 60_000,
    refetchOnWindowFocus: false,
  });
}

/**
 * Run triage on a typed complaint. A MUTATION (not a query) so the PHI complaint
 * is never cached in a query key. Triage is advisory and NOT idempotency-required,
 * so submitTriage attaches no Idempotency-Key. The intake caller passes neither
 * patientId nor bookingId (free-text path → no purpose-of-use header).
 */
export function useTriage() {
  return useMutation<TriageResult, unknown, TriageRequestInput>({
    mutationFn: (input: TriageRequestInput) => submitTriage(input),
  });
}
