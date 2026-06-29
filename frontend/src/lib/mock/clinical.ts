// Mock adapter for clinical PHI (Slice 03b). Shapes mirror the ClinicalDtos and
// the ClinicalController access model, so the mock→real swap is a no-op for zod.
//
// THE MOST PHI-SENSITIVE SURFACE — invariants baked in:
//  - Every clinical READ takes a declared `purpose` (the X-Purpose-Of-Use the UI
//    declared). The mock THROWS if it's absent, mirroring the server's 422 — so
//    the UI's purpose gate is load-bearing, not cosmetic.
//  - ABDM reads additionally require active consent → throw ConsentRequiredError
//    if consent is not 'granted'. The UI shows the "no active consent" state.
//  - List shapes carry NO clinical content (numbers/status/date/doctor only).
//  - Patient identity in clinical context is a MASKED phone (maskPhone).
//  - All clinical content here is CLEARLY SYNTHETIC (fake names/values).

import { maskPhone } from '@/lib/format';
import { DOCTORS, PATIENTS } from '@/lib/data';
import {
  AbdmRecordDetailSchema,
  AbdmRecordListItemSchema,
  BreakGlassResultSchema,
  CreateMedicalHistoryResultSchema,
  IssuePrescriptionResultSchema,
  LabReportDetailSchema,
  LabReportListItemSchema,
  MedicalHistorySchema,
  PatientConsentSchema,
  PrescriptionDetailSchema,
  PrescriptionListItemSchema,
  PushAbdmRecordResultSchema,
  UploadLabReportResultSchema,
  type AbdmRecordDetail,
  type AbdmRecordListItem,
  type BreakGlassRequest,
  type BreakGlassResult,
  type CreateMedicalHistoryRequest,
  type CreateMedicalHistoryResult,
  type IssuePrescriptionRequest,
  type IssuePrescriptionResult,
  type LabReportDetail,
  type LabReportListItem,
  type MedicalHistory,
  type PatientConsent,
  type PrescriptionDetail,
  type PrescriptionListItem,
  type PushAbdmRecordResult,
  type UpdateMedicalHistoryRequest,
  type UploadLabReportRequest,
  type UploadLabReportResult,
} from './contracts';

const LATENCY = 220;
function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY));
}

const idemCache = new Map<string, unknown>();
function withIdem<T>(key: string, compute: () => T): Promise<T> {
  if (idemCache.has(key)) return delay(idemCache.get(key) as T);
  const result = compute();
  idemCache.set(key, result);
  return delay(result);
}

const iso = (daysAgo: number) => new Date(Date.now() - daysAgo * 86_400_000).toISOString();

/** Thrown when a clinical read is attempted without a declared purpose-of-use. */
export class PurposeRequiredError extends Error {
  constructor() {
    super('A declared purpose-of-use is required to read clinical PHI.');
    this.name = 'PurposeRequiredError';
  }
}
/** Thrown when an ABDM read is attempted without active consent. */
export class ConsentRequiredError extends Error {
  constructor() {
    super('Active patient consent is required for ABDM records.');
    this.name = 'ConsentRequiredError';
  }
}

function requirePurpose(purpose: string | undefined): void {
  if (!purpose || purpose.trim().length === 0) throw new PurposeRequiredError();
}

/** Mirror the server's consent gate: a clinical read for a patient WITHOUT active
 *  clinical consent 403s (here: ConsentRequiredError), which the UI surfaces as a
 *  break-glass affordance. A break-glass grant flips the seed consent to 'granted'
 *  so the retried read succeeds. Patients with no consent seed are treated as
 *  granted (so the common path stays unblocked). */
function requireClinicalConsent(patientId: string): void {
  const consent = CONSENT[patientId];
  if (consent && consent.clinicalConsent !== 'granted') throw new ConsentRequiredError();
}

const doctorName = (id: string) => DOCTORS.find((d) => d.id === id)?.name ?? 'Dr. —';

