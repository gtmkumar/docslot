// Doctors feature: queries + mutations. Co-located per feature-folder rule. Reads
// route through the backend seam (mock by default; live behind VITE_USE_REAL_API).
// The add-doctor mutation POSTs to /doctors with an Idempotency-Key and invalidates
// the directory so the new card appears immediately.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { addDoctor, listDoctorCards } from '@/lib/backend';

export const doctorCardsQueryKey = ['doctors', 'cards'] as const;

export function useDoctorCards() {
  return useQuery({ queryKey: doctorCardsQueryKey, queryFn: listDoctorCards });
}

export interface AddDoctorInput {
  fullName: string;
  departmentId: string | null;
  specialization: string | null;
  qualifications: string[];
  consultationFee: number | null;
  phone: string | null;
  /** Stable key generated once at submit (action start) by the caller. */
  idempotencyKey: string;
}

/** Provision a doctor into the tenant. Invalidates the directory on success so the
 *  "Practitioners · N" count + the new card refresh from server state. */
export function useAddDoctor() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: AddDoctorInput) => addDoctor(input),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: doctorCardsQueryKey });
    },
  });
}
