// LIVE .NET API implementations, behind the VITE_USE_REAL_API flag (see ./flag).
//
// These mirror the mock seam's function SIGNATURES exactly so lib/backend can
// pick real-vs-mock per function with zero changes in feature code. Each fn:
//   1. calls apiFetch (same-origin `/api/v1/...`, Bearer token attached by the
//      api-client from the live session),
//   2. zod-parses the RAW success DTO (no wrapper),
//   3. ADAPTS it into the existing app-facing shape the screens already consume
//      (BookingRow / PatientRow / DoctorCard / DashboardSummary / …).
//
// Errors: the .NET API wraps failures as
//   { status:false, message:{ errorTypeCode, errorMessage, responseMessage } }.
// apiFetch throws ApiError(status, _, body); toUserError below pulls
// message.responseMessage for display. Auth errors are re-mapped to the existing
// bilingual i18n keys so LoginScreen stays unchanged.

import { ApiError, apiFetch } from '@/lib/api-client';
import { inr, maskPhone } from '@/lib/format';
import { MockApiError } from '@/lib/mock';
import {
  AnalyticsDtoSchema,
  AnalyticsSchema,
  AttributionSchema,
  BadgesResponseSchema,
  BookingActionResultDtoSchema,
  BookingListItemDtoSchema,
  BookingRowSchema,
  BrokerSchema,
  CalendarGridSchema,
  CommissionCreatedSchema,
  CommissionRuleSchema,
  CreateBookingResultDtoSchema,
  CreateDoctorResultDtoSchema,
  DashboardSummaryDtoSchema,
  DisputeSchema,
  DoctorDtoSchema,
  MeSchema,
  MenusResponseSchema,
  PatientListItemDtoSchema,
  PayoutActionResultSchema,
  PayoutSchema,
  PermissionsResponseSchema,
  RegisterBrokerResultSchema,
  RegisterPatientResultDtoSchema,
  SlotDtoSchema,
  SlotSchema,
  TokenResponseSchema,
  type Analytics,
  type AnalyticsDto,
  type Attribution,
  type BadgesResponse,
  type BookingMutationResult,
  type BookingRow,
  type Broker,
  type CalendarGrid,
  type CommissionCreated,
  type CommissionRule,
  type CreateBookingResult,
  type CreateCommissionRuleRequest,
  type DashboardSummary,
  type Dispute,
  type DoctorCard,
  type LoginRequest,
  type Me,
  type MenusResponse,
  type PatientRow,
  type Payout,
  type PayoutActionResult,
  type PermissionsResponse,
  type Practitioner,
  type RaiseDisputeRequest,
  type RegisterBrokerRequest,
  type RegisterBrokerResult,
  type ResolveDisputeRequest,
  type Slot,
  type TokenResponse,
} from '@/lib/mock/contracts';
import type { Booking, BookingSource, BookingStatus, Lang } from '@/lib/types';

// ── Error mapping ─────────────────────────────────────────────────────────────

/** Pull the user-facing message from the wrapped .NET error envelope. */
export function toUserError(e: unknown): string {
  if (e instanceof ApiError) {
    const body = e.body as
      | { message?: { responseMessage?: string; errorMessage?: string } }
      | undefined;
    return (
      body?.message?.responseMessage ??
      body?.message?.errorMessage ??
      e.message
    );
  }
  return e instanceof Error ? e.message : 'Request failed';
}

/** Re-map an auth failure to an existing bilingual i18n key the LoginScreen
 *  already renders (423 → locked, everything else → invalid). The server's raw
 *  responseMessage is preserved on the thrown error for diagnostics. */
function asAuthError(e: unknown): MockApiError {
  const status = e instanceof ApiError ? e.status : 401;
  const key = status === 423 ? 'auth.error.locked' : 'auth.error.invalid';
  const err = new MockApiError(status, key);
  // Attach the server detail without breaking the i18n-keyed contract.
  (err as MockApiError & { detail?: string }).detail = toUserError(e);
  return err;
}

// ── AUTH ──────────────────────────────────────────────────────────────────────

export async function login(req: LoginRequest): Promise<TokenResponse> {
  try {
    const raw = await apiFetch<unknown>('/auth/login', {
      method: 'POST',
      body: { email: req.email, password: req.password, tenantId: req.tenantId ?? null },
    });
    return TokenResponseSchema.parse(raw);
  } catch (e) {
    throw asAuthError(e);
  }
}

export async function refresh(refreshToken: string): Promise<TokenResponse> {
  const raw = await apiFetch<unknown>('/auth/refresh', {
    method: 'POST',
    body: { refreshToken },
  });
  return TokenResponseSchema.parse(raw);
}

export async function logout(refreshToken?: string): Promise<void> {
  // 204 No Content — apiFetch returns undefined; ignore any benign failure on
  // sign-out (the client clears its session regardless).
  await apiFetch<void>('/auth/logout', {
    method: 'POST',
    body: { refreshToken: refreshToken ?? null },
  }).catch(() => undefined);
}

export async function getMe(): Promise<Me> {
  const raw = await apiFetch<unknown>('/me');
  return MeSchema.parse(raw);
}

// ── PERMISSIONS ────────────────────────────────────────────────────────────────

export async function getPermissions(): Promise<PermissionsResponse> {
  const raw = await apiFetch<unknown>('/me/permissions');
  return PermissionsResponseSchema.parse(raw);
}

// ── MENUS (backend-driven nav) ──────────────────────────────────────────────────

