// Bookings feature: queries + mutations. Co-located per feature-folder rule.
// Mutations route through the api-client seam and attach an Idempotency-Key on
// every state-changing POST (REACT_SKILL: idempotent-safe mutations).
//
// IMPORTANT (security): the Idempotency-Key is a STABLE key generated ONCE per
// logical action by the CALLER (on action start), then passed in as
// `input.idempotencyKey`. It is NOT generated inside the mutationFn — that would
// produce a fresh key on every retry and defeat de-duplication, allowing a
// double-submit to double-confirm or double-charge. TanStack Query retries reuse
// the same variables object, so the key is preserved across retries.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
// The bookings LIST + the booking action mutations (approve/cancel/complete/
// no-show/create) are wired to the live API behind the VITE_USE_REAL_API flag.
// practitioners/slots have no live endpoint wave yet, so they stay on the mock
// seam (so does sendPaymentLink).
import {
  approveBooking,
  cancelBooking,
  checkInBooking,
  completeBooking,
  createBooking,
  getBooking,
  listBookings,
  listPractitioners,
  listSlots,
  noShowBooking,
  rescheduleBooking,
} from '@/lib/backend';
import { sendPaymentLink } from '@/lib/mock';
import type { BookingRow } from '@/lib/mock/contracts';

export const bookingsQueryKey = ['bookings', 'list'] as const;

// Sibling caches that a booking mutation invalidates so live server state wins:
// the dashboard KPI strip (live-queue / confirmed / revenue / no-show) and the
// batched nav badge counts (pending_bookings_count, today_bookings_count). Kept
// in sync with features/dashboard/api.ts + features/navigation/api.ts.
const dashboardSummaryQueryKey = ['dashboard', 'summary'] as const;
const badgesQueryKey = ['me', 'badges'] as const;

export function useBookings() {
  return useQuery({ queryKey: bookingsQueryKey, queryFn: listBookings });
}

/** Pending bookings only — the approval queue source. */
export function usePendingBookings() {
  return useQuery({
    queryKey: bookingsQueryKey,
    queryFn: listBookings,
    select: (rows: BookingRow[]) => rows.filter((b) => b.status === 'pending'),
  });
}

export function usePractitioners(deptId?: string) {
  return useQuery({
    queryKey: ['practitioners', deptId ?? 'all'] as const,
    queryFn: () => listPractitioners(deptId),
  });
}

export function useSlots(doctorId: string | undefined, date?: string) {
  return useQuery({
    queryKey: ['slots', doctorId, date ?? 'today'] as const,
    queryFn: () => listSlots(doctorId ?? '', date),
    enabled: Boolean(doctorId),
  });
}

/**
 * Fetch the FULL booking (phone, age, gender, note, language, …) for a given id,
 * so the manage / approve slide-over can open for a REAL booking. In live mode
 * this hits GET /bookings/{id}; in mock mode it resolves the prototype seam. Only
 * enabled while an id is present (the panel opener triggers the fetch on demand).
 */
export function useBookingDetail(bookingId: string | undefined) {
  return useQuery({
    queryKey: ['bookings', 'detail', bookingId] as const,
    queryFn: () => getBooking(bookingId ?? ''),
    enabled: Boolean(bookingId),
    staleTime: 0,
  });
}

/** Invalidate the bookings list + the dashboard summary + the nav badges so a
 *  status transition is reflected everywhere the server state drives (queue row,
 *  KPI strip, pending-bookings badge). In live mode this refetches real data; in
 *  mock mode the refetch returns the same static data (no behavior change). */
function invalidateBookingViews(qc: ReturnType<typeof useQueryClient>): void {
  void qc.invalidateQueries({ queryKey: bookingsQueryKey });
  void qc.invalidateQueries({ queryKey: dashboardSummaryQueryKey });
  void qc.invalidateQueries({ queryKey: badgesQueryKey });
}

export interface ApproveBookingInput {
  bookingId: string;
  /** Stable key generated once at action start by the caller. */
  idempotencyKey: string;
}

/**
 * Approve a booking. Optimistically marks it confirmed in the cached list so the
 * queue row updates instantly; rolls back on error. The 5s undo window is handled
 * by the calling component (sonner deferred-mutation), which only fires this after
 * the toast elapses.
 */
export function useApproveBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ bookingId, idempotencyKey }: ApproveBookingInput) =>
      approveBooking(bookingId, idempotencyKey),
    onMutate: async ({ bookingId }) => {
      await qc.cancelQueries({ queryKey: bookingsQueryKey });
      const prev = qc.getQueryData<BookingRow[]>(bookingsQueryKey);
      qc.setQueryData<BookingRow[]>(bookingsQueryKey, (rows) =>
        rows?.map((b) => (b.id === bookingId ? { ...b, status: 'confirmed' } : b)),
      );
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(bookingsQueryKey, ctx.prev);
    },
    onSettled: () => invalidateBookingViews(qc),
  });
}

export interface CancelBookingInput {
  bookingId: string;
  reason: string;
  idempotencyKey: string;
}

