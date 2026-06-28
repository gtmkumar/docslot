// Domain types for the DocSlot reception-desk dashboard.
// These mirror the mock data shapes; when the .NET API lands they become the
// DTO contracts consumed by TanStack Query.

export type OrgType = 'hospital' | 'individual_doctor' | 'pathology_lab';

export interface Org {
  id: string;
  name: string;
  type: OrgType;
  city: string;
  whatsappNumber: string;
  subscribers: number;
}

export interface Department {
  id: string;
  name: string;
  color: string;
  count: number;
}

export interface Doctor {
  id: string;
  name: string;
  spec: string;
  deptId: string;
  qual: string;
  fee: number;
  room: string;
  rating: number;
  today: number;
  next: string;
  img: string;
  color: string;
}

// Canonical booking status tokens (mirror the SQL CHECK constraint — snake_case).
// Display labels live in i18n (status.*), never in the wire enum.
// `checked_in` (Phase 1): a confirmed patient who has physically arrived at the
// desk (confirmed → checked_in via POST /bookings/{id}/check-in).
export type BookingStatus =
  | 'pending'
  | 'confirmed'
  | 'checked_in'
  | 'cancelled'
  | 'completed'
  | 'no_show'
  | 'rescheduled';
// Canonical booked_via tokens (snake_case). Display labels live in i18n (source.*).
export type BookingSource = 'whatsapp' | 'dashboard' | 'api' | 'walk_in' | 'phone_call';
export type Lang = 'en' | 'hi' | 'mr';

// Who created the booking (Phase 1). `behalf` = a third party booked for the
// patient (e.g. via WhatsApp), so patient consent gating applies. Display labels
// live in i18n (behalf.*), never in the wire enum.
export type BookedByType = 'self' | 'behalf';
// Relationship of the person who booked to the patient (when bookedByType==='behalf').
// Mirrors the SQL CHECK; display labels live in i18n (behalf.relation.*).
export type BehalfRelation = 'family' | 'friend' | 'neighbour' | 'care_partner' | 'other';
// Patient-consent lifecycle for a behalf booking (Phase 1). `not_required` for
// self bookings; the others track the OTP-consent flow. Display labels live in
// i18n (consent.*). Approve is gated on this server-side and mirrored in the UI.
export type PatientConsentStatus = 'not_required' | 'pending' | 'confirmed' | 'denied' | 'expired';

export interface Booking {
  id: string;
  token: number;
  patient: string;
  phone: string;
  age: number;
  gender: 'F' | 'M' | 'O';
  doctorId: string;
  doctorName: string;
  dept: string;
  date: string;
  time: string;
  duration: number;
  status: BookingStatus;
  source: BookingSource;
  note: string;
  createdAgo: string;
  lang: Lang;
  // Behalf / consent (Phase 1, read-only surface). For a `self` booking,
  // bookedByType==='self', behalfRelation is null, and patientConsentStatus is
  // 'not_required'. For a `behalf` booking these reflect the relationship + the
  // OTP-consent state; the manage panel surfaces them and gates Approve.
  bookedByType: BookedByType;
  behalfRelation: BehalfRelation | null;
  patientConsentStatus: PatientConsentStatus;
}

export interface ChatMessage {
  from: 'patient' | 'bot';
  text: string;
  at: string;
  interactive?: string[];
  system?: boolean;
}

export interface SlotCell {
  state: 'open' | 'tight' | 'full' | 'blocked' | 'off';
  booked: number;
  cap: number;
}

export interface Patient {
  id: string;
  name: string;
  phone: string;
  age: number;
  lang: Lang;
  visits: number;
  lastVisit: string;
  spend: number;
  guardian?: string;
}
