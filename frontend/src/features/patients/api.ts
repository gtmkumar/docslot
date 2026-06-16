// Patients + clinical records feature (Slice 03b). Co-located queries/mutations.
//
// ACCESS MODEL reflected here:
//  - Clinical READS take the declared `purpose` (the X-Purpose-Of-Use the UI
//    declared via the purpose gate). The query is DISABLED until a purpose exists,
//    so no clinical fetch happens before declaration. The mock throws without it.
//  - ABDM detail reads additionally require active consent (mock throws otherwise).
//  - Mutations carry a stable caller-generated Idempotency-Key.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { addPatient, listPatients } from '@/lib/backend';
import {
  deliverLabReport,
  getAbdmRecord,
  getLabReport,
  getPatientConsent,
  getPrescription,
  issuePrescription,
  listAbdmRecords,
  listLabReports,
  listMedicalHistory,
  listPrescriptions,
  pushAbdmRecord,
  uploadLabReport,
} from '@/lib/mock';
import type { IssuePrescriptionRequest, UploadLabReportRequest } from '@/lib/mock/contracts';

// ── Patients list (mock derives from the seed; real hits /patients) ──────────
export const patientsListQueryKey = ['patients', 'list'] as const;

export function usePatients() {
  return useQuery({ queryKey: patientsListQueryKey, queryFn: listPatients });
}

// ── Add patient (cross-tenant by phone) ──────────────────────────────────────
// Live mode POSTs /patients with a stable caller-generated Idempotency-Key, then
// invalidates the list so the new row appears. Mock mode is a no-op (synthetic id).
export interface AddPatientInput {
  phone: string;
  name: string;
  age: string;
  lang: 'en' | 'hi';
  idempotencyKey: string;
}

export function useAddPatient() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: AddPatientInput) => addPatient(input),
    onSuccess: () => void qc.invalidateQueries({ queryKey: patientsListQueryKey }),
  });
}

// ── Consent (drives what the records screen shows) ───────────────────────────
export function usePatientConsent(patientId: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'consent', patientId] as const,
    queryFn: () => getPatientConsent(patientId ?? ''),
    enabled: Boolean(patientId),
  });
}

// ── Prescriptions ────────────────────────────────────────────────────────────
// The LIST carries no clinical content, so it doesn't require a purpose; it's
// gated by docslot.prescription.read (menu/route) like any list. The DETAIL does.
export function usePrescriptions(patientId: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'prescriptions', patientId] as const,
    queryFn: () => listPrescriptions(patientId ?? ''),
    enabled: Boolean(patientId),
  });
}

export function usePrescription(prescriptionId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'prescription', prescriptionId, purpose] as const,
    queryFn: () => getPrescription(prescriptionId ?? '', purpose),
    enabled: Boolean(prescriptionId) && Boolean(purpose),
  });
}

export function useIssuePrescription(patientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: IssuePrescriptionRequest & { idempotencyKey: string }) =>
      issuePrescription(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical', 'prescriptions', patientId] }),
  });
}

// ── Lab reports ──────────────────────────────────────────────────────────────
export function useLabReports(patientId: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'reports', patientId] as const,
    queryFn: () => listLabReports(patientId ?? ''),
    enabled: Boolean(patientId),
  });
}

export function useLabReport(reportId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'report', reportId, purpose] as const,
    queryFn: () => getLabReport(reportId ?? '', purpose),
    enabled: Boolean(reportId) && Boolean(purpose),
  });
}

export function useUploadLabReport(patientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: UploadLabReportRequest & { idempotencyKey: string }) =>
      uploadLabReport(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical', 'reports', patientId] }),
  });
}

export function useDeliverLabReport(patientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ reportId, idempotencyKey }: { reportId: string; idempotencyKey: string }) =>
      deliverLabReport(reportId, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical', 'reports', patientId] }),
  });
}

// ── Medical history (purpose-gated read) ─────────────────────────────────────
export function useMedicalHistory(patientId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'history', patientId, purpose] as const,
    queryFn: () => listMedicalHistory(patientId ?? '', purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
  });
}

// ── ABDM (consent-gated) ─────────────────────────────────────────────────────
export function useAbdmRecords(patientId: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'abdm', patientId] as const,
    queryFn: () => listAbdmRecords(patientId ?? ''),
    enabled: Boolean(patientId),
  });
}

export function useAbdmRecord(recordId: string | undefined, patientId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'abdmRecord', recordId, purpose] as const,
    queryFn: () => getAbdmRecord(recordId ?? '', patientId ?? '', purpose),
    enabled: Boolean(recordId) && Boolean(patientId) && Boolean(purpose),
    retry: false, // a consent failure shouldn't be retried
  });
}

export function usePushAbdm(patientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey }: { idempotencyKey: string }) => pushAbdmRecord({ patientId }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical', 'abdm', patientId] }),
  });
}
