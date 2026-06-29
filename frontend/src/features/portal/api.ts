// Care Partner self-service portal (Slice 07 — broker self-service). Queries +
// mutations for the partner's OWN data. The server resolves broker_id from the
// JWT `broker_id` claim, so NONE of these carry an id (IDOR-safe). The book-on-
// behalf POST creates a BEHALF booking whose result status is
// 'awaiting_patient_consent' — the patient approves via WhatsApp OTP (DPDP).

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  createPortalBooking,
  createReferralLink,
  getBrokerWallet,
  listPractitioners,
  listReferralLinks,
  listSlots,
} from '@/lib/backend';
import type {
  BrokerPortalBookingRequest,
  CreateReferralLinkRequest,
} from '@/lib/mock/contracts';

export const walletQueryKey = ['portal', 'wallet'] as const;
export const linksQueryKey = ['portal', 'links'] as const;

// Doctor + slot pickers for book-on-behalf. Reused from the shared seam (NOT the
// bookings feature internals) so the portal stays self-contained.
export function usePortalPractitioners() {
  return useQuery({ queryKey: ['portal', 'practitioners'], queryFn: () => listPractitioners() });
}

export function usePortalSlots(doctorId: string | null) {
  return useQuery({
    queryKey: ['portal', 'slots', doctorId],
    queryFn: () => listSlots(doctorId as string),
    enabled: Boolean(doctorId),
  });
}

export function useBrokerWallet() {
  return useQuery({ queryKey: walletQueryKey, queryFn: getBrokerWallet });
}

export function useReferralLinks() {
  return useQuery({ queryKey: linksQueryKey, queryFn: listReferralLinks });
}

export function useCreateReferralLink() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateReferralLinkRequest & { idempotencyKey: string }) =>
      createReferralLink(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: linksQueryKey }),
  });
}

export function useCreatePortalBooking() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: BrokerPortalBookingRequest & { idempotencyKey: string }) =>
      createPortalBooking(req, idempotencyKey),
    onSuccess: () => {
      // A behalf booking earns a (pending) attribution → the wallet's current-month
      // attribution count can change. Refresh the wallet on success.
      void qc.invalidateQueries({ queryKey: walletQueryKey });
    },
  });
}