/** Normalize a server route to a SPA route. The API serves `/dashboard` for the
 *  overview but the app's index route is `/`. Other unknown routes are returned
 *  as-is (they 404 gracefully until the SPA grows the screen). */
function normalizeRoute(route: string | null): string | null {
  if (route === '/dashboard') return '/';
  return route;
}

function normalizeMenuRoutes(nodes: MenusResponse): MenusResponse {
  return nodes.map((n) => ({
    ...n,
    route: normalizeRoute(n.route),
    children: n.children.length ? normalizeMenuRoutes(n.children) : n.children,
  }));
}

export async function getMenus(): Promise<MenusResponse> {
  const raw = await apiFetch<unknown>('/me/menus');
  const tree = MenusResponseSchema.parse(raw);
  return normalizeMenuRoutes(tree);
}

// ── BADGES ──────────────────────────────────────────────────────────────────────
// `GET /api/v1/me/badges` → { counts: { <badge_source>: count } } keyed by the
// menu nodes' badgeSource (e.g. pending_bookings_count, today_bookings_count).
// useBadges() unwraps .counts; the nav matches each node's badgeSource to a key.
export async function getBadges(): Promise<BadgesResponse> {
  const raw = await apiFetch<unknown>('/me/badges');
  return BadgesResponseSchema.parse(raw);
}

// ── BOOKINGS LIST ───────────────────────────────────────────────────────────────

/** Render a slot ISO/`HH:mm` value as the `HH:mm` the screen passes to istSlot().
 *  Accepts a full ISO datetime or a bare time; falls back to the raw string. */
function toClockTime(value: string): string {
  // ISO datetime → take the HH:mm in Asia/Kolkata.
  if (value.includes('T') || value.includes(' ')) {
    const d = new Date(value);
    if (!Number.isNaN(d.getTime())) {
      return new Intl.DateTimeFormat('en-GB', {
        timeZone: 'Asia/Kolkata',
        hour: '2-digit',
        minute: '2-digit',
        hour12: false,
      }).format(d);
    }
  }
  // Already a clock string like "11:30" or "11:30:00".
  return value.slice(0, 5);
}

/** Today's date as YYYY-MM-DD in Asia/Kolkata (the clinic timezone), for the
 *  slots query default. en-CA renders ISO-ordered y-m-d. */
function istToday(): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Kolkata',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(new Date());
}

// The API serializes booking enums as the canonical snake_case STRING tokens
// (status/source). We pass them straight through to BookingRowSchema, which is
// the authoritative enum gate — an unexpected token surfaces here rather than
// silently mis-rendering. (A defensive int fallback maps a regression to the
// pending/dashboard default instead of throwing.)
const BOOKING_STATUS_FALLBACK: BookingRow['status'] = 'pending';
const BOOKING_SOURCE_FALLBACK: BookingRow['source'] = 'dashboard';

function asString<T extends string>(value: number | string, fallback: T): T {
  return typeof value === 'string' ? (value as T) : fallback;
}

export async function listBookings(): Promise<BookingRow[]> {
  const raw = await apiFetch<unknown[]>('/bookings');
  const dtos = BookingListItemDtoSchema.array().parse(raw);
  return dtos.map((d) =>
    BookingRowSchema.parse({
      id: d.bookingId,
      token: d.tokenNumber ?? 0,
      patient: d.patientDisplayName,
      // Server already masks; fall back to a safe placeholder if absent.
      maskedPhone: d.maskedPhone ?? '+91 ····· ·····',
      doctorName: d.doctorName ?? '—',
      dept: d.departmentName ?? '—',
      date: d.slotStart,
      time: toClockTime(d.slotStart),
      status: asString(d.status, BOOKING_STATUS_FALLBACK),
      source: asString(d.source, BOOKING_SOURCE_FALLBACK),
      note: d.note ?? '',
      createdAgo: d.createdAt,
    }),
  );
}

// ── BOOKING DETAIL (open Manage / Approve panel for a REAL booking) ──────────────
// `GET /api/v1/bookings/{bookingId}` returns a BookingListItemDto (same shape as a
// list row). The slide-over panels (manage/approve/conversation) consume the
// richer app-facing `Booking`, so we ADAPT the DTO into it. PHI: the detail
// endpoint returns a MASKED phone only — the panel shows `booking.phone`, so we
// fill it with the masked value (the raw number is never sent to the client).

/** Wire gender token → the Booking type's single-letter code. */
function asGenderCode(value: number | string | null | undefined): Booking['gender'] {
  if (typeof value !== 'string') return 'O';
  const v = value.trim().toLowerCase();
  if (v === 'female' || v === 'f') return 'F';
  if (v === 'male' || v === 'm') return 'M';
  return 'O';
}

/** Wire language token → the Booking type's Lang. Unknown → 'en'. */
function asLang(value: number | string | null | undefined): Lang {
  if (typeof value !== 'string') return 'en';
  const v = value.trim().toLowerCase();
  return v === 'hi' || v === 'mr' ? (v as Lang) : 'en';
}

