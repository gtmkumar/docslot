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
  TenantDetailSchema,
  ForgotPasswordResultSchema,
  ResetPasswordResultSchema,
  AdminResetPasswordResultSchema,
  type BookingMutationResult,
  type TenantListItem,
  type TenantDetail,
  type UpdateTenantRequest,
  type ForgotPasswordRequest,
  type ForgotPasswordResult,
  type ResetPasswordRequest,
  type ResetPasswordResult,
  type AdminResetPasswordResult,
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

// Mock tenant edit (live mode hits PUT /tenants/{id}, super_admin only). Echoes the
// merged row back as a TenantDto so the panel's success path + toast are exercisable
// offline. Contact/display fields + geo only — lifecycle status is changed via
// suspend/reactivate. Echoes the fresh TenantDetail (merging the request onto the seed
// row) so the geo/city/etc. round-trip and the panel's detail cache re-syncs.
export async function updateTenantMock(
  tenantId: string,
  req: UpdateTenantRequest,
  _idempotencyKey: string,
): Promise<TenantDetail> {
  const detail = await getTenantMock(tenantId);
  return TenantDetailSchema.parse({
    ...detail,
    displayName: req.displayName,
    legalName: req.legalName,
    primaryEmail: req.primaryEmail,
    primaryPhone: req.primaryPhone,
    city: req.city ?? null,
    state: req.state ?? null,
    pinCode: req.pinCode ?? null,
    latitude: req.latitude ?? null,
    longitude: req.longitude ?? null,
  });
}

// Mock tenant lifecycle actions (live: PUT /tenants/{id}/suspend | /reactivate, gated
// platform.tenants.suspend). Each echoes the fresh TenantDetail with the new status +
// suspended_reason so the confirm flow + chip re-sync are exercisable offline.
export async function suspendTenantMock(tenantId: string, reason: string, _idempotencyKey: string): Promise<TenantDetail> {
  const detail = await getTenantMock(tenantId);
  return TenantDetailSchema.parse({ ...detail, status: 'suspended', suspendedReason: reason });
}

export async function reactivateTenantMock(tenantId: string, _reason: string | null, _idempotencyKey: string): Promise<TenantDetail> {
  const detail = await getTenantMock(tenantId);
  return TenantDetailSchema.parse({ ...detail, status: 'active', suspendedReason: null });
}

// ── PASSWORD RESET (mock) ──────────────────────────────────────────────────────
// forgot-password ALWAYS resolves { requested:true } (mirrors the live anti-enumeration
// contract — never reveals whether the email exists). reset-password "succeeds" for any
// non-empty token so the public page is demoable offline (the live endpoint 4xxs bad/
// expired tokens). adminResetUserPassword fabricates a one-time link + expiry so the
// admin copyable panel is exercisable with the flag off.
export function forgotPasswordMock(_req: ForgotPasswordRequest): Promise<ForgotPasswordResult> {
  return delay(ForgotPasswordResultSchema.parse({ requested: true }));
}

export function resetPasswordMock(req: ResetPasswordRequest): Promise<ResetPasswordResult> {
  if (!req.token.trim()) {
    return Promise.reject(new Error('This reset link is invalid or has expired.'));
  }
  return delay(ResetPasswordResultSchema.parse({ reset: true }));
}

export function adminResetUserPasswordMock(_userId: string, _idempotencyKey: string): Promise<AdminResetPasswordResult> {
  const expiresAt = new Date(Date.now() + 60 * 60 * 1000).toISOString(); // 1h
  const token = crypto.randomUUID().replace(/-/g, '');
  return delay(
    AdminResetPasswordResultSchema.parse({
      resetLink: `${window.location.origin}/reset-password?token=${token}`,
      expiresAt,
    }),
  );
}

// Mock tenant detail (live: GET /tenants/{id} → TenantDetailDto). Augments the matching
// static list row with placeholder legalName/primaryPhone/state/pinCode so the edit
// form's pre-fill path is exercisable offline.
export async function getTenantMock(tenantId: string): Promise<TenantDetail> {
  const list = await listTenantsMock();
  const row = list.find((tn) => tn.tenantId === tenantId);
  const suspended = row?.status === 'suspended';
  return TenantDetailSchema.parse({
    tenantId,
    tenantCode: row?.tenantCode ?? 'MOCK-CODE',
    displayName: row?.displayName ?? 'Mock Clinic',
    tenantType: row?.tenantType ?? 'hospital',
    legalName: `${row?.displayName ?? 'Mock Clinic'} Pvt Ltd`,
    primaryEmail: row?.primaryEmail ?? 'ops@clinic.in',
    primaryPhone: '+91 98200 11223',
    status: row?.status ?? 'active',
    country: row?.country ?? 'IN',
    city: row?.city ?? null,
    state: null,
    pinCode: null,
    suspendedReason: suspended ? 'Mock suspension reason' : null,
    latitude: null,
    longitude: null,
  });
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

/** Mock check-in (confirmed → checked_in). Mirrors the approve/cancel mock shape. */
export function checkInBookingMock(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return withIdem(idempotencyKey, () =>
    BookingMutationResultSchema.parse({ id: bookingId, status: 'checked_in' }),
  );
}

/** Mock reschedule: no server call (the prototype has no slot-supersede engine).
 *  Returns the same result shape the live endpoint does so the panel + hook are
 *  mode-blind (toast + invalidate + close); the mock list re-renders unchanged. */
export function rescheduleBookingMock(
  bookingId: string,
  _input: { newSlotId: string; newDoctorId?: string; reason?: string },
  idempotencyKey: string,
): Promise<{ oldBookingId: string; newBookingId: string; newBookingNumber: string | null; tokenNumber: number | null }> {
  return withIdem(idempotencyKey, () => ({
    oldBookingId: bookingId,
    newBookingId: `B-mock-${Date.now()}`,
    newBookingNumber: null,
    tokenNumber: null,
  }));
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