// ── Consent context (seed; NO backend GET — flagged) ─────────────────────────
// Two patients have granted clinical consent; one has ABDM consent absent to
// exercise the "no active consent" + break-glass path.
const CONSENT: Record<string, PatientConsent> = {
  p1: { patientId: 'p1', maskedPhone: maskPhone(PATIENTS[0].phone), clinicalConsent: 'granted', abdmConsent: 'granted', consentExpiresAt: iso(-180) },
  p2: { patientId: 'p2', maskedPhone: maskPhone(PATIENTS[1].phone), clinicalConsent: 'granted', abdmConsent: 'revoked', consentExpiresAt: iso(-90) },
  p3: { patientId: 'p3', maskedPhone: maskPhone(PATIENTS[2].phone), clinicalConsent: 'requested', abdmConsent: 'denied', consentExpiresAt: null },
};

export function getPatientConsent(patientId: string): Promise<PatientConsent> {
  const c = CONSENT[patientId] ?? {
    patientId,
    maskedPhone: maskPhone(PATIENTS.find((p) => p.id === patientId)?.phone ?? '+91 00000 00000'),
    clinicalConsent: 'granted' as const,
    abdmConsent: 'granted' as const,
    consentExpiresAt: iso(-365),
  };
  return delay(PatientConsentSchema.parse(c));
}

// ── Prescriptions ────────────────────────────────────────────────────────────
interface SeedRx {
  prescriptionId: string;
  prescriptionNumber: string;
  patientId: string;
  doctorId: string;
  status: string;
  createdAt: string;
  chiefComplaints: string;
  examination: string;
  diagnosis: string;
  medications: { name: string; dose: string; frequency: string; duration: string }[];
  advice: string;
  followUpInDays: number | null;
}

// All content below is SYNTHETIC test data.
const RX: SeedRx[] = [
  {
    prescriptionId: 'rx-1', prescriptionNumber: 'PRX-2026-06-00481', patientId: 'p1', doctorId: 'd1', status: 'issued', createdAt: iso(2),
    chiefComplaints: 'Chest discomfort on exertion, x3 days', examination: 'BP 138/88, HR 84, no murmurs', diagnosis: 'Stable angina — suspected',
    medications: [
      { name: 'Aspirin', dose: '75mg', frequency: 'OD', duration: '30 days' },
      { name: 'Atorvastatin', dose: '20mg', frequency: 'HS', duration: '30 days' },
    ],
    advice: 'Low-salt diet; review with ECG in 1 week', followUpInDays: 7,
  },
  {
    prescriptionId: 'rx-2', prescriptionNumber: 'PRX-2026-05-00377', patientId: 'p1', doctorId: 'd1', status: 'issued', createdAt: iso(35),
    chiefComplaints: 'Routine review', examination: 'BP 130/84, stable', diagnosis: 'Hypertension — controlled',
    medications: [{ name: 'Telmisartan', dose: '40mg', frequency: 'OD', duration: '60 days' }],
    advice: 'Continue current regimen', followUpInDays: 60,
  },
];

export function listPrescriptions(patientId: string, purpose: string | undefined): Promise<PrescriptionListItem[]> {
  // The backend gates the LIST read with X-Purpose-Of-Use too (a missing purpose
  // is a 422). Mirror that here so the purpose gate stays load-bearing in mock mode.
  requirePurpose(purpose);
  const rows = RX.filter((r) => r.patientId === patientId).map((r) =>
    PrescriptionListItemSchema.parse({
      prescriptionId: r.prescriptionId,
      prescriptionNumber: r.prescriptionNumber,
      doctorId: r.doctorId,
      doctorName: doctorName(r.doctorId),
      status: r.status,
      createdAt: r.createdAt,
    }),
  );
  return delay(rows);
}

