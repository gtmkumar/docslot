// Shared zod schemas for booking forms. Lives in the feature folder; the Patient
// step uses this with react-hook-form, and the create mutation input is derived
// from it so validation is single-sourced.

import { z } from 'zod';

export const patientStepSchema = z.object({
  phone: z
    .string()
    .trim()
    .regex(/^\+?[0-9\s-]{8,16}$/, 'phone'),
  name: z.string().trim().min(1, 'name'),
  age: z.string().trim().optional().default(''),
  sex: z.enum(['F', 'M', 'O']).default('F'),
  lang: z.enum(['en', 'hi']).default('en'),
  reason: z.string().trim().optional().default(''),
});

export type PatientStep = z.infer<typeof patientStepSchema>;
