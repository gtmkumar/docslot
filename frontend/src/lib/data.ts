// Mock data for the DocSlot dashboard — hospital-primary, with org switching.
// Ported from the design prototype. This is the seam the real DocSlot.API will
// replace: feature hooks read from here today, from TanStack Query tomorrow.

import type {
  Booking,
  ChatMessage,
  Department,
  Doctor,
  Org,
  Patient,
  SlotCell,
} from './types';

export const ORGS: Org[] = [
  { id: 'apollo-andheri', name: 'Apollo Care · Andheri West', type: 'hospital', city: 'Mumbai', whatsappNumber: '+91 99201 50312', subscribers: 14820 },
  { id: 'dr-mehta', name: "Dr. Mehta's Cardiology", type: 'individual_doctor', city: 'Pune', whatsappNumber: '+91 87654 32109', subscribers: 1840 },
  { id: 'thyrocare-kandivli', name: 'Thyrocare Diagnostics · Kandivli', type: 'pathology_lab', city: 'Mumbai', whatsappNumber: '+91 99700 41234', subscribers: 8930 },
];

export const DEPARTMENTS: Department[] = [
  { id: 'card', name: 'Cardiology', color: '#C2563D', count: 6 },
  { id: 'ortho', name: 'Orthopedics', color: '#2A5C8A', count: 4 },
  { id: 'gyn', name: 'Gynaecology', color: '#7E4FB0', count: 5 },
  { id: 'ped', name: 'Paediatrics', color: '#1F5E50', count: 3 },
  { id: 'derm', name: 'Dermatology', color: '#A57F1B', count: 2 },
  { id: 'ent', name: 'ENT', color: '#496D40', count: 2 },
  { id: 'gen', name: 'General Medicine', color: '#5C6F69', count: 8 },
];

export const DOCTORS: Doctor[] = [
  { id: 'd1', name: 'Dr. Anjali Sharma', spec: 'Cardiology', deptId: 'card', qual: 'MD, DM Cardio', fee: 900, room: '302-A', rating: 4.9, today: 12, next: '11:30', img: 'AS', color: '#C2563D' },
  { id: 'd2', name: 'Dr. Rohan Iyer', spec: 'Cardiology', deptId: 'card', qual: 'MBBS, MD', fee: 750, room: '304', rating: 4.7, today: 9, next: '12:00', img: 'RI', color: '#C2563D' },
  { id: 'd3', name: 'Dr. Priya Nair', spec: 'Gynaecology', deptId: 'gyn', qual: 'MS, FRCOG', fee: 1100, room: '210', rating: 4.95, today: 14, next: '10:45', img: 'PN', color: '#7E4FB0' },
  { id: 'd4', name: 'Dr. Vikram Bose', spec: 'Orthopedics', deptId: 'ortho', qual: 'MS Ortho', fee: 850, room: '412', rating: 4.6, today: 7, next: '13:15', img: 'VB', color: '#2A5C8A' },
  { id: 'd5', name: 'Dr. Meera Krishnan', spec: 'Paediatrics', deptId: 'ped', qual: 'MD Paed', fee: 700, room: '108', rating: 4.85, today: 18, next: '10:30', img: 'MK', color: '#1F5E50' },
  { id: 'd6', name: 'Dr. Saurabh Gupta', spec: 'Dermatology', deptId: 'derm', qual: 'MD Derm', fee: 800, room: '215', rating: 4.7, today: 6, next: '14:00', img: 'SG', color: '#A57F1B' },
  { id: 'd7', name: 'Dr. Faisal Khan', spec: 'ENT', deptId: 'ent', qual: 'MS ENT', fee: 700, room: '318', rating: 4.5, today: 5, next: '15:30', img: 'FK', color: '#496D40' },
  { id: 'd8', name: 'Dr. Lakshmi Rao', spec: 'General Medicine', deptId: 'gen', qual: 'MD Gen Med', fee: 600, room: 'OPD-1', rating: 4.6, today: 21, next: '10:15', img: 'LR', color: '#5C6F69' },
];