export function getPrescription(prescriptionId: string, purpose: string | undefined): Promise<PrescriptionDetail> {
  requirePurpose(purpose);
  const r = RX.find((x) => x.prescriptionId === prescriptionId);
  if (!r) return Promise.reject(new Error('Not found'));
  // The detail read is consent-gated (a denied read 403s → break-glass affordance).
  try {
    requireClinicalConsent(r.patientId);
  } catch (e) {
    return Promise.reject(e);
  }
  return delay(
    PrescriptionDetailSchema.parse({
      prescriptionId: r.prescriptionId,
      prescriptionNumber: r.prescriptionNumber,
      patientId: r.patientId,
      doctorId: r.doctorId,
      doctorName: doctorName(r.doctorId),
      chiefComplaints: r.chiefComplaints,
      examination: r.examination,
      diagnosis: r.diagnosis,
      medications: r.medications,
      advice: r.advice,
      followUpInDays: r.followUpInDays,
      status: r.status,
      createdAt: r.createdAt,
    }),
  );
}

export function issuePrescription(req: IssuePrescriptionRequest, idempotencyKey: string): Promise<IssuePrescriptionResult> {
  return withIdem(idempotencyKey, () => {
    void req;
    return IssuePrescriptionResultSchema.parse({
      prescriptionId: crypto.randomUUID(),
      prescriptionNumber: `PRX-2026-06-${String(500 + Math.floor(Math.random() * 99)).padStart(5, '0')}`,
    });
  });
}

// ── Lab reports ──────────────────────────────────────────────────────────────
interface SeedReport {
  reportId: string;
  reportNumber: string;
  patientId: string;
  testName: string;
  fileName: string;
  status: 'pending' | 'delivered';
  hasCriticalFindings: boolean;
  createdAt: string;
  results: { analyte: string; value: string; unit: string | null; refRange: string | null; flag: 'normal' | 'high' | 'low' | 'critical' | null }[];
}

const REPORTS: SeedReport[] = [
  {
    reportId: 'rpt-1', reportNumber: 'RPT-2026-06-00231', patientId: 'p1', testName: 'Lipid profile', fileName: 'lipid_profile.pdf',
    status: 'delivered', hasCriticalFindings: false, createdAt: iso(3),
    results: [
      { analyte: 'Total cholesterol', value: '212', unit: 'mg/dL', refRange: '<200', flag: 'high' },
      { analyte: 'HDL', value: '44', unit: 'mg/dL', refRange: '>40', flag: 'normal' },
      { analyte: 'LDL', value: '138', unit: 'mg/dL', refRange: '<100', flag: 'high' },
    ],
  },
  {
    reportId: 'rpt-2', reportNumber: 'RPT-2026-06-00250', patientId: 'p1', testName: 'Troponin I', fileName: 'troponin.pdf',
    status: 'pending', hasCriticalFindings: true, createdAt: iso(1),
    results: [{ analyte: 'Troponin I', value: '0.9', unit: 'ng/mL', refRange: '<0.04', flag: 'critical' }],
  },
];

export function listLabReports(patientId: string, purpose: string | undefined): Promise<LabReportListItem[]> {
  requirePurpose(purpose);
  const rows = REPORTS.filter((r) => r.patientId === patientId).map((r) =>
    LabReportListItemSchema.parse({
      reportId: r.reportId,
      reportNumber: r.reportNumber,
      testName: r.testName,
      status: r.status,
      hasCriticalFindings: r.hasCriticalFindings,
      createdAt: r.createdAt,
    }),
  );
  return delay(rows);
}

