// Static clinical vocabularies for the consultation composer (Phase A).
//
// These are DATA (a controlled clinical vocabulary — drug brands, diagnosis names,
// lab tests), not UI chrome, so the values are English medical terms that also
// appear verbatim on the printed Rx (exactly like the department/doctor names in
// lib/data). All surrounding UI labels/buttons/placeholders are bilingual via
// i18n; the doctor can additionally type free-text (incl. Hindi) anywhere. Phase C
// replaces these with per-doctor templates + favourites from the backend.

import type { MedTiming, StructuredMedication } from '@/lib/mock/contracts';

/** A formulary entry: one click adds a COMPLETE medication line (strength, form,
 *  default dose, timing, duration) the doctor can then tweak. */
export interface FormularyItem {
  id: string;
  name: string;
  generic: string;
  strength: string;
  form: string;
  dose: { morning: number; noon: number; night: number };
  sos?: boolean;
  weekly?: boolean;
  timing: MedTiming;
  durationDays?: number | null;
  instructions?: string | null;
}

// Sensible Indian brand examples with clinically typical defaults.
export const FORMULARY: FormularyItem[] = [
  { id: 'dolo650', name: 'Dolo 650', generic: 'Paracetamol', strength: '650 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 1 }, timing: 'after_food', durationDays: 5 },
  { id: 'augmentin625', name: 'Augmentin 625', generic: 'Amoxicillin + Clavulanic', strength: '625 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 1 }, timing: 'after_food', durationDays: 5 },
  { id: 'azithral500', name: 'Azithral 500', generic: 'Azithromycin', strength: '500 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 0 }, timing: 'before_food', durationDays: 3 },
  { id: 'pan40', name: 'Pan 40', generic: 'Pantoprazole', strength: '40 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 0 }, timing: 'empty_stomach', durationDays: 14 },
  { id: 'amlong5', name: 'Amlong 5', generic: 'Amlodipine', strength: '5 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 0 }, timing: 'anytime', durationDays: 30 },
  { id: 'emeset4', name: 'Emeset 4', generic: 'Ondansetron', strength: '4 mg', form: 'tab', dose: { morning: 0, noon: 0, night: 0 }, sos: true, timing: 'anytime', durationDays: 5, instructions: 'If vomiting' },
  { id: 'telma40', name: 'Telma 40', generic: 'Telmisartan', strength: '40 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 0 }, timing: 'anytime', durationDays: 30 },
  { id: 'calcirol', name: 'Calcirol', generic: 'Vitamin D3', strength: '60k', form: 'sachet', dose: { morning: 1, noon: 0, night: 0 }, weekly: true, timing: 'after_food', durationDays: 56, instructions: 'Once a week' },
  { id: 'shelcal500', name: 'Shelcal 500', generic: 'Calcium + Vitamin D3', strength: '500 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 0 }, timing: 'after_food', durationDays: 30 },
  { id: 'montek-lc', name: 'Montek LC', generic: 'Montelukast + Levocetirizine', strength: '10 mg', form: 'tab', dose: { morning: 0, noon: 0, night: 1 }, timing: 'after_food', durationDays: 7 },
  { id: 'zerodol-sp', name: 'Zerodol SP', generic: 'Aceclofenac + Serratiopeptidase', strength: '100 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 1 }, timing: 'after_food', durationDays: 5 },
  { id: 'metformin500', name: 'Glycomet 500', generic: 'Metformin', strength: '500 mg', form: 'tab', dose: { morning: 1, noon: 0, night: 1 }, timing: 'after_food', durationDays: 30 },
];

/** Map a formulary entry (or a bare name) to a complete structured medication. */
export function fromFormulary(item: FormularyItem): StructuredMedication {
  return {
    name: item.name,
    strength: item.strength,
    form: item.form,
    dose: { ...item.dose },
    sos: item.sos ?? false,
    weekly: item.weekly ?? false,
    timing: item.timing,
    durationDays: item.durationDays ?? null,
    instructions: item.instructions ?? null,
  };
}

/** A blank line seeded from a free-text search term (drug not in the formulary). */
export function blankMedication(name: string): StructuredMedication {
  return { name, strength: null, form: 'tab', dose: { morning: 1, noon: 0, night: 1 }, sos: false, weekly: false, timing: 'after_food', durationDays: 5, instructions: null };
}

// Common diagnoses (typeahead over these + free text).
export const DIAGNOSES: string[] = [
  'Viral fever',
  'Upper respiratory tract infection',
  'Acute gastroenteritis',
  'Hypertension',
  'Type 2 diabetes mellitus',
  'Acid peptic disease',
  'Migraine',
  'Allergic rhinitis',
  'Urinary tract infection',
  'Bronchitis',
  'Anaemia',
  'Hypothyroidism',
  'Osteoarthritis',
  'Dyslipidaemia',
];

// Lab tests & imaging (chip typeahead).
export const INVESTIGATIONS: string[] = [
  'CBC',
  'FBS / PPBS',
  'HbA1c',
  'Lipid profile',
  'LFT',
  'KFT',
  'TSH',
  'Urine R/M',
  'ECG',
  'Chest X-ray',
  'Serum electrolytes',
  'Vitamin D',
  'Vitamin B12',
  'CRP',
];

// Advice quick-chips (doctor may also type free-text advice).
export const ADVICE_CHIPS: string[] = [
  'Plenty of oral fluids',
  'Adequate rest',
  'Light, easily digestible diet',
  'Avoid oily & spicy food',
  'Steam inhalation',
  'Warm saline gargles',
  'Monitor temperature',
  'Low-salt diet',
  'Regular exercise',
  'Avoid self-medication',
];

/** Follow-up quick options → followUpInDays. Sentinels: `null` = not set (nothing
 *  shown); `0` = "SOS only" / as-needed (a valid selection); `>0` = after N days.
 *  Overloading 0 as the SOS sentinel lets the single number|null field round-trip
 *  the SOS selection through autosave. */
export const FOLLOW_UPS: { key: string; labelKey: string; days: number }[] = [
  { key: '3d', labelKey: 'consult.followUp.d3', days: 3 },
  { key: '1w', labelKey: 'consult.followUp.w1', days: 7 },
  { key: '15d', labelKey: 'consult.followUp.d15', days: 15 },
  { key: '1m', labelKey: 'consult.followUp.m1', days: 30 },
  { key: 'sos', labelKey: 'consult.followUp.sos', days: 0 },
];

/** A quick-start template MERGES its diagnosis + meds + advice + follow-up into the
 *  current draft (nothing is cleared; everything stays editable afterwards). */
export interface QuickTemplate {
  key: string;
  labelKey: string;
  emoji: string;
  diagnoses: string[];
  medItemIds: string[];
  advice: string[];
  followUpInDays: number | null;
  investigations?: string[];
}

export const TEMPLATES: QuickTemplate[] = [
  {
    key: 'viral_fever',
    labelKey: 'consult.template.viralFever',
    emoji: '🤒',
    diagnoses: ['Viral fever'],
    medItemIds: ['dolo650', 'emeset4'],
    advice: ['Plenty of oral fluids', 'Adequate rest'],
    followUpInDays: 3,
    investigations: ['CBC'],
  },
  {
    key: 'urti',
    labelKey: 'consult.template.urti',
    emoji: '🤧',
    diagnoses: ['Upper respiratory tract infection'],
    medItemIds: ['dolo650', 'azithral500', 'montek-lc'],
    advice: ['Steam inhalation', 'Warm saline gargles'],
    followUpInDays: 7,
  },
  {
    key: 'acid_peptic',
    labelKey: 'consult.template.acidPeptic',
    emoji: '🔥',
    diagnoses: ['Acid peptic disease'],
    medItemIds: ['pan40'],
    advice: ['Avoid oily & spicy food', 'Light, easily digestible diet'],
    followUpInDays: 15,
  },
  {
    key: 'htn_review',
    labelKey: 'consult.template.htnReview',
    emoji: '❤️',
    diagnoses: ['Hypertension'],
    medItemIds: ['telma40', 'amlong5'],
    advice: ['Low-salt diet', 'Regular exercise'],
    followUpInDays: 30,
    investigations: ['Lipid profile', 'KFT'],
  },
];