export async function getBooking(bookingId: string): Promise<Booking> {
  const raw = await apiFetch<unknown>(`/bookings/${bookingId}`);
  const d = BookingListItemDtoSchema.parse(raw);
  return {
    id: d.bookingId,
    token: d.tokenNumber ?? 0,
    patient: d.patientDisplayName,
    // Detail endpoint is masked-phone only (DPDP) — the panel renders this as-is.
    phone: d.maskedPhone ?? '+91 ····· ·····',
    age: d.age ?? 0,
    gender: asGenderCode(d.gender),
    // The list/detail DTO carries no doctorId; the panels only need it for the
    // (mock) conversation link, not for the live approve/cancel actions (which key
    // off the booking id), so an empty id is safe here.
    doctorId: '',
    doctorName: d.doctorName ?? '—',
    dept: d.departmentName ?? '—',
    date: d.slotStart,
    time: toClockTime(d.slotStart),
    duration: 15,
    status: asString(d.status, BOOKING_STATUS_FALLBACK) as BookingStatus,
    source: asString(d.source, BOOKING_SOURCE_FALLBACK) as BookingSource,
    note: d.note ?? '',
    createdAgo: d.createdAt,
    lang: asLang(d.language),
  };
}

// ── DOCTOR SLOTS (NewBooking wizard Slot step) ───────────────────────────────────
// `GET /api/v1/doctors/{doctorId}/slots?date=YYYY-MM-DD` → SlotDto[]. We keep only
// status==="available" and adapt each into the app-facing Slot, carrying the
// `slotId` the create call needs. `time` is the "HH:mm" start (drop the seconds).

export async function listSlots(doctorId: string, date?: string): Promise<Slot[]> {
  const day = date ?? istToday();
  const raw = await apiFetch<unknown[]>(`/doctors/${doctorId}/slots?date=${day}`);
  const dtos = SlotDtoSchema.array().parse(raw);
  return dtos
    .filter((s) => s.status.toLowerCase() === 'available')
    .map((s) =>
      SlotSchema.parse({
        time: s.startTime.slice(0, 5),
        state: 'open',
        slotId: s.slotId,
      }),
    );
}

// ── PRACTITIONERS (NewBooking wizard Department/Doctor step) ──────────────────────
// The wizard's doctor step consumes Practitioner[]. Live mode reuses GET /doctors
// (DoctorDto) and filters client-side by departmentName (the wizard's deptId is a
// mock token, so we map the live department name back to it). The picked
// practitioner's `id` is the real doctor GUID required by the create call.

export async function listPractitioners(deptId?: string): Promise<Practitioner[]> {
  const raw = await apiFetch<unknown[]>('/doctors');
  const dtos = DoctorDtoSchema.array().parse(raw);
  return dtos
    .filter((d) => !deptId || deptId === 'all' || deptIdForName(d.departmentName) === deptId)
    .map((d) => {
      const deptName = d.departmentName ?? d.specialization ?? '—';
      return {
        id: d.doctorId,
        name: d.displayName ?? d.fullName,
        spec: d.specialization ?? deptName,
        deptId: deptIdForName(d.departmentName),
        fee: d.consultationFee ?? 0,
        room: '—',
        next: d.nextAvailableSlot ? toClockTime(d.nextAvailableSlot) : '—',
        initials: doctorInitials(d.displayName ?? d.fullName),
      } satisfies Practitioner;
    });
}

// ── PATIENTS LIST ─────────────────────────────────────────────────────────────

export async function listPatients(): Promise<PatientRow[]> {
  const raw = await apiFetch<unknown[]>('/patients');
  const dtos = PatientListItemDtoSchema.array().parse(raw);
  return dtos.map((d) => ({
    id: d.patientId,
    name: d.fullName,
    // Server already masks; mask again defensively only if a raw number slipped.
    maskedPhone: d.maskedPhone ? d.maskedPhone : maskPhone('+91 00000 00000'),
    age: d.age,
    gender: d.gender,
    preferredLanguage: d.preferredLanguage,
  }));
}

// ── DOCTORS DIRECTORY ──────────────────────────────────────────────────────────
// DoctorDto now carries real directory enrichment: departmentName, today's
// booked/capacity OPD load, average rating, today's OPD window (HH:mm:ss), and
// the next free slot (nullable ISO). We surface those instead of placeholders.
// colorKey stays a TOKEN KEY (never a hex), derived from the department name.

/** Mock department id → token color key (mirrors lib/mock DEPT_COLOR_KEY). */
const DEPT_COLOR_KEY: Record<string, string> = {
  card: 'accent',
  ortho: 'info',
  gyn: 'primary',
  ped: 'primary',
  derm: 'warn',
  ent: 'primary',
  gen: 'muted',
};

/** Real department name → mock dept id, so the DoctorsScreen filter tabs (which
 *  iterate the mock DEPARTMENTS by id) match live cards. Unknown names fall back
 *  to 'gen' (General Medicine). */
const DEPT_NAME_TO_ID: Record<string, string> = {
  cardiology: 'card',
  orthopedics: 'ortho',
  orthopaedics: 'ortho',
  gynaecology: 'gyn',
  gynecology: 'gyn',
  paediatrics: 'ped',
  pediatrics: 'ped',
  dermatology: 'derm',
  ent: 'ent',
  'general medicine': 'gen',
};

function deptIdForName(name: string | null | undefined): string {
  if (!name) return 'gen';
  return DEPT_NAME_TO_ID[name.trim().toLowerCase()] ?? 'gen';
}

function doctorInitials(name: string): string {
  return name
    .replace(/^Dr\.?\s+/i, '')
    .split(/\s+/)
    .slice(0, 2)
    .map((p) => p[0]?.toUpperCase() ?? '')
    .join('');
}

/** "HH:mm:ss" → "HH:mm" (drop seconds); null → undefined. */
function hhmm(value: string | null | undefined): string | undefined {
  return value ? value.slice(0, 5) : undefined;
}

