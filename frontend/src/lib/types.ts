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
export type BookingStatus = 'pending' | 'confirmed' | 'cancelled' | 'completed' | 'no_show' | 'rescheduled';
// Canonical booked_via tokens (snake_case). Display labels live in i18n (source.*).
export type BookingSource = 'whatsapp' | 'dashboard' | 'api' | 'walk_in' | 'phone_call';
export type Lang = 'en' | 'hi' | 'mr';

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
