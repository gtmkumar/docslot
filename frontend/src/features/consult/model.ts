// Editable form model for the composer + serialisation to/from the wire shapes.
// The composer edits a rich local form; autosave serialises it to a
// SaveConsultationRequest (structured meds → medicationsJson string), and a loaded
// draft hydrates back into the form. Diagnoses render as chips (stored as a joined
// string), advice as chips + free-text (stored newline-joined). followUpInDays uses
// the shared sentinels: null = not set, 0 = SOS only, >0 = after N days.

import { isLegacyMedication } from '@/lib/mock/contracts';
import type {
  ConsultationDraft,
  RxMedication,
  SaveConsultationRequest,
  StructuredMedication,
  Vitals,
} from '@/lib/mock/contracts';

export interface VitalsForm {
  bp: string;
  pulse: string;
  temp: string;
  spo2: string;
  weight: string;
}

export interface ConsultForm {
  vitals: VitalsForm;
  diagnoses: string[];
  medications: StructuredMedication[];
  investigations: string[];
  adviceChips: string[];
  adviceText: string;
  followUpInDays: number | null;
}

/** Coerce any medication (structured or legacy free-text) to the structured shape
 *  the editor manipulates. Legacy rows (from a repeated old prescription) fold their
 *  free-text dose/frequency/duration into a sensible structured default + note. */
export function toStructured(med: RxMedication): StructuredMedication {
  if (isLegacyMedication(med)) {
    return {
      name: med.name,
      strength: med.dose || null,
      form: 'tab',
      dose: { morning: 1, noon: 0, night: 1 },
      sos: false,
      weekly: false,
      timing: 'after_food',
      durationDays: null,
      instructions: [med.frequency, med.duration].filter(Boolean).join(' · ') || null,
    };
  }
  return med;
}

function toNumber(s: string): number | null {
  const t = s.trim();
  if (!t) return null;
  const n = Number(t);
  return Number.isFinite(n) ? n : null;
}

export function draftToForm(d: ConsultationDraft): ConsultForm {
  return {
    vitals: {
      bp: d.vitals.bp ?? '',
      pulse: d.vitals.pulseBpm != null ? String(d.vitals.pulseBpm) : '',
      temp: d.vitals.tempF != null ? String(d.vitals.tempF) : '',
      spo2: d.vitals.spo2 != null ? String(d.vitals.spo2) : '',
      weight: d.vitals.weightKg != null ? String(d.vitals.weightKg) : '',
    },
    diagnoses: d.diagnosis ? d.diagnosis.split(',').map((s) => s.trim()).filter(Boolean) : [],
    medications: d.medications.map(toStructured),
    investigations: [...d.investigations],
    adviceChips: d.advice ? d.advice.split('\n').map((s) => s.trim()).filter(Boolean) : [],
    adviceText: '',
    followUpInDays: d.followUpInDays,
  };
}

export function formToSave(f: ConsultForm): SaveConsultationRequest {
  const vitals: Vitals = {
    bp: f.vitals.bp.trim() || null,
    pulseBpm: toNumber(f.vitals.pulse),
    tempF: toNumber(f.vitals.temp),
    spo2: toNumber(f.vitals.spo2),
    weightKg: toNumber(f.vitals.weight),
  };
  const advice = [...f.adviceChips, f.adviceText.trim()].map((s) => s.trim()).filter(Boolean).join('\n');
  return {
    vitals,
    diagnosis: f.diagnoses.length ? f.diagnoses.join(', ') : null,
    medicationsJson: JSON.stringify(f.medications),
    investigations: f.investigations,
    advice: advice || null,
    followUpInDays: f.followUpInDays,
  };
}

/** The empty form (before a draft loads). */
export const EMPTY_FORM: ConsultForm = {
  vitals: { bp: '', pulse: '', temp: '', spo2: '', weight: '' },
  diagnoses: [],
  medications: [],
  investigations: [],
  adviceChips: [],
  adviceText: '',
  followUpInDays: null,
};