/** Build the OPD window label "09:00–17:00" from today's start/end, or "—". */
function opdWindow(start: string | null | undefined, end: string | null | undefined): string {
  const s = hhmm(start);
  const e = hhmm(end);
  return s && e ? `${s}–${e}` : '—';
}

export async function listDoctorCards(): Promise<DoctorCard[]> {
  const raw = await apiFetch<unknown[]>('/doctors');
  const dtos = DoctorDtoSchema.array().parse(raw);
  return dtos.map((d) => {
    const deptName = d.departmentName ?? d.specialization ?? '—';
    const deptId = deptIdForName(d.departmentName);
    const next = d.nextAvailableSlot ? toClockTime(d.nextAvailableSlot) : '—';
    return {
      id: d.doctorId,
      name: d.displayName ?? d.fullName,
      spec: d.specialization ?? deptName,
      deptId,
      deptName,
      colorKey: DEPT_COLOR_KEY[deptId] ?? 'muted',
      // The DTO has no qualification field; show the specialization as the
      // subtitle so the card line isn't an em dash.
      qualification: d.specialization ?? '—',
      feeInr: d.consultationFee ?? 0,
      room: '—',
      rating: d.rating ?? 0,
      initials: doctorInitials(d.displayName ?? d.fullName),
      todayCount: d.todayBooked ?? 0,
      todayCapacity: d.todayCapacity ?? 0,
      hours: opdWindow(d.todayHoursStart, d.todayHoursEnd),
      nextSlot: next,
    } satisfies DoctorCard;
  });
}

// ── DASHBOARD SUMMARY ──────────────────────────────────────────────────────────
// Adapt the (possibly partial) DTO into the app-facing DashboardSummary, filling
// 0/0.0 fallbacks for anything the backend doesn't emit yet so the strip renders.

export async function getDashboardSummary(): Promise<DashboardSummary> {
  const raw = await apiFetch<unknown>('/dashboard/summary');
  const dto = DashboardSummaryDtoSchema.parse(raw);
  const num = (v: number | null | undefined, fallback = 0): number =>
    typeof v === 'number' ? v : fallback;
  // noShowRate is a FRACTION (0..1) on the wire; StatCards renders `${value}%`,
  // so present it as a percentage and round to 1dp.
  const noShowPct = Math.round(num(dto.noShowRate) * 1000) / 10;
  return {
    liveQueue: num(dto.liveQueueCount),
    // The WhatsApp/walk-in split isn't in the live DTO yet (flagged) — 0/0.
    liveQueueWhatsapp: 0,
    liveQueueWalkIn: 0,
    confirmedToday: num(dto.confirmedTodayCount),
    revenueToday: num(dto.todayRevenue),
    noShowRate: noShowPct,
    // No active-conversations metric in the live summary yet (flagged) — 0.
    activeConversations: 0,
  };
}

// ── ANALYTICS ───────────────────────────────────────────────────────────────────
// `GET /api/v1/analytics?period=month|quarter|year` → AnalyticsDto (aggregates,
// NO PHI). We adapt it into the app-facing Analytics shape AnalyticsScreen already
// consumes: pre-formatted KPI strings (deltaPct/higherIsBetter drive the trend
// colour), channel-split weekly volume (other → "direct"), colour-keyed top
// departments, and the fixed 5-stage funnel (stage strings → enum keys by order).

/** Top-department colour keys, in display order (token keys, never hex). */
const TOP_DEPT_COLOR_KEYS = ['accent', 'muted', 'primary', 'info', 'warn', 'primary'];

/** The funnel's enum keys, in the server's stage order. The screen labels off
 *  these keys via i18n; the server's free-text stage label is not displayed. */
const FUNNEL_KEYS = ['startedChat', 'pickedDept', 'pickedDoctor', 'pickedSlot', 'confirmed'] as const;

/** Round a 0..100 percentage to at most 1 decimal for a clean "%" label. */
function pct1(value: number): number {
  return Math.round(value * 10) / 10;
}

function adaptAnalytics(dto: AnalyticsDto): Analytics {
  const k = dto.kpis;
  return AnalyticsSchema.parse({
    kpis: [
      {
        key: 'totalBookings',
        value: k.totalBookings.toLocaleString('en-IN'),
        deltaPct: 0,
        higherIsBetter: true,
        caption: null,
      },
      {
        key: 'whatsappShare',
        value: `${pct1(k.whatsappSharePct)}%`,
        deltaPct: 0,
        higherIsBetter: true,
        caption: null,
      },
      {
        key: 'noShowRate',
        value: `${pct1(k.noShowRatePct)}%`,
        deltaPct: 0,
        higherIsBetter: false,
        caption: null,
      },
      {
        key: 'revenue',
        value: inr(Math.round(k.revenue)),
        deltaPct: 0,
        higherIsBetter: true,
        caption: null,
      },
    ],
    volume: dto.weeklyVolume.map((v) => ({
      weekday: v.weekday,
      whatsapp: v.whatsapp,
      direct: v.other,
    })),
    topDepartments: dto.topDepartments.map((d, i) => ({
      id: `dept-${i}`,
      name: d.departmentName,
      colorKey: TOP_DEPT_COLOR_KEYS[i % TOP_DEPT_COLOR_KEYS.length],
      bookings: d.bookings,
    })),
    funnel: dto.funnel.slice(0, FUNNEL_KEYS.length).map((f, i) => ({
      key: FUNNEL_KEYS[i],
      count: f.count,
      pct: pct1(f.pct),
    })),
  });
}

