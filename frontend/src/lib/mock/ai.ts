// Mock adapter for the two AI-assist capabilities (no-show risk + triage). Shapes
// mirror NoShowRiskDto / TriageResultDto so the mock→real swap is a no-op for zod.
//
// Both are DETERMINISTIC and CLEARLY LABELLED as mock (`source: 'mock-ui'`) so the
// app works with VITE_USE_REAL_API off without ever implying a real model ran:
//   - no-show: band derived from a stable hash of the booking id (no randomness,
//     so the same booking always shows the same band on reload).
//   - triage: a tiny keyword classifier over the (PHI) complaint string. The
//     complaint is used ONLY to compute the result here and is NEVER persisted,
//     logged, or echoed back in the result (the result carries derived bands +
//     symptoms + suggested doctors, not the raw text).

import { DOCTORS } from '@/lib/data';
import {
  NoShowRiskSchema,
  OcrAnalyteSchema,
  OcrExtractionListSchema,
  OcrExtractionSchema,
  OcrExtractionSummarySchema,
  RagAnswerSchema,
  RagCitationSchema,
  RagStatusSchema,
  TriageResultSchema,
  type ExtractLabReportInput,
  type NoShowRisk,
  type OcrExtraction,
  type OcrExtractionList,
  type RagAnswer,
  type RagAskInput,
  type RagStatus,
  type RiskBand,
  type SuggestedDoctor,
  type TriageRequestInput,
  type TriageResult,
  type UrgencyBand,
} from './contracts';

const LATENCY = 220;
function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY));
}

/** Stable non-cryptographic hash of a string → unsigned 32-bit int. Used to pick a
 *  deterministic band from a booking id (same id → same band across reloads). */