// Phase 1: every Booking carries behalf/consent fields. Most are `self`/
// 'not_required'; a few are `behalf` with varied consent states so the manage
// panel's read-only surface + the Approve gate are exercisable in mock mode:
//   - B-2841 behalf/family, consent PENDING  → Approve disabled (awaiting consent)
//   - B-2839 behalf/care_partner, CONFIRMED  → Approve enabled
//   - B-2832 behalf/friend, DENIED           → Approve disabled
export const BOOKINGS: Booking[] = [
  { id: 'B-2841', token: 12, patient: 'Riya Kapoor', phone: '+91 98203 14572', age: 31, gender: 'F', doctorId: 'd1', doctorName: 'Dr. Anjali Sharma', dept: 'Cardiology', date: 'Today', time: '11:30', duration: 15, status: 'pending', source: 'whatsapp', note: 'Chest pain since morning. भूख नहीं लग रही.', createdAgo: '2 min ago', lang: 'hi', bookedByType: 'behalf', behalfRelation: 'family', patientConsentStatus: 'pending' },
  { id: 'B-2840', token: 11, patient: 'Aman Shah', phone: '+91 99205 88812', age: 42, gender: 'M', doctorId: 'd2', doctorName: 'Dr. Rohan Iyer', dept: 'Cardiology', date: 'Today', time: '12:00', duration: 15, status: 'pending', source: 'whatsapp', note: 'Follow-up — ECG report attached', createdAgo: '5 min ago', lang: 'en', bookedByType: 'self', behalfRelation: null, patientConsentStatus: 'not_required' },
  { id: 'B-2839', token: 14, patient: 'Sneha Reddy', phone: '+91 90415 22034', age: 28, gender: 'F', doctorId: 'd3', doctorName: 'Dr. Priya Nair', dept: 'Gynaecology', date: 'Today', time: '10:45', duration: 20, status: 'pending', source: 'whatsapp', note: 'First visit — antenatal', createdAgo: '12 min ago', lang: 'en', bookedByType: 'behalf', behalfRelation: 'care_partner', patientConsentStatus: 'confirmed' },
  { id: 'B-2838', token: 7, patient: 'Karan Mehta', phone: '+91 98701 56432', age: 8, gender: 'M', doctorId: 'd5', doctorName: 'Dr. Meera Krishnan', dept: 'Paediatrics', date: 'Today', time: '10:30', duration: 15, status: 'confirmed', source: 'whatsapp', note: 'Fever 102°F, cough', createdAgo: '32 min ago', lang: 'en', bookedByType: 'behalf', behalfRelation: 'family', patientConsentStatus: 'confirmed' },
  { id: 'B-2837', token: 18, patient: 'Pooja Singh', phone: '+91 99113 47720', age: 56, gender: 'F', doctorId: 'd4', doctorName: 'Dr. Vikram Bose', dept: 'Orthopedics', date: 'Today', time: '13:15', duration: 20, status: 'confirmed', source: 'walk_in', note: 'Knee pain — review', createdAgo: '1 hr ago', lang: 'en', bookedByType: 'self', behalfRelation: null, patientConsentStatus: 'not_required' },
  { id: 'B-2836', token: 6, patient: 'Aditya Pillai', phone: '+91 90008 11420', age: 34, gender: 'M', doctorId: 'd6', doctorName: 'Dr. Saurabh Gupta', dept: 'Dermatology', date: 'Today', time: '14:00', duration: 15, status: 'confirmed', source: 'whatsapp', note: 'Rash on forearms', createdAgo: '2 hr ago', lang: 'en', bookedByType: 'self', behalfRelation: null, patientConsentStatus: 'not_required' },
  { id: 'B-2835', token: 4, patient: 'Divya Joshi', phone: '+91 87012 90034', age: 39, gender: 'F', doctorId: 'd8', doctorName: 'Dr. Lakshmi Rao', dept: 'General Medicine', date: 'Today', time: '10:15', duration: 15, status: 'completed', source: 'whatsapp', note: 'Routine checkup', createdAgo: '3 hr ago', lang: 'en', bookedByType: 'self', behalfRelation: null, patientConsentStatus: 'not_required' },
  { id: 'B-2834', token: 3, patient: 'Nikhil Bhatt', phone: '+91 98671 22311', age: 50, gender: 'M', doctorId: 'd8', doctorName: 'Dr. Lakshmi Rao', dept: 'General Medicine', date: 'Today', time: '10:00', duration: 15, status: 'completed', source: 'phone_call', note: 'BP review', createdAgo: '3 hr ago', lang: 'en', bookedByType: 'self', behalfRelation: null, patientConsentStatus: 'not_required' },
  { id: 'B-2833', token: 2, patient: 'Tanvi Iyer', phone: '+91 90120 56781', age: 26, gender: 'F', doctorId: 'd3', doctorName: 'Dr. Priya Nair', dept: 'Gynaecology', date: 'Today', time: '09:45', duration: 20, status: 'no_show', source: 'whatsapp', note: '—', createdAgo: '4 hr ago', lang: 'en', bookedByType: 'self', behalfRelation: null, patientConsentStatus: 'not_required' },
  { id: 'B-2832', token: 9, patient: 'Harsh Patel', phone: '+91 99882 03114', age: 19, gender: 'M', doctorId: 'd7', doctorName: 'Dr. Faisal Khan', dept: 'ENT', date: 'Today', time: '15:30', duration: 15, status: 'pending', source: 'whatsapp', note: 'Throat infection — kal se', createdAgo: '18 min ago', lang: 'hi', bookedByType: 'behalf', behalfRelation: 'friend', patientConsentStatus: 'denied' },
  { id: 'B-2831', token: 1, patient: 'Meera Joshi', phone: '+91 87760 12039', age: 64, gender: 'F', doctorId: 'd1', doctorName: 'Dr. Anjali Sharma', dept: 'Cardiology', date: 'Today', time: '09:30', duration: 15, status: 'completed', source: 'whatsapp', note: 'Hypertension review', createdAgo: '5 hr ago', lang: 'en', bookedByType: 'self', behalfRelation: null, patientConsentStatus: 'not_required' },
];