export async function getAnalytics(period: 'month' | 'quarter' | 'year' = 'month'): Promise<Analytics> {
  const raw = await apiFetch<unknown>(`/analytics?period=${period}`);
  return adaptAnalytics(AnalyticsDtoSchema.parse(raw));
}

// ── BOOKING MUTATIONS (Idempotency-Key required) ─────────────────────────────────
// Each action POSTs to /bookings/{id}/<action> with an Idempotency-Key header
// (apiFetch idempotency:<stable key>). The stable key is generated ONCE by the
// caller (on action start) and passed in, so a retry maps to the same key and the
// server de-dupes. We adapt the BookingActionResultDto into the app-facing
// BookingMutationResult { id, status }.

/** Map the action result's status (string token, defensively int-tolerant) onto
 *  the app-facing BookingRow status, falling back to the optimistic target. */
function actionStatus(
  raw: number | string | undefined,
  fallback: BookingRow['status'],
): BookingRow['status'] {
  return typeof raw === 'string' ? (raw as BookingRow['status']) : fallback;
}

async function bookingAction(
  bookingId: string,
  action: 'approve' | 'cancel' | 'complete' | 'no-show',
  idempotencyKey: string,
  optimistic: BookingRow['status'],
  body?: unknown,
): Promise<BookingMutationResult> {
  const raw = await apiFetch<unknown>(`/bookings/${bookingId}/${action}`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body,
  });
  const dto = BookingActionResultDtoSchema.parse(raw);
  return { id: dto.bookingId ?? bookingId, status: actionStatus(dto.status, optimistic) };
}

export function approveBooking(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return bookingAction(bookingId, 'approve', idempotencyKey, 'confirmed');
}

export function cancelBooking(
  bookingId: string,
  reason: string,
  idempotencyKey: string,
): Promise<BookingMutationResult> {
  return bookingAction(bookingId, 'cancel', idempotencyKey, 'cancelled', { reason });
}

export function completeBooking(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return bookingAction(bookingId, 'complete', idempotencyKey, 'completed');
}

export function noShowBooking(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return bookingAction(bookingId, 'no-show', idempotencyKey, 'no_show');
}

// ── CREATE BOOKING ────────────────────────────────────────────────────────────
// POST /bookings (CreateBookingRequest). The wizard collects patient + doctor +
// slot. NOTE: the live slots/practitioners endpoints aren't wired yet, so the
// wizard's doctor/slot ids are mock placeholders — a live create will surface the
// server's validation error gracefully (via toUserError) rather than crash. When
// the slot endpoints land this sends real GUIDs and succeeds.
const SEX_TO_GENDER: Record<string, string> = { F: 'female', M: 'male', O: 'other' };

export async function createBooking(
  draft: {
    phone: string;
    name: string;
    age: string;
    sex: 'F' | 'M' | 'O';
    lang: 'en' | 'hi';
    reason: string;
    doctorId: string;
    slot: string;
  },
  idempotencyKey: string,
): Promise<CreateBookingResult> {
  const ageNum = Number.parseInt(draft.age, 10);
  const raw = await apiFetch<unknown>('/bookings', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      slotId: draft.slot,
      doctorId: draft.doctorId,
      departmentId: null,
      patientPhone: draft.phone,
      patientName: draft.name || null,
      patientAge: Number.isFinite(ageNum) ? ageNum : null,
      patientGender: SEX_TO_GENDER[draft.sex] ?? null,
      bookingType: 'consultation',
      bookedVia: 'dashboard',
      chiefComplaint: draft.reason || null,
      issueOpdToken: true,
    },
  });
  const dto = CreateBookingResultDtoSchema.parse(raw);
  // The app-facing CreateBookingResult requires a numeric token + 'confirmed'
  // literal; a new booking is 'pending' server-side and the token may be null,
  // so we present the token (0 when absent) and the optimistic 'confirmed'.
  return { id: dto.bookingId, token: dto.tokenNumber ?? 0, status: 'confirmed' };
}

// ── ADD PATIENT ───────────────────────────────────────────────────────────────
// POST /patients (RegisterPatientRequest). Cross-tenant by phone; returns the
// patient id + whether it already existed.
export async function addPatient(
  input: { phone: string; name: string; age: string; lang: 'en' | 'hi'; idempotencyKey: string },
): Promise<{ patientId: string; alreadyExisted: boolean }> {
  const ageNum = Number.parseInt(input.age, 10);
  const raw = await apiFetch<unknown>('/patients', {
    method: 'POST',
    idempotency: input.idempotencyKey,
    body: {
      phoneNumber: input.phone,
      fullName: input.name || null,
      age: Number.isFinite(ageNum) ? ageNum : null,
      gender: null,
      preferredLanguage: input.lang,
    },
  });
  const dto = RegisterPatientResultDtoSchema.parse(raw);
  return { patientId: dto.patientId, alreadyExisted: dto.alreadyExisted };
}

// ── ADD DOCTOR ──────────────────────────────────────────────────────────────────
// POST /doctors (CreateDoctorRequest). Provisions a doctor into the caller's
// tenant (tenant_id from the JWT, never a header). Idempotency-Key honoured if
// present. We map only the panel fields that have a backend column; panel-only
// fields (e.g. room) are dropped. `departmentId` is sent only when a real GUID is
// supplied (the panel's mock dept token is NOT a column value, so it's omitted).
export async function addDoctor(
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
  const raw = await apiFetch<unknown>('/doctors', {
    method: 'POST',
    idempotency: input.idempotencyKey,
    body: {
      fullName: input.fullName,
      departmentId: input.departmentId,
      specialization: input.specialization,
      qualifications: input.qualifications.length ? input.qualifications : null,
      consultationFee: input.consultationFee,
      phone: input.phone,
      isAcceptingNewPatients: true,
    },
  });
  const dto = CreateDoctorResultDtoSchema.parse(raw);
  return { doctorId: dto.doctorId, fullName: dto.fullName, departmentId: dto.departmentId };
}