export function getLabReport(reportId: string, purpose: string | undefined): Promise<LabReportDetail> {
  requirePurpose(purpose);
  const r = REPORTS.find((x) => x.reportId === reportId);
  if (!r) return Promise.reject(new Error('Not found'));
  // The detail read is consent-gated (a denied read 403s → break-glass affordance).
  try {
    requireClinicalConsent(r.patientId);
  } catch (e) {
    return Promise.reject(e);
  }
  return delay(
    LabReportDetailSchema.parse({
      reportId: r.reportId,
      reportNumber: r.reportNumber,
      patientId: r.patientId,
      testName: r.testName,
      fileName: r.fileName,
      results: r.results,
      status: r.status,
      hasCriticalFindings: r.hasCriticalFindings,
      createdAt: r.createdAt,
    }),
  );
}

export function uploadLabReport(req: UploadLabReportRequest, idempotencyKey: string): Promise<UploadLabReportResult> {
  return withIdem(idempotencyKey, () => {
    void req;
    return UploadLabReportResultSchema.parse({
      reportId: crypto.randomUUID(),
      reportNumber: `RPT-2026-06-${String(300 + Math.floor(Math.random() * 99)).padStart(5, '0')}`,
    });
  });
}

export function deliverLabReport(
  patientId: string,
  reportId: string,
  idempotencyKey: string,
): Promise<{ reportId: string }> {
  return withIdem(idempotencyKey, () => {
    void patientId;
    return { reportId };
  });
}

// ── Medical history (purpose-gated) ──────────────────────────────────────────
// Records carry the non-encrypted scalars severity/icd10Code/startedDate/endedDate
// (matching MedicalHistoryDto) so the EDIT round-trip is exercisable flag-off — an
// edit that doesn't touch these must PRESERVE them (the PUT treats missing as null).
const HISTORY: Record<string, MedicalHistory[]> = {
  p1: [
    { historyId: 'h-1', recordType: 'chronic_condition', title: 'Hypertension', description: 'Diagnosed 2024; on Telmisartan', severity: 'moderate', icd10Code: 'I10', startedDate: '2024-02-14', endedDate: null, isActive: true, isCritical: false, addedAt: iso(420) },
    { historyId: 'h-2', recordType: 'allergy', title: 'Penicillin allergy', description: 'Rash on exposure', severity: 'severe', icd10Code: 'Z88.0', startedDate: null, endedDate: null, isActive: true, isCritical: true, addedAt: iso(900) },
    { historyId: 'h-3', recordType: 'surgery', title: 'Appendectomy', description: 'Laparoscopic, uneventful', severity: null, icd10Code: 'K35.80', startedDate: '2021-07-03', endedDate: '2021-07-05', isActive: false, isCritical: false, addedAt: iso(1800) },
  ],
};

export function listMedicalHistory(patientId: string, purpose: string | undefined): Promise<MedicalHistory[]> {
  requirePurpose(purpose);
  // Consent-gated (a denied read 403s → break-glass affordance, then re-fetch).
  requireClinicalConsent(patientId);
  return delay((HISTORY[patientId] ?? []).map((h) => MedicalHistorySchema.parse(h)));
}

/** Create a medical-history entry. Mutates the seed so the timeline reflects it
 *  after the list invalidates (flag-off parity). title/description are PHI. */
export function createMedicalHistory(
  patientId: string,
  req: CreateMedicalHistoryRequest,
  idempotencyKey: string,
): Promise<CreateMedicalHistoryResult> {
  return withIdem(idempotencyKey, () => {
    const historyId = crypto.randomUUID();
    const list = HISTORY[patientId] ?? (HISTORY[patientId] = []);
    list.unshift(
      MedicalHistorySchema.parse({
        historyId,
        recordType: req.recordType,
        title: req.title,
        description: req.description ?? null,
        severity: req.severity ?? null,
        icd10Code: req.icd10Code ?? null,
        startedDate: req.startedDate ?? null,
        endedDate: req.endedDate ?? null,
        isActive: true,
        isCritical: req.isCritical,
        addedAt: new Date().toISOString(),
      }),
    );
    return CreateMedicalHistoryResultSchema.parse({ historyId });
  });
}

/** Update (or retire, isActive=false) a medical-history entry. Returns true when
 *  the row was found + patched, false (404 in real) otherwise. */