export const CONVERSATIONS: Record<string, ChatMessage[]> = {
  'B-2841': [
    { from: 'patient', text: 'Hello, I need to see a heart doctor today', at: '11:02 AM' },
    { from: 'bot', text: 'नमस्ते Riya 🏥 Welcome to Apollo Care, Andheri West.\nWhich department?', at: '11:02 AM', interactive: ['Cardiology', 'Gynaecology', 'Paediatrics', 'More…'] },
    { from: 'patient', text: 'Cardiology', at: '11:03 AM' },
    { from: 'bot', text: 'Choose a doctor:', at: '11:03 AM', interactive: ['Dr. Anjali Sharma · ₹900', 'Dr. Rohan Iyer · ₹750'] },
    { from: 'patient', text: 'Dr. Anjali Sharma · ₹900', at: '11:03 AM' },
    { from: 'bot', text: 'Available today:', at: '11:04 AM', interactive: ['11:30 AM', '11:45 AM', '12:15 PM'] },
    { from: 'patient', text: '11:30 AM', at: '11:04 AM' },
    { from: 'bot', text: 'Almost done. Any symptoms to share with the doctor?', at: '11:04 AM' },
    { from: 'patient', text: 'Chest pain since morning. भूख नहीं लग रही.', at: '11:05 AM' },
    { from: 'bot', text: 'Got it. Confirm appointment with Dr. Anjali Sharma at 11:30 AM today? Token #12.', at: '11:05 AM', interactive: ['✅ Confirm', 'Change slot', 'Cancel'] },
    { from: 'patient', text: '✅ Confirm', at: '11:05 AM' },
    { from: 'bot', text: "Booked. Awaiting reception approval — you'll get a confirmation shortly.", at: '11:05 AM', system: true },
  ],
};