// ═════════════════════════════════════════════════════════════════════════════
// COMMISSION / CARE PARTNERS (Slice 07)  — base path /commission
// ─────────────────────────────────────────────────────────────────────────────
// The frontend zod contracts (Broker/Attribution/CommissionRule/Payout/Dispute)
// MIRROR the .NET DTOs 1:1, so the live READ adapters are pure pass-throughs:
//   apiFetch → zod-parse the RAW DTO → return the (identical) app-facing shape.
// WRITES: money actions (approve/execute) and status changes carry an
//   Idempotency-Key (apiFetch idempotency:<stable key>). APPROVE and EXECUTE are
//   DISTINCT endpoints/permissions — never collapsed. Several mutations return
//   204 No Content (apiFetch → undefined); we synthesize the same result shape
//   the mock returns so the feature hooks (which only invalidate) are mode-blind.
// PHI/legal: lists carry MASKED phone + first-name only; NO full PAN is ever
//   returned; PCPNDT excludesPndt stays a server-enforced literal(true).
// ═════════════════════════════════════════════════════════════════════════════

// ── READS (GET) ──────────────────────────────────────────────────────────────

export async function listBrokers(): Promise<Broker[]> {
  const raw = await apiFetch<unknown[]>('/commission/brokers?skip=0&take=100');
  return BrokerSchema.array().parse(raw);
}

export async function listAttributions(): Promise<Attribution[]> {
  const raw = await apiFetch<unknown[]>('/commission/attributions?skip=0&take=100');
  return AttributionSchema.array().parse(raw);
}

export async function listCommissionRules(): Promise<CommissionRule[]> {
  const raw = await apiFetch<unknown[]>('/commission/rules');
  // The CommissionRuleDto omits the optional cap/min/max + firstBookingOnly keys
  // when unset (they are not serialized as null). The app-facing CommissionRule
  // models them as required-nullable, so we fill the absent keys before parsing —
  // the schema (and the mock, which always supplies them) stay untouched.
  const rows = (raw as Record<string, unknown>[]).map((r) => ({
    minCommissionInr: null,
    maxCommissionInr: null,
    maxMonthlyPerBrokerInr: null,
    firstBookingOnly: false,
    ...r,
  }));
  return CommissionRuleSchema.array().parse(rows);
}

export async function listPayouts(): Promise<Payout[]> {
  const raw = await apiFetch<unknown[]>('/commission/payouts?skip=0&take=100');
  return PayoutSchema.array().parse(raw);
}

export async function listDisputes(): Promise<Dispute[]> {
  const raw = await apiFetch<unknown[]>('/commission/disputes');
  return DisputeSchema.array().parse(raw);
}

// ── WRITES (POST, idempotent) ────────────────────────────────────────────────

export async function registerBroker(
  req: RegisterBrokerRequest,
  idempotencyKey: string,
): Promise<RegisterBrokerResult> {
  const raw = await apiFetch<unknown>('/commission/brokers', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      phone: req.phone,
      fullName: req.fullName,
      email: req.email ?? null,
      brokerType: req.brokerType,
      pan: req.pan ?? null,
      gstNumber: req.gstNumber ?? null,
      // The dashboard onboards Care Partners directly; tier is server-defaulted.
      onboardedVia: 'dashboard',
    },
  });
  // PAN is captured server-side and NEVER echoed back (result is id + existed).
  return RegisterBrokerResultSchema.parse(raw);
}

export async function setBrokerStatus(
  brokerId: string,
  input: { isActive: boolean; reason?: string },
  idempotencyKey: string,
): Promise<CommissionCreated> {
  // 204 No Content — synthesize the mock's { id } result so the hook is mode-blind.
  await apiFetch<void>(`/commission/brokers/${brokerId}/status`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body: { isActive: input.isActive, reason: input.reason ?? null },
  });
  return CommissionCreatedSchema.parse({ id: brokerId });
}

export async function blacklistBroker(
  brokerId: string,
  reason: string,
  idempotencyKey: string,
): Promise<CommissionCreated> {
  // 204 No Content. `reason` is REQUIRED by the endpoint.
  await apiFetch<void>(`/commission/brokers/${brokerId}/blacklist`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body: { reason },
  });
  return CommissionCreatedSchema.parse({ id: brokerId });
}

