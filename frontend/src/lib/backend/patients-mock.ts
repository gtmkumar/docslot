// Mock-side patients list. The PatientsScreen previously read lib/data PATIENTS
// directly; to give it a uniform query seam (so the live /patients swap is a
// no-op) we expose the same PatientRow[] the real adapter produces, derived from
// the existing seed. PHI: masked phone only — identical to what the screen
// rendered before.

import { PATIENTS } from '@/lib/data';
import { maskPhone } from '@/lib/format';
import { PatientRowSchema, type PatientRow } from '@/lib/mock/contracts';

const LATENCY = 180;

export function listPatientsMock(): Promise<PatientRow[]> {
  const rows: PatientRow[] = PATIENTS.map((p) =>
    PatientRowSchema.parse({
      id: p.id,
      name: p.name,
      maskedPhone: maskPhone(p.phone),
      age: p.age,
      gender: null, // the prototype seed has no gender field
      preferredLanguage: p.lang,
    }),
  );
  return new Promise((resolve) => setTimeout(() => resolve(rows), LATENCY));
}
