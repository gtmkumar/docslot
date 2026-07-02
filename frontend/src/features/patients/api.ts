// Patients + clinical records feature. Co-located queries/mutations.
//
// All clinical/ABDM/consent fns now flow through the BACKEND SEAM (@/lib/backend):
// real .NET HTTP when VITE_USE_REAL_API is on, the mock seam otherwise — identical
// signatures, so this file is mode-agnostic.
//
// ACCESS MODEL reflected here:
//  - Clinical READS (lists + details, except consent) send the declared `purpose`
//    as X-Purpose-Of-Use. The query is DISABLED until a purpose exists, so no
//    clinical fetch happens before declaration (a read without it is a 422).
//  - A consent-denied read 403s (real) / throws ConsentRequiredError (mock); the
//    UI surfaces a contextual break-glass affordance, POSTs /security/break-glass,
//    then re-fetches the gated read (useBreakGlass invalidates the clinical cache).
//  - ABDM detail additionally requires active consent.
//  - Mutations carry a stable caller-generated Idempotency-Key.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  addPatient,
  breakGlass,
  createMedicalHistory,
  deliverLabReport,
  getAbdmRecord,
  getLabReport,
  getPatientConsent,
  getPrescription,
  listAbdmRecords,
  listLabReports,
  listMedicalHistory,
  listPatients,
  listPrescriptions,
  pushAbdmRecord,
  updateMedicalHistory,
  uploadLabReport,
} from '@/lib/backend';
import type {
  BreakGlassRequest,
  CreateMedicalHistoryRequest,
  UpdateMedicalHistoryRequest,
  UploadLabReportRequest,
} from '@/lib/mock/contracts';

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
// The LIST carries no clinical content, but the backend STILL gates the read with
// X-Purpose-Of-Use (a missing purpose is a 422), so the query is DISABLED until a
// purpose is declared and the declared purpose is sent on the list read too.
export function usePrescriptions(patientId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'prescriptions', patientId, purpose] as const,
    queryFn: () => listPrescriptions(patientId ?? '', purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
  });
}

export function usePrescription(prescriptionId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'prescription', prescriptionId, purpose] as const,
    queryFn: () => getPrescription(prescriptionId ?? '', purpose),
    enabled: Boolean(prescriptionId) && Boolean(purpose),
    retry: false, // a consent 403 shouldn't be retried — surface it so break-glass can offer
  });
}

// ── Lab reports ──────────────────────────────────────────────────────────────
// Purpose-gated list read (see usePrescriptions): the declared purpose is sent on
// the list read and the query stays disabled until it exists.
export function useLabReports(patientId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'reports', patientId, purpose] as const,
    queryFn: () => listLabReports(patientId ?? '', purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
  });
}

export function useLabReport(reportId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'report', reportId, purpose] as const,
    queryFn: () => getLabReport(reportId ?? '', purpose),
    enabled: Boolean(reportId) && Boolean(purpose),
    retry: false, // a consent 403 shouldn't be retried — surface it so break-glass can offer
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
      deliverLabReport(patientId, reportId, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical', 'reports', patientId] }),
  });
}

// ── Medical history (purpose-gated read + create/update writes) ──────────────
export function useMedicalHistory(patientId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'history', patientId, purpose] as const,
    queryFn: () => listMedicalHistory(patientId ?? '', purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
  });
}

/** Create a history entry. title/description are PHI — captured in the form, sent
 *  in the POST body (never the URL/log), encrypted server-side. Idempotency-Key. */
export function useCreateMedicalHistory(patientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateMedicalHistoryRequest & { idempotencyKey: string }) =>
      createMedicalHistory(patientId, req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical', 'history', patientId] }),
  });
}

/** Update (or retire, isActive=false) a history entry. Idempotency-Key. */
export function useUpdateMedicalHistory(patientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      historyId,
      idempotencyKey,
      ...req
    }: UpdateMedicalHistoryRequest & { historyId: string; idempotencyKey: string }) =>
      updateMedicalHistory(patientId, historyId, req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical', 'history', patientId] }),
  });
}

// ── ABDM (consent-gated) ─────────────────────────────────────────────────────
// The list read is purpose-gated like the others (X-Purpose-Of-Use); the DETAIL
// additionally requires active consent (a 403 → consent-blocked state).
export function useAbdmRecords(patientId: string | undefined, purpose: string | undefined) {
  return useQuery({
    queryKey: ['clinical', 'abdm', patientId, purpose] as const,
    queryFn: () => listAbdmRecords(patientId ?? '', purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
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

// ── Break-glass (emergency access) ───────────────────────────────────────────
// POST /security/break-glass grants emergency access to a consent-denied record.
// On success we invalidate the patient's whole clinical namespace so EVERY gated
// read (lists + the open detail) re-fetches and now succeeds — the "re-fetch the
// gated read" step. The justification (>=10 chars) is validated by the panel + the
// zod schema; resourceType/resourceId are derived from the read's context, never
// typed free-hand.
export function useBreakGlass(patientId: string) {
  void patientId; // bound for the mutation's context/log; the grant is patient-scoped server-side
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: BreakGlassRequest & { idempotencyKey: string }) =>
      breakGlass(req, idempotencyKey),
    // Invalidate the whole clinical namespace so EVERY gated read (the lists AND the
    // open detail panel) re-fetches and — now that a grant exists — succeeds. This
    // is the "re-fetch the gated read" step; it makes break-glass seamless rather
    // than requiring a manual retry. Clinical queries are few + session-scoped.
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['clinical'] }),
  });
}