export function useCancelBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ bookingId, reason, idempotencyKey }: CancelBookingInput) =>
      cancelBooking(bookingId, reason, idempotencyKey),
    onMutate: async ({ bookingId }) => {
      await qc.cancelQueries({ queryKey: bookingsQueryKey });
      const prev = qc.getQueryData<BookingRow[]>(bookingsQueryKey);
      qc.setQueryData<BookingRow[]>(bookingsQueryKey, (rows) =>
        rows?.map((b) => (b.id === bookingId ? { ...b, status: 'cancelled' } : b)),
      );
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(bookingsQueryKey, ctx.prev);
    },
    onSettled: () => invalidateBookingViews(qc),
  });
}

/** Mark a confirmed booking complete (gated docslot.booking.complete). */
export interface CompleteBookingInput {
  bookingId: string;
  idempotencyKey: string;
}

export function useCompleteBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ bookingId, idempotencyKey }: CompleteBookingInput) =>
      completeBooking(bookingId, idempotencyKey),
    onMutate: async ({ bookingId }) => {
      await qc.cancelQueries({ queryKey: bookingsQueryKey });
      const prev = qc.getQueryData<BookingRow[]>(bookingsQueryKey);
      qc.setQueryData<BookingRow[]>(bookingsQueryKey, (rows) =>
        rows?.map((b) => (b.id === bookingId ? { ...b, status: 'completed' } : b)),
      );
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(bookingsQueryKey, ctx.prev);
    },
    onSettled: () => invalidateBookingViews(qc),
  });
}

/** Mark a booking as no-show (gated docslot.booking.no_show). */
export interface NoShowBookingInput {
  bookingId: string;
  idempotencyKey: string;
}

export function useNoShowBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ bookingId, idempotencyKey }: NoShowBookingInput) =>
      noShowBooking(bookingId, idempotencyKey),
    onMutate: async ({ bookingId }) => {
      await qc.cancelQueries({ queryKey: bookingsQueryKey });
      const prev = qc.getQueryData<BookingRow[]>(bookingsQueryKey);
      qc.setQueryData<BookingRow[]>(bookingsQueryKey, (rows) =>
        rows?.map((b) => (b.id === bookingId ? { ...b, status: 'no_show' } : b)),
      );
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(bookingsQueryKey, ctx.prev);
    },
    onSettled: () => invalidateBookingViews(qc),
  });
}

/** Check a CONFIRMED patient in at the desk (confirmed → checked_in). Optimistic. */
export interface CheckInBookingInput {
  bookingId: string;
  idempotencyKey: string;
}

export function useCheckInBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ bookingId, idempotencyKey }: CheckInBookingInput) =>
      checkInBooking(bookingId, idempotencyKey),
    onMutate: async ({ bookingId }) => {
      await qc.cancelQueries({ queryKey: bookingsQueryKey });
      const prev = qc.getQueryData<BookingRow[]>(bookingsQueryKey);
      qc.setQueryData<BookingRow[]>(bookingsQueryKey, (rows) =>
        rows?.map((b) => (b.id === bookingId ? { ...b, status: 'checked_in' } : b)),
      );
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(bookingsQueryKey, ctx.prev);
    },
    onSettled: () => invalidateBookingViews(qc),
  });
}

export interface CreateBookingInput {
  phone: string;
  name: string;
  age: string;
  sex: 'F' | 'M' | 'O';
  lang: 'en' | 'hi';
  reason: string;
  doctorId: string;
  slot: string;
  idempotencyKey: string;
}

export function useCreateBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...draft }: CreateBookingInput) => createBooking(draft, idempotencyKey),
    onSuccess: () => invalidateBookingViews(qc),
  });
}

/**
 * Reschedule a booking onto a new slot (and optionally a new doctor / with a
 * reason). A reschedule supersedes the old booking with a NEW one (lineage), so
 * we don't optimistically mutate a single row's status — we invalidate the
 * booking views on success/settle so the list/detail refetch the lineage. The
 * Idempotency-Key is generated ONCE by the caller and reused on retry.
 */
export interface RescheduleBookingInput {
  bookingId: string;
  newSlotId: string;
  newDoctorId?: string;
  reason?: string;
  idempotencyKey: string;
}

export function useRescheduleBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ bookingId, newSlotId, newDoctorId, reason, idempotencyKey }: RescheduleBookingInput) =>
      rescheduleBooking(bookingId, { newSlotId, newDoctorId, reason }, idempotencyKey),
    onSettled: (_data, _err, { bookingId }) => {
      invalidateBookingViews(qc);
      // The superseded booking's detail must refetch (its status changes to
      // rescheduled) so any open manage panel reflects the lineage.
      void qc.invalidateQueries({ queryKey: ['bookings', 'detail', bookingId] });
    },
  });
}

export interface SendPaymentLinkInput {
  bookingId: string;
  amount: number;
  expiresInMins: number;
  idempotencyKey: string;
}

export function useSendPaymentLink() {
  return useMutation({
    mutationFn: (input: SendPaymentLinkInput) => sendPaymentLink(input),
  });
}