function hash(value: string): number {
  let h = 2166136261;
  for (let i = 0; i < value.length; i += 1) {
    h ^= value.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return h >>> 0;
}

const NO_SHOW_BANDS: RiskBand[] = ['low', 'medium', 'high'];

/** Deterministic no-show risk from the booking id. probability is a stable 0..1
 *  value within the band's range so the "· NN%" label looks plausible. */
export function getNoShowRisk(bookingId: string): Promise<NoShowRisk> {
  const h = hash(bookingId);
  const band = NO_SHOW_BANDS[h % NO_SHOW_BANDS.length];
  // Keep the probability inside a band-appropriate window: low 0.05–0.30,
  // medium 0.35–0.60, high 0.65–0.92 — derived from the hash, no randomness.
  const within = (h % 1000) / 1000;
  const ranges: Record<RiskBand, [number, number]> = {
    low: [0.05, 0.3],
    medium: [0.35, 0.6],
    high: [0.65, 0.92],
  };
  const [lo, hi] = ranges[band];
  const probability = Math.round((lo + within * (hi - lo)) * 100) / 100;
  return delay(
    NoShowRiskSchema.parse({
      bookingId,
      available: true,
      probability,
      band,
      modelName: 'mock-noshow-v0',
      source: 'mock-ui',
    }),
  );
}

// ── Triage keyword classifier ────────────────────────────────────────────────
// Buckets are checked in priority order (emergency wins). Each rule contributes a
// band, optional red flags, surfaced symptoms, and a target specialization used to
// pick suggested doctors from the existing mock directory.
interface TriageRule {
  keywords: string[];
  band: UrgencyBand;
  redFlags: string[];
  symptoms: string[];
  /** Mock DOCTORS.spec to match for suggestions (case-insensitive). */
  spec: string | null;
}

const RULES: TriageRule[] = [
  {
    keywords: ['chest pain', 'chest', 'breath', 'breathing', 'short of breath', 'collapse', 'unconscious', 'severe bleeding'],
    band: 'emergency',
    redFlags: ['Chest pain / breathlessness — possible cardiac event', 'Escalate to emergency triage immediately'],
    symptoms: ['Chest discomfort', 'Shortness of breath'],
    spec: 'Cardiology',
  },
  {
    keywords: ['fever', 'cough', 'cold', 'sore throat', 'vomit', 'diarrhea', 'diarrhoea', 'body ache'],
    band: 'medium',
    redFlags: [],
    symptoms: ['Fever', 'Cough'],
    spec: 'General Medicine',
  },
  {
    keywords: ['rash', 'skin', 'itch', 'acne'],
    band: 'low',
    redFlags: [],
    symptoms: ['Skin complaint'],
    spec: 'Dermatology',
  },
  {
    keywords: ['ear', 'nose', 'throat', 'sinus'],
    band: 'low',
    redFlags: [],
    symptoms: ['ENT complaint'],
    spec: 'ENT',
  },
];

function pickDoctors(spec: string | null): SuggestedDoctor[] {
  const matches = spec
    ? DOCTORS.filter((d) => d.spec.toLowerCase() === spec.toLowerCase())
    : DOCTORS.filter((d) => d.spec === 'General Medicine');
  const chosen = (matches.length ? matches : DOCTORS).slice(0, 2);
  return chosen.map((d) => ({
    doctorId: d.id,
    fullName: d.name,
    specialization: d.spec,
    consultationFee: d.fee,
    // The mock directory carries a clock-time next slot ("11:30"); surface it as-is.
    nextAvailableSlot: d.next,
  }));
}

/** Mock triage. Classifies the complaint by keyword; defaults to a low-urgency
 *  General-Medicine suggestion when nothing matches. The complaint is consumed
 *  here ONLY — never stored or returned. */
export function submitTriage(input: TriageRequestInput): Promise<TriageResult> {
  const complaint = input.complaint.toLowerCase();
  const rule = RULES.find((r) => r.keywords.some((k) => complaint.includes(k)));
  const resolved = rule ?? {
    band: 'low' as UrgencyBand,
    redFlags: [] as string[],
    symptoms: ['General complaint'],
    spec: 'General Medicine',
  };
  return delay(
    TriageResultSchema.parse({
      available: true,
      urgencyBand: resolved.band,
      department: resolved.spec,
      redFlags: resolved.redFlags,
      symptoms: resolved.symptoms,
      suggestedDoctors: pickDoctors(resolved.spec),
      runId: null,
      source: 'mock-ui',
    }),
  );
}

// ── OCR lab-report extraction (Slice 11) ──────────────────────────────────────
// Deterministic stub: a fixed analyte panel whose values are derived from a stable
// hash of the patient (+booking) id, so the same patient yields the same extraction
// across reloads. The values ARE treated as PHI even in the mock (computed
// transiently, never persisted) and the result is clearly labelled `source:
// 'mock-ui'` so flag-off never implies a real OCR engine ran.

const OCR_PANEL: { test: string; unit: string; refLow: number; refHigh: number }[] = [
  { test: 'Hemoglobin', unit: 'g/dL', refLow: 13, refHigh: 17 },
  { test: 'WBC', unit: '10^3/µL', refLow: 4, refHigh: 11 },
  { test: 'Platelets', unit: '10^3/µL', refLow: 150, refHigh: 410 },
  { test: 'Fasting glucose', unit: 'mg/dL', refLow: 70, refHigh: 100 },
  { test: 'Creatinine', unit: 'mg/dL', refLow: 0.7, refHigh: 1.3 },
];

function flagFor(value: number, lo: number, hi: number): string {
  if (value < lo) return 'low';
  if (value > hi) return 'high';
  return 'normal';
}

/** Mock OCR extraction. The complaint-free, patient-bound PHI path: analyte values
 *  are derived from the patient id and returned ONLY in this result (never cached
 *  in a query key by the hook). */
export function extractLabReport(input: ExtractLabReportInput): Promise<OcrExtraction> {
  const h = hash(input.relatedPatientId + (input.relatedBookingId ?? ''));
  const analytes = OCR_PANEL.map((a, i) => {
    const span = a.refHigh - a.refLow;
    // Deterministic value in roughly [refLow - 15%span, refHigh + 15%span].
    const within = ((h >>> (i * 3)) % 1000) / 1000;
    const value = Math.round((a.refLow - span * 0.15 + within * span * 1.3) * 10) / 10;
    return OcrAnalyteSchema.parse({
      test: a.test,
      value,
      unit: a.unit,
      refLow: a.refLow,
      refHigh: a.refHigh,
      flag: flagFor(value, a.refLow, a.refHigh),
    });
  });
  const abnormalCount = analytes.filter((a) => a.flag !== 'normal').length;
  return delay(
    OcrExtractionSchema.parse({
      available: true,
      extractionId: `mock-ext-${(h % 100000).toString().padStart(5, '0')}`,
      ocrEngine: 'mock-ocr-v0',
      overallConfidence: Math.round((0.8 + (h % 20) / 100) * 100) / 100,
      requiresHumanReview: abnormalCount > 1,
      abnormalCount,
      analytes,
      source: 'mock-ui',
    }),
  );
}

// ── RAG ask over a patient's indexed medical history (Slice 11) ───────────────
// Deterministic, extractive-style stub. The QUESTION is PHI: it is read here ONLY
// to lightly shape the (still generic) answer and is NEVER stored, logged, or
// echoed back in the result. The answer + citations are derived from a stable hash
// of the patient id and labelled `source: 'mock-ui'` so flag-off never implies a
// real LLM/RAG ran.

const RAG_CITATIONS: { recordType: string; title: string; severity: string }[] = [
  { recordType: 'condition', title: 'Type 2 diabetes mellitus', severity: 'moderate' },
  { recordType: 'allergy', title: 'Penicillin allergy', severity: 'high' },
  { recordType: 'surgery', title: 'Appendectomy (2019)', severity: 'low' },
];

export function askPatientRag(input: RagAskInput): Promise<RagAnswer> {
  // Touch the question so a medication-themed ask gets a medication-themed answer,
  // but NEVER retain or echo it (PHI).
  const wantsMeds = /med|drug|dose|prescri|दवा/i.test(input.question);
  const h = hash(input.patientId);
  const citationCount = 1 + (h % RAG_CITATIONS.length);
  const citations = RAG_CITATIONS.slice(0, citationCount).map((c, i) =>
    RagCitationSchema.parse({
      historyId: `mock-hist-${(h >>> (i * 4)) % 9999}`,
      recordType: c.recordType,
      title: c.title,
      severity: c.severity,
      score: Math.round((0.6 + ((h >>> i) % 40) / 100) * 100) / 100,
    }),
  );
  const answer = wantsMeds
    ? 'Based on the indexed history, no active medication conflicts were detected. Review the cited records before prescribing.'
    : 'The indexed medical history surfaces the records cited below as most relevant to this question. This is an advisory summary — confirm against the source records.';
  return delay(
    RagAnswerSchema.parse({
      available: true,
      patientId: input.patientId,
      answer,
      mode: 'extractive',
      citations,
      retrieved: citations.length,
      source: 'mock-ui',
    }),
  );
}

// ── AI operational reads (non-PHI summaries) ──────────────────────────────────
// Deterministic, no-PHI summaries for the AI Operations screen — cacheable queries.

export function listAiExtractions(limit = 20): Promise<OcrExtractionList> {
  const count = Math.min(limit, 6);
  const rows = Array.from({ length: count }).map((_, i) => {
    const h = hash(`ext-${i}`);
    const requiresHumanReview = i % 3 === 0;
    return OcrExtractionSummarySchema.parse({
      extractionId: `mock-ext-${(h % 100000).toString().padStart(5, '0')}`,
      sourceType: 'lab_report',
      status: requiresHumanReview ? 'needs_review' : 'completed',
      overallConfidence: Math.round((0.75 + (h % 24) / 100) * 100) / 100,
      requiresHumanReview,
      abnormalCount: h % 4,
      createdAt: new Date(Date.now() - i * 3_600_000).toISOString(),
    });
  });
  return delay(OcrExtractionListSchema.parse({ available: true, extractions: rows, source: 'mock-ui' }));
}

export function getRagStatus(): Promise<RagStatus> {
  return delay(
    RagStatusSchema.parse({
      available: true,
      embeddings: 1240,
      patientsIndexed: 86,
      knowledgeBases: [
        { kbKey: 'patient_history', name: 'Patient medical history', documentCount: 612 },
        { kbKey: 'clinical_guidelines', name: 'Clinical guidelines', documentCount: 48 },
      ],
      source: 'mock-ui',
    }),
  );
}
