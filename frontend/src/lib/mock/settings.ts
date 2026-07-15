// Mock adapter for the Workspace Settings screen (Phase 1). Shapes mirror
// mediq.SharedDataModel/Docslot/Settings/SettingsDtos.cs, so the mock→real swap is a
// no-op for zod.
//
// INVARIANTS baked in:
//  - NO PHI. Tenant configuration only (facility identity, hours, booking rules, the
//    WhatsApp connection STATUS — never its access token).
//  - The settings object is MUTABLE in-memory so a refetch after a PATCH reflects the
//    change — same as the real endpoint persisting to the facility row.
//  - PATCH REPLACES a section wholesale (not a diff): a supplied businessHours /
//    appointmentSettings overwrites that section; an omitted section is untouched.
//  - Seeded with realistic defaults (weekday 09:00–17:00, Sat 09:00–13:00, Sun closed;
//    a connected WhatsApp number; HFR unlinked) so the demo exercises every state.

import {
  SettingsSchema,
  type Settings,
  type UpdateSettingsRequest,
} from './contracts';

const LATENCY = 200;
function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY));
}

// A connected WhatsApp number so the "Connected · verified" chip + copy actions render
// flag-off. The token is NEVER modelled — the DTO omits it and so does the demo.
let SETTINGS: Settings = SettingsSchema.parse({
  facilityType: 'Multi-specialty clinic',
  specialtyFocus: 'Cardiology · Orthopaedics · Paediatrics',
  businessHours: {
    mon: { open: '09:00', close: '17:00', closed: false },
    tue: { open: '09:00', close: '17:00', closed: false },
    wed: { open: '09:00', close: '17:00', closed: false },
    thu: { open: '09:00', close: '17:00', closed: false },
    fri: { open: '09:00', close: '17:00', closed: false },
    sat: { open: '09:00', close: '13:00', closed: false },
    sun: { open: null, close: null, closed: true },
  },
  appointmentSettings: {
    slotDurationMinutes: 15,
    bookingCutoffHours: 2,
    autoConfirm: true,
    maxAdvanceDays: 30,
    allowOverbooking: false,
    reminderHoursBefore: 24,
    noShowGraceMinutes: 15,
  },
  // `whatsApp` (capital A) mirrors the live wire — see the SettingsSchema wire note.
  whatsApp: {
    connected: true,
    phoneNumberId: '109350958516789',
    verifiedAt: new Date(Date.now() - 96 * 3_600_000).toISOString(),
  },
  hfr: { id: null, status: null },
});

export function getSettings(): Promise<Settings> {
  return delay(SettingsSchema.parse(SETTINGS));
}

export function updateSettings(req: UpdateSettingsRequest): Promise<Settings> {
  // Full-section replace, mirroring the real handler: a supplied section overwrites the
  // stored one; an omitted section is left as-is. Re-read + re-validate before returning.
  SETTINGS = SettingsSchema.parse({
    ...SETTINGS,
    ...(req.businessHours ? { businessHours: req.businessHours } : {}),
    ...(req.appointmentSettings ? { appointmentSettings: req.appointmentSettings } : {}),
  });
  return delay(SettingsSchema.parse(SETTINGS));
}