export function updateMedicalHistory(
  patientId: string,
  historyId: string,
  req: UpdateMedicalHistoryRequest,
  idempotencyKey: string,
): Promise<boolean> {
  return withIdem(idempotencyKey, () => {
    const list = HISTORY[patientId];
    const row = list?.find((h) => h.historyId === historyId);
    if (!row) return false;
    // Mirror the real PUT: write exactly the body received. The panel sends
    // icd10Code/startedDate/endedDate back from the read, so they're PRESERVED
    // (a body omitting them would null them — the data-loss bug being closed).
    row.recordType = req.recordType;
    row.title = req.title;
    row.description = req.description ?? null;
    row.severity = req.severity ?? null;
    row.icd10Code = req.icd10Code ?? null;
    row.startedDate = req.startedDate ?? null;
    row.endedDate = req.endedDate ?? null;
    row.isActive = req.isActive;
    row.isCritical = req.isCritical;
    return true;
  });
}

// ── ABDM (consent-gated) ─────────────────────────────────────────────────────
const ABDM: Record<string, AbdmRecordListItem[]> = {
  p1: [
    { recordId: 'ab-1', recordType: 'DiagnosticReport', abhaNumber: '14-1234-5678-9012', isLinkedToPhr: true, createdAt: iso(5) },
    { recordId: 'ab-2', recordType: 'Prescription', abhaNumber: '14-1234-5678-9012', isLinkedToPhr: false, createdAt: iso(40) },
  ],
};

export function listAbdmRecords(patientId: string, purpose: string | undefined): Promise<AbdmRecordListItem[]> {
  requirePurpose(purpose);
  return delay((ABDM[patientId] ?? []).map((r) => AbdmRecordListItemSchema.parse(r)));
}

export function getAbdmRecord(recordId: string, patientId: string, purpose: string | undefined): Promise<AbdmRecordDetail> {
  requirePurpose(purpose);
  // ABDM additionally requires ACTIVE consent.
  const consent = CONSENT[patientId];
  if (consent && consent.abdmConsent !== 'granted') return Promise.reject(new ConsentRequiredError());
  return delay(
    AbdmRecordDetailSchema.parse({
      recordId,
      patientId,
      abhaNumber: '14-1234-5678-9012',
      recordType: 'DiagnosticReport',
      fhirResourceCount: 6,
      isLinkedToPhr: true,
      createdAt: iso(5),
    }),
  );
}

export function pushAbdmRecord(input: { patientId: string }, idempotencyKey: string): Promise<PushAbdmRecordResult> {
  return withIdem(idempotencyKey, () => {
    void input;
    return PushAbdmRecordResultSchema.parse({ recordId: crypto.randomUUID() });
  });
}

// ── Break-glass (emergency access) ───────────────────────────────────────────
// POST /security/break-glass → a grant id. After a successful grant the clinician
// can read a consent-denied record, so the UI re-fetches the gated read. In the
// mock we LIFT the relevant consent for the patient so the retried read succeeds,
// making the affordance demonstrable in flag-off mode. The justification is the
// clinician's typed reason (>=10 chars, enforced by the panel + the zod schema).
export function breakGlass(req: BreakGlassRequest, idempotencyKey: string): Promise<BreakGlassResult> {
  return withIdem(idempotencyKey, () => {
    const consent = CONSENT[req.patientId];
    if (consent) {
      // The grant lifts the gate that was blocking the read. ABDM records gate on
      // abdmConsent; the general clinical surface gates on clinicalConsent.
      if (req.resourceType === 'medical_history' || req.resourceType === 'prescription' || req.resourceType === 'lab_report') {
        consent.clinicalConsent = 'granted';
      }
      consent.abdmConsent = 'granted';
    }
    return BreakGlassResultSchema.parse({ grantId: crypto.randomUUID() });
  });
}