export const DAYS = ['Mon 21', 'Tue 22', 'Wed 23', 'Thu 24', 'Fri 25', 'Sat 26', 'Sun 27'];
export const TIMES = ['09:00', '09:30', '10:00', '10:30', '11:00', '11:30', '12:00', '12:30', '13:00', '14:00', '14:30', '15:00', '15:30', '16:00', '16:30', '17:00', '17:30', '18:00'];

// Deterministic load grid for the calendar visualisation.
export function buildSlotGrid(): Record<string, SlotCell[]> {
  const grid: Record<string, SlotCell[]> = {};
  TIMES.forEach((t, ti) => {
    grid[t] = DAYS.map((_d, di) => {
      const seed = (ti * 31 + di * 7 + 13) % 100;
      if (t === '13:00') return { state: 'off', booked: 0, cap: 0 };
      if (di === 6 && ti < 4) return { state: 'off', booked: 0, cap: 0 };
      if (seed < 12) return { state: 'blocked', booked: 0, cap: 4 };
      const cap = 4;
      const booked = Math.min(cap, Math.floor(seed / 22));
      let state: SlotCell['state'] = 'open';
      if (booked === cap) state = 'full';
      else if (booked >= cap - 1) state = 'tight';
      return { state, booked, cap };
    });
  });
  return grid;
}

export const PATIENTS: Patient[] = [
  { id: 'p1', name: 'Riya Kapoor', phone: '+91 98203 14572', age: 31, lang: 'hi', visits: 4, lastVisit: 'Today', spend: 3200 },
  { id: 'p2', name: 'Aman Shah', phone: '+91 99205 88812', age: 42, lang: 'en', visits: 11, lastVisit: 'Today', spend: 9450 },
  { id: 'p3', name: 'Sneha Reddy', phone: '+91 90415 22034', age: 28, lang: 'en', visits: 2, lastVisit: 'Today', spend: 2200 },
  { id: 'p4', name: 'Karan Mehta', phone: '+91 98701 56432', age: 8, lang: 'en', visits: 3, lastVisit: 'Today', spend: 2100, guardian: 'Pranav Mehta' },
  { id: 'p5', name: 'Pooja Singh', phone: '+91 99113 47720', age: 56, lang: 'en', visits: 8, lastVisit: 'Today', spend: 6800 },
  { id: 'p6', name: 'Aditya Pillai', phone: '+91 90008 11420', age: 34, lang: 'en', visits: 1, lastVisit: 'Today', spend: 800 },
  { id: 'p7', name: 'Divya Joshi', phone: '+91 87012 90034', age: 39, lang: 'en', visits: 6, lastVisit: 'Today', spend: 4200 },
  { id: 'p8', name: 'Nikhil Bhatt', phone: '+91 98671 22311', age: 50, lang: 'en', visits: 14, lastVisit: 'Today', spend: 11200 },
  { id: 'p9', name: 'Tanvi Iyer', phone: '+91 90120 56781', age: 26, lang: 'en', visits: 1, lastVisit: 'Today', spend: 0 },
  { id: 'p10', name: 'Harsh Patel', phone: '+91 99882 03114', age: 19, lang: 'hi', visits: 2, lastVisit: 'Today', spend: 1400 },
  { id: 'p11', name: 'Meera Joshi', phone: '+91 87760 12039', age: 64, lang: 'en', visits: 22, lastVisit: 'Today', spend: 18900 },
  { id: 'p12', name: 'Suresh Kulkarni', phone: '+91 99876 04123', age: 71, lang: 'en', visits: 9, lastVisit: 'Yesterday', spend: 7700 },
];