export async function createCommissionRule(
  req: CreateCommissionRuleRequest,
  idempotencyKey: string,
): Promise<CommissionRule> {
  // POST /rules → ruleId (Guid). The list refetches after invalidation, so we
  // adapt the panel's request + the returned id into a CommissionRule for the
  // optimistic-free mutation result (the hook only invalidates). excludesPndt is
  // server-enforced true (PCPNDT).
  const raw = await apiFetch<unknown>('/commission/rules', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      ruleName: req.ruleName,
      ruleKey: req.ruleKey,
      calcType: req.calcType,
      flatAmountInr: req.flatAmountInr ?? null,
      percentage: req.percentage ?? null,
      minCommissionInr: req.minCommissionInr ?? null,
      maxCommissionInr: req.maxCommissionInr ?? null,
      maxMonthlyPerBrokerInr: req.maxMonthlyPerBrokerInr ?? null,
      priority: req.priority,
      firstBookingOnly: req.firstBookingOnly,
    },
  });
  // The endpoint returns the ruleId (Guid) — a bare string, or { ruleId } object.
  const ruleId =
    typeof raw === 'string'
      ? raw
      : ((raw as { ruleId?: string })?.ruleId ?? crypto.randomUUID());
  return CommissionRuleSchema.parse({
    ruleId,
    ruleName: req.ruleName,
    ruleKey: req.ruleKey,
    calcType: req.calcType,
    flatAmountInr: req.flatAmountInr ?? null,
    percentage: req.percentage ?? null,
    minCommissionInr: req.minCommissionInr ?? null,
    maxCommissionInr: req.maxCommissionInr ?? null,
    maxMonthlyPerBrokerInr: req.maxMonthlyPerBrokerInr ?? null,
    priority: req.priority,
    firstBookingOnly: req.firstBookingOnly,
    isActive: true,
    excludesPndt: true,
  });
}

/** APPROVE a rule (if the UI exposes it) → POST /rules/{id}/approve → 204. */
export async function approveRule(ruleId: string, idempotencyKey: string): Promise<CommissionCreated> {
  await apiFetch<void>(`/commission/rules/${ruleId}/approve`, {
    method: 'POST',
    idempotency: idempotencyKey,
  });
  return CommissionCreatedSchema.parse({ id: ruleId });
}

/** APPROVE a payout — DISTINCT permission/step from execute. → { payoutId, status:'approved', paymentReference }. */
export async function approvePayout(payoutId: string, idempotencyKey: string): Promise<PayoutActionResult> {
  const raw = await apiFetch<unknown>(`/commission/payouts/${payoutId}/approve`, {
    method: 'POST',
    idempotency: idempotencyKey,
  });
  return PayoutActionResultSchema.parse(raw);
}

/** EXECUTE a payout — DISTINCT permission/step from approve. → { payoutId, status:'paid', paymentReference:'UTR-…' }. */
export async function executePayout(payoutId: string, idempotencyKey: string): Promise<PayoutActionResult> {
  const raw = await apiFetch<unknown>(`/commission/payouts/${payoutId}/execute`, {
    method: 'POST',
    idempotency: idempotencyKey,
  });
  return PayoutActionResultSchema.parse(raw);
}

export async function raiseDispute(
  req: RaiseDisputeRequest,
  idempotencyKey: string,
): Promise<CommissionCreated> {
  // The dashboard raises disputes as clinic STAFF (the app-facing request carries
  // no raisedBy — it's a fixed dashboard context), so we supply the required
  // `raisedBy: tenant_staff` here. → disputeId.
  const raw = await apiFetch<unknown>('/commission/disputes', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      attributionId: req.attributionId,
      raisedBy: 'tenant_staff',
      disputeReason: req.disputeReason,
      description: req.description,
    },
  });
  const disputeId =
    typeof raw === 'string'
      ? raw
      : ((raw as { disputeId?: string })?.disputeId ?? crypto.randomUUID());
  return CommissionCreatedSchema.parse({ id: disputeId });
}

export async function resolveDispute(
  req: ResolveDisputeRequest,
  idempotencyKey: string,
): Promise<CommissionCreated> {
  // 204 No Content.
  await apiFetch<void>('/commission/disputes/resolve', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      disputeId: req.disputeId,
      status: req.status,
      resolutionNotes: req.resolutionNotes ?? null,
      amountAdjustmentInr: req.amountAdjustmentInr ?? null,
    },
  });
  return CommissionCreatedSchema.parse({ id: req.disputeId });
}

// ═════════════════════════════════════════════════════════════════════════════
// CALENDAR (/calendar) — week-view capacity heatmap built from REAL slots.
// ─────────────────────────────────────────────────────────────────────────────
// No dedicated week-aggregate endpoint exists, so we roll one up client-side:
//   1. GET /doctors → tenant doctors.
//   2. For each (day in the current Mon–Sun week) × (doctor): GET
//      /doctors/{id}/slots?date=YYYY-MM-DD. ~8 doctors × 7 days fetched
//      concurrently; a per-call failure is tolerated (treated as "no slots").
//   3. Aggregate per (timeRow, day): booked = Σ currentCount, capacity = Σ
//      maxCount across the day's slots whose startTime maps to that row; derive a
//      cell state. We use the SAME TIMES rows the mock grid uses for visual parity.
// NO PHI — counts + capacity only.
// ═════════════════════════════════════════════════════════════════════════════

// The time rows the mock CalendarGrid renders — reused verbatim for visual parity.
const CAL_TIMES = [
  '09:00', '09:30', '10:00', '10:30', '11:00', '11:30', '12:00', '12:30',
  '13:00', '14:00', '14:30', '15:00', '15:30', '16:00', '16:30', '17:00',
  '17:30', '18:00',
];
const WEEKDAY_ORDER = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const MONTHS_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** IST date parts (y/m/d/weekday) for an arbitrary instant. */
function istParts(d: Date): { y: number; m: number; day: number; weekday: string } {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Kolkata',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    weekday: 'short',
  }).formatToParts(d);
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? '';
  return {
    y: Number(get('year')),
    m: Number(get('month')),
    day: Number(get('day')),
    weekday: get('weekday'),
  };
}

