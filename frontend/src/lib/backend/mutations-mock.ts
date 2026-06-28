// Mock-side fallbacks for mutations that have a LIVE endpoint but no pre-existing
// mock implementation (add-patient, complete, no-show). These preserve the mock
// app's behavior byte-for-byte: in mock mode AddPatientPanel previously just
// toasted + closed without a server call, so the mock addPatient resolves with a
// synthetic id and does nothing else. complete/no-show mirror the existing
// approve/cancel mock result shape. The existing approveBooking / cancelBooking /
// createBooking / getAnalytics mocks live in lib/mock and are reused directly by
// the facade — only the genuinely-new functions live here.

import {
  BookingMutationResultSchema,
  TenantListItemSchema,
  type BookingMutationResult,
  type TenantListItem,
} from '@/lib/mock/contracts';
import { BOOKINGS } from '@/lib/data';
import type { Booking } from '@/lib/types';

const LATENCY = 180;
function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY));
}

// Mock target-tenant list for the begin-impersonation picker (live mode hits
// GET /tenants, super_admin only). A handful of distinct tenants so the selector
// is exercisable in mock mode.
export function listTenantsMock(): Promise<TenantListItem[]> {
  return delay(
    TenantListItemSchema.array().parse([
      { tenantId: '11111111-1111-1111-1111-111111111111', tenantCode: 'APOLLO-AND', displayName: 'Apollo Care · Andheri West', tenantType: 'hospital', status: 'active', country: 'IN', city: 'Mumbai' },
      { tenantId: '22222222-2222-2222-2222-222222222222', tenantCode: 'CITYLAB-BLR', displayName: 'CityLab Diagnostics · Bengaluru', tenantType: 'diagnostic_lab', status: 'active', country: 'IN', city: 'Bengaluru' },
      { tenantId: '33333333-3333-3333-3333-333333333333', tenantCode: 'SUNRISE-DEL', displayName: 'Sunrise Clinic · New Delhi', tenantType: 'clinic', status: 'active', country: 'IN', city: 'New Delhi' },
    ]),
  );
}

// Idempotent replay cache, mirroring the lib/mock withIdem helper.
const idemCache = new Map<string, unknown>();
function withIdem<T>(key: string, compute: () => T): Promise<T> {
  if (idemCache.has(key)) return delay(idemCache.get(key) as T);
  const result = compute();
  idemCache.set(key, result);
  return delay(result);
}

export function completeBookingMock(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return withIdem(idempotencyKey, () =>
    BookingMutationResultSchema.parse({ id: bookingId, status: 'completed' }),
  );
}

export function noShowBookingMock(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return withIdem(idempotencyKey, () =>
    BookingMutationResultSchema.parse({ id: bookingId, status: 'no_show' }),
  );
}

/** Mock booking-detail: resolve the full Booking from the prototype seam by id,
 *  matching the prior `BOOKINGS.find` behavior the screens used inline. Rejects on
 *  an unknown id so the panel surfaces an error state (parity with the live 404). */
export function getBookingMock(bookingId: string): Promise<Booking> {
  const full = BOOKINGS.find((b) => b.id === bookingId);
  return new Promise((resolve, reject) =>
    setTimeout(() => (full ? resolve(full) : reject(new Error('Booking not found'))), LATENCY),
  );
}

/** Mock add-doctor: no server call (matches the prior mock panel's "saved" toast);
 *  returns a synthetic id so callers don't branch on mode. */
export function addDoctorMock(
  input: {
    fullName: string;
    departmentId: string | null;
    specialization: string | null;
    qualifications: string[];
    consultationFee: number | null;
    phone: string | null;
    idempotencyKey: string;
  },
): Promise<{ doctorId: string; fullName: string; departmentId: string | null }> {
  return withIdem(input.idempotencyKey, () => ({
    doctorId: `D-mock-${Date.now()}`,
    fullName: input.fullName,
    departmentId: input.departmentId,
  }));
}

/** Mock approve-rule: no pre-existing mock fn (the live endpoint is /rules/{id}/
 *  approve → 204). Mirrors the other commission 204 shapes — resolves { id }. Only
 *  used if the UI exposes a rule-approve action; otherwise inert. */
export function approveRuleMock(ruleId: string, idempotencyKey: string): Promise<{ id: string }> {
  return withIdem(idempotencyKey, () => ({ id: ruleId }));
}

/** Mock add-patient: no server call (matches the prior mock panel behavior); the
 *  panel toasts + closes on success. Returns a synthetic id so callers don't
 *  branch on mode. */
export function addPatientMock(
  input: { phone: string; name: string; age: string; lang: 'en' | 'hi'; idempotencyKey: string },
): Promise<{ patientId: string; alreadyExisted: boolean }> {
  return withIdem(input.idempotencyKey, () => ({
    patientId: `P-mock-${Date.now()}`,
    alreadyExisted: false,
  }));
}