/** Build the 7 Mon–Sun day descriptors of the current IST week (date keys + labels). */
function currentIstWeek(): {
  key: string; // YYYY-MM-DD
  weekday: string; // Mon
  dayOfMonth: string; // 21
  label: string; // Mon 21
  isToday: boolean;
}[] {
  const now = new Date();
  const today = istParts(now);
  const todayIdx = WEEKDAY_ORDER.indexOf(today.weekday); // 0=Mon … 6=Sun
  const offsetToMonday = todayIdx < 0 ? 0 : -todayIdx;
  // Anchor at IST-midnight today, then step in whole days. Use a UTC noon anchor
  // for the IST date to avoid DST/edge rounding (IST has no DST, but be safe).
  const anchor = new Date(Date.UTC(today.y, today.m - 1, today.day, 6, 0, 0)); // 06:00Z ≈ 11:30 IST
  return WEEKDAY_ORDER.map((_w, i) => {
    const d = new Date(anchor.getTime() + (offsetToMonday + i) * 86_400_000);
    const p = istParts(d);
    const key = `${p.y}-${String(p.m).padStart(2, '0')}-${String(p.day).padStart(2, '0')}`;
    return {
      key,
      weekday: WEEKDAY_ORDER[i],
      dayOfMonth: String(p.day),
      label: `${WEEKDAY_ORDER[i]} ${p.day}`,
      isToday: p.y === today.y && p.m === today.m && p.day === today.day,
    };
  });
}

/** Human range label, e.g. "21–27 Jun 2026" (spans month if needed). */
function weekRangeLabel(week: ReturnType<typeof currentIstWeek>): string {
  const first = week[0];
  const last = week[week.length - 1];
  const [fy, fm, fd] = first.key.split('-').map(Number);
  const [ly, lm, ld] = last.key.split('-').map(Number);
  if (fy === ly && fm === lm) return `${fd}–${ld} ${MONTHS_SHORT[fm - 1]} ${fy}`;
  if (fy === ly) return `${fd} ${MONTHS_SHORT[fm - 1]} – ${ld} ${MONTHS_SHORT[lm - 1]} ${ly}`;
  return `${fd} ${MONTHS_SHORT[fm - 1]} ${fy} – ${ld} ${MONTHS_SHORT[lm - 1]} ${ly}`;
}

type SlotAgg = { booked: number; capacity: number; allBlocked: boolean; any: boolean };

/** Derive a cell state from an aggregated (timeRow, day) bucket. */
function cellState(a: SlotAgg): CalendarGrid['columns'][number]['cells'][number]['state'] {
  if (!a.any) return 'off';
  if (a.allBlocked) return 'blocked';
  if (a.capacity > 0 && a.booked >= a.capacity) return 'full';
  if (a.capacity > 0 && a.booked / a.capacity >= 0.7) return 'tight';
  if (a.capacity > 0) return 'open';
  return 'off';
}

export async function getCalendarGrid(): Promise<CalendarGrid> {
  const week = currentIstWeek();

  // 1) tenant doctors
  const doctorsRaw = await apiFetch<unknown[]>('/doctors');
  const doctors = DoctorDtoSchema.array().parse(doctorsRaw);

  // 2) fetch every (day × doctor) slot list concurrently; tolerate per-call failure
  const tasks: Promise<{ dayKey: string; slots: ReturnType<typeof SlotDtoSchema.parse>[] }>[] = [];
  for (const day of week) {
    for (const doc of doctors) {
      tasks.push(
        apiFetch<unknown[]>(`/doctors/${doc.doctorId}/slots?date=${day.key}`)
          .then((raw) => ({ dayKey: day.key, slots: SlotDtoSchema.array().parse(raw) }))
          .catch(() => ({ dayKey: day.key, slots: [] })),
      );
    }
  }
  const results = await Promise.all(tasks);

  // 3) aggregate per (timeRow, dayKey)
  // bucket[dayKey][timeRow] = SlotAgg
  const buckets = new Map<string, Map<string, SlotAgg>>();
  const ensure = (dayKey: string, row: string): SlotAgg => {
    let dayMap = buckets.get(dayKey);
    if (!dayMap) {
      dayMap = new Map();
      buckets.set(dayKey, dayMap);
    }
    let agg = dayMap.get(row);
    if (!agg) {
      agg = { booked: 0, capacity: 0, allBlocked: true, any: false };
      dayMap.set(row, agg);
    }
    return agg;
  };

  for (const { dayKey, slots } of results) {
    for (const s of slots) {
      const row = s.startTime.slice(0, 5); // "HH:mm"
      if (!CAL_TIMES.includes(row)) continue; // keep grid visually consistent
      const agg = ensure(dayKey, row);
      agg.any = true;
      const blocked = s.status.toLowerCase() === 'blocked';
      if (!blocked) agg.allBlocked = false;
      agg.booked += s.currentCount;
      agg.capacity += s.maxCount;
    }
  }

  const columns = week.map((day, di) => ({
    key: `d-${di}`,
    label: day.label,
    weekday: day.weekday,
    dayOfMonth: day.dayOfMonth,
    isToday: day.isToday,
    cells: CAL_TIMES.map((row) => {
      const agg = buckets.get(day.key)?.get(row) ?? {
        booked: 0,
        capacity: 0,
        allBlocked: true,
        any: false,
      };
      return { state: cellState(agg), booked: agg.booked, capacity: agg.capacity };
    }),
  }));

  return CalendarGridSchema.parse({
    times: CAL_TIMES,
    rangeLabel: weekRangeLabel(week),
    columns,
  });
}
