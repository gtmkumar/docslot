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
import { getSessionSnapshot } from '@/stores/session';
import { inr, maskPhone } from '@/lib/format';
import { MockApiError, type ApiRequestLogFilter } from '@/lib/mock';
import {
  AnalyticsDtoSchema,
  AnalyticsSchema,
  NoShowRiskSchema,
  TriageResultSchema,
  type NoShowRisk,
  type TriageRequestInput,
  type TriageResult,
  ApiClientSchema,
  ApiClientMutationResultSchema,
  ApiClientSecretResultSchema,
  ApiRequestLogPageSchema,
  CreateWebhookResultSchema,
  WebhookDeliverySchema,
  AttributionSchema,
  AuditAnchorSchema,
  AuditChainVerifySchema,
  BadgesResponseSchema,
  AbdmRecordDetailSchema,
  AbdmRecordListItemSchema,
  BreakGlassResultSchema,
  CreateMedicalHistoryResultSchema,
  IssuePrescriptionResultSchema,
  LabReportDetailSchema,
  LabReportListItemSchema,
  MedicalHistorySchema,
  PatientConsentSchema,
  PrescriptionDetailSchema,
  PrescriptionListItemSchema,
  PushAbdmRecordResultSchema,
  UploadLabReportResultSchema,
  BehalfRelationSchema,
  BookedByTypeSchema,
  BookingActionResultDtoSchema,
  BookingListItemDtoSchema,
  BookingRowSchema,
  BreachSchema,
  ConversationMessageDtoSchema,
  PatientConsentStatusSchema,
  RescheduleResultDtoSchema,
  BrokerBookingResultSchema,
  BrokerSchema,
  BrokerWalletSchema,
  CalendarGridSchema,
  CampaignSchema,
  CommissionCreatedSchema,
  CommissionRuleSchema,
  Form16ACertificateSchema,
  ReferralLinkSchema,
  CreateBookingResultDtoSchema,
  CreateModuleResultSchema,
  CreatePermissionResultSchema,
  CreateDoctorResultDtoSchema,
  DashboardSummaryDtoSchema,
  DisputeSchema,
  DoctorDtoSchema,
  DpdpRequestSchema,
  DuplicateRoleResultSchema,
  EffectiveAccessSchema,
  EventTypeSchema,
  IamPermissionDtoSchema,
  KeyStatusSchema,
  ModuleDtoSchema,
  RoleMatrixSchema,
  RolePermissionToggleResultSchema,
  RoleSchema,
  AssignRoleResultSchema,
  RevokeRoleResultSchema,
  SetOverrideResultSchema,
  UserListItemSchema,
  UserListItemDtoSchema,
  CreateUserResultSchema,
  SetUserStatusResultSchema,
  UpdateUserProfileResultSchema,
  ResetAccessResultSchema,
  MeSchema,
  MenusResponseSchema,
  PatientListItemDtoSchema,
  PayoutActionResultSchema,
  PayoutSchema,
  PermissionsResponseSchema,
  RegisterBrokerResultSchema,
  RegisterPatientResultDtoSchema,
  TenantListItemSchema,
  type TenantListItem,
  ReviewQueueItemSchema,
  ScopeSchema,
  SlotDtoSchema,
  SlotSchema,
  TokenResponseSchema,
  WebhookSubscriptionSchema,
  type AssignRoleRequest,
  type AssignRoleResult,
  type RevokeRoleResult,
  type Analytics,
  type AnalyticsDto,
  type ApiClient,
  type ApiClientMutationResult,
  type ApiClientSecretResult,
  type ApiRequestLogPage,
  type CreateWebhookRequest,
  type CreateWebhookResult,
  type RegisterApiClientRequest,
  type SetClientRateLimitsRequest,
  type UpdateWebhookRequest,
  type WebhookDelivery,
  type Attribution,
  type AuditAnchor,
  type AuditChainVerify,
  type BadgesResponse,
  type AbdmRecordDetail,
  type AbdmRecordListItem,
  type BreakGlassRequest,
  type BreakGlassResult,
  type CreateMedicalHistoryRequest,
  type CreateMedicalHistoryResult,
  type IssuePrescriptionRequest,
  type IssuePrescriptionResult,
  type LabReportDetail,
  type LabReportListItem,
  type MedicalHistory,
  type PatientConsent,
  type PrescriptionDetail,
  type PrescriptionListItem,
  type PushAbdmRecordResult,
  type UpdateMedicalHistoryRequest,
  type UploadLabReportRequest,
  type UploadLabReportResult,
  type BookingMutationResult,
  type BookingRow,
  type Breach,
  type Broker,
  type BrokerBookingResult,
  type BrokerPortalBookingRequest,
  type BrokerWallet,
  type CalendarGrid,
  type Campaign,
  type CommissionCreated,
  type CommissionRule,
  type CreateBookingResult,
  type CreateCampaignRequest,
  type CreateCommissionRuleRequest,
  type CreateReferralLinkRequest,
  type Form16ACertificate,
  type ReferralLink,
  type CreateModuleRequest,
  type CreateModuleResult,
  type CreatePermissionRequest,
  type CreatePermissionResult,
  type DashboardSummary,
  type Dispute,
  type DoctorCard,
  type DpdpRequest,
  type DuplicateRoleRequest,
  type DuplicateRoleResult,
  type EffectiveAccess,
  type EventType,
  type IamPermissionDto,
  type KeyStatus,
  type ModuleDto,
  type Role,
  type RoleMatrix,
  type RolePermissionToggleResult,
  type SetOverrideRequest,
  type SetOverrideResult,
  type UserListItem,
  type CreateUserRequest,
  type CreateUserResult,
  type SetUserStatusRequest,
  type SetUserStatusResult,
  type UpdateUserProfileRequest,
  type UpdateUserProfileResult,
  type ResetAccessResult,
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
  type ReviewQueueItem,
  type Scope,
  type Slot,
  type TokenResponse,
  type WebhookSubscription,
} from '@/lib/mock/contracts';
import type {
  BehalfRelation,
  BookedByType,
  Booking,
  BookingSource,
  BookingStatus,
  ChatMessage,
  Lang,
  PatientConsentStatus,
} from '@/lib/types';

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

// ── Behalf / consent mapping (Phase 1) ───────────────────────────────────────
// The wire tokens are the canonical snake_case strings; absent/unknown values
// fall back to a `self` / `not_required` booking so a pre-Phase-1 row stays safe.
// We parse through the zod enums so an unexpected token degrades to the default
// rather than mis-gating the Approve action.
function asBookedByType(value: number | string | null | undefined): BookedByType {
  const parsed = typeof value === 'string' ? BookedByTypeSchema.safeParse(value) : null;
  return parsed?.success ? parsed.data : 'self';
}

function asBehalfRelation(value: number | string | null | undefined): BehalfRelation | null {
  const parsed = typeof value === 'string' ? BehalfRelationSchema.safeParse(value) : null;
  return parsed?.success ? parsed.data : null;
}

function asConsentStatus(value: number | string | null | undefined): PatientConsentStatus {
  const parsed = typeof value === 'string' ? PatientConsentStatusSchema.safeParse(value) : null;
  return parsed?.success ? parsed.data : 'not_required';
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
      bookedByType: asBookedByType(d.bookedByType),
      behalfRelation: asBehalfRelation(d.behalfRelation),
      patientConsentStatus: asConsentStatus(d.patientConsentStatus),
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
    // The booking's doctor — used by the reschedule slide-over to list this doctor's open slots.
    doctorId: d.doctorId ?? '',
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
    // Behalf / consent (Phase 1, read-only) — the manage panel surfaces these and
    // gates Approve on patientConsentStatus.
    bookedByType: asBookedByType(d.bookedByType),
    behalfRelation: asBehalfRelation(d.behalfRelation),
    patientConsentStatus: asConsentStatus(d.patientConsentStatus),
  };
}

// ── CONVERSATION THREAD (WhatsApp-mirrored, Phase 1) ─────────────────────────────
// `GET /api/v1/bookings/{bookingId}/conversation` → ConversationMessageDto[] (from
// wa_message_log). We adapt each into the app-facing ChatMessage the thread renders:
//   - direction 'inbound'  → from 'patient' (left bubble)
//   - direction 'outbound' → from 'bot'     (right WhatsApp-tinted bubble)
//   - `content` is the message body (null for non-text types → a typed placeholder)
//   - `at` is the HH:mm send time in Asia/Kolkata.
// The live feed has no interactive-chip / system-line metadata, so those stay
// undefined (the thread renders a plain bubble). NO extra PHI — only the message
// body the patient sent over WhatsApp.
export async function getConversation(bookingId: string): Promise<ChatMessage[]> {
  const raw = await apiFetch<unknown[]>(`/bookings/${bookingId}/conversation`);
  const dtos = ConversationMessageDtoSchema.array().parse(raw);
  return dtos.map((m) => ({
    from: m.direction === 'inbound' ? ('patient' as const) : ('bot' as const),
    // A non-text message (image/template/etc.) has null content; show its type so
    // the thread isn't a blank bubble.
    text: m.content ?? `[${m.messageType}]`,
    at: toClockTime(m.sentAt),
  }));
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

// ── TENANTS LIST (begin-impersonation target picker) ──────────────────────────
// GET /api/v1/tenants → TenantDto[] (RAW), gated by `platform.tenants.read`
// (super_admin). Pure pass-through: TenantListItemSchema mirrors the DTO 1:1.

export async function listTenants(): Promise<TenantListItem[]> {
  const raw = await apiFetch<unknown[]>('/tenants?skip=0&take=200');
  return TenantListItemSchema.array().parse(raw);
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
    // The conversation THREAD is now wired (getConversation → GET
    // /bookings/{id}/conversation), but the summary endpoint still has no
    // active-conversations COUNT metric (a live WhatsApp-thread tally), so this
    // card stays 0 until /dashboard/summary emits it.
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
  action: 'approve' | 'cancel' | 'complete' | 'no-show' | 'check-in',
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

/** Check a CONFIRMED patient in at the desk (confirmed → checked_in). */
export function checkInBooking(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return bookingAction(bookingId, 'check-in', idempotencyKey, 'checked_in');
}

// ── RESCHEDULE BOOKING (Phase 1) ──────────────────────────────────────────────
// POST /bookings/{bookingId}/reschedule (Idempotency-Key). A reschedule supersedes
// the old booking with a NEW one at the chosen slot (lineage), optionally on a new
// doctor, with an optional reason. The result carries both ids + the new booking
// number + token. The new slotId is the GUID from the (already-wired) slots
// listing. The Idempotency-Key is generated ONCE by the caller and reused on retry.
export async function rescheduleBooking(
  bookingId: string,
  input: { newSlotId: string; newDoctorId?: string; reason?: string },
  idempotencyKey: string,
): Promise<{ oldBookingId: string; newBookingId: string; newBookingNumber: string | null; tokenNumber: number | null }> {
  const raw = await apiFetch<unknown>(`/bookings/${bookingId}/reschedule`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      newSlotId: input.newSlotId,
      newDoctorId: input.newDoctorId ?? null,
      reason: input.reason ?? null,
    },
  });
  const dto = RescheduleResultDtoSchema.parse(raw);
  return {
    oldBookingId: dto.oldBookingId,
    newBookingId: dto.newBookingId,
    newBookingNumber: dto.newBookingNumber,
    tokenNumber: dto.tokenNumber,
  };
}

// ── CREATE BOOKING ────────────────────────────────────────────────────────────
// POST /bookings (CreateBookingRequest). The wizard collects patient + doctor +
// slot. The slots/practitioners endpoints ARE wired (GET /doctors +
// GET /doctors/{id}/slots), so the wizard sends real doctor/slot GUIDs and a live
// create succeeds; a server validation error still surfaces gracefully via
// toUserError. The staff create path is always a `self` walk-in/dashboard booking
// (no patient OTP) — behalf+OTP is WhatsApp-only this phase, so no behalf fields
// are sent here and CreateBookingRequest's new behalf fields default server-side.
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
  // A new booking is 'pending' server-side (it still needs approval) and the
  // token may be null. Report the status FAITHFULLY as 'pending' — the earlier
  // 'confirmed' coercion mis-stated the booking; consumers read only the token.
  return { id: dto.bookingId, token: dto.tokenNumber ?? 0, status: 'pending' };
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

// ── Campaigns (admin) ────────────────────────────────────────────────────────

export async function listCampaigns(): Promise<Campaign[]> {
  const raw = await apiFetch<unknown[]>('/commission/campaigns');
  return CampaignSchema.array().parse(raw);
}

export async function createCampaign(
  req: CreateCampaignRequest,
  idempotencyKey: string,
): Promise<CommissionCreated> {
  // POST /campaigns → campaignId (Guid; bare string or { campaignId }). The list
  // refetches after invalidation, so the hook only needs an { id } back.
  const raw = await apiFetch<unknown>('/commission/campaigns', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      campaignName: req.campaignName,
      bonusType: req.bonusType,
      bonusValue: req.bonusValue ?? null,
      startsAt: req.startsAt,
      endsAt: req.endsAt,
      totalBudgetInr: req.totalBudgetInr ?? null,
    },
  });
  const id =
    typeof raw === 'string' ? raw : ((raw as { campaignId?: string })?.campaignId ?? crypto.randomUUID());
  return CommissionCreatedSchema.parse({ id });
}

// ── Form 16A (TDS 194H certificate for a PAID payout) ─────────────────────────
// Issue returns the cert DTO (PAN LAST 4 only — the full PAN is solely on the
// rendered document). Status is 'provisional' until filed on TRACES.

export async function issueForm16A(payoutId: string, idempotencyKey: string): Promise<Form16ACertificate> {
  const raw = await apiFetch<unknown>(`/commission/payouts/${payoutId}/form-16a`, {
    method: 'POST',
    idempotency: idempotencyKey,
  });
  return Form16ACertificateSchema.parse(raw);
}

/** The same-origin path to the certificate document (text/html, full PAN). The
 *  caller (openForm16ADocument) fetches it WITH auth headers and opens a transient
 *  object URL in a new tab — the document is NEVER kept in app state or cached. */
export function getForm16ADocumentUrl(payoutId: string): string {
  return `/api/v1/commission/payouts/${payoutId}/form-16a/document`;
}

/**
 * Open the Form 16A document (text/html, contains the FULL PAN) in a NEW TAB.
 * The endpoint requires the Bearer token + tenant header, so a bare window.open
 * wouldn't authenticate. We fetch it WITH auth headers into a transient blob,
 * open an object URL, and revoke it shortly after — the full-PAN HTML is NEVER
 * stored in React state, the query cache, or logged (DPDP/PHI discipline). The
 * server logs the access to key_usage_log on its side.
 */
export async function openForm16ADocument(payoutId: string): Promise<void> {
  const { accessToken, tenantId } = getSessionSnapshot();
  const headers: Record<string, string> = {};
  if (accessToken) headers['Authorization'] = `Bearer ${accessToken}`;
  if (tenantId) headers['X-Tenant-Id'] = tenantId;

  const res = await fetch(getForm16ADocumentUrl(payoutId), { headers });
  if (!res.ok) throw new ApiError(res.status, `Form 16A document failed: ${res.status}`);
  const html = await res.blob();
  const url = URL.createObjectURL(html);
  window.open(url, '_blank', 'noopener,noreferrer');
  // Revoke once the new tab has had time to load — the blob is not retained.
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

// ═════════════════════════════════════════════════════════════════════════════
// BROKER SELF-SERVICE PORTAL (/commission/me) — the Care Partner's OWN data.
// ─────────────────────────────────────────────────────────────────────────────
// IDOR-safe: the server resolves broker_id from the JWT `broker_id` claim — there
// is NO id in any path. A holder of read_self/generate_link_self/create_booking_self
// can only ever act on their own broker. POSTs carry an Idempotency-Key. The
// book-on-behalf result status is 'awaiting_patient_consent' (patient OTP, DPDP).
// ═════════════════════════════════════════════════════════════════════════════

export async function getBrokerWallet(): Promise<BrokerWallet> {
  const raw = await apiFetch<unknown>('/commission/me/wallet');
  return BrokerWalletSchema.parse(raw);
}

export async function listReferralLinks(): Promise<ReferralLink[]> {
  // ReferralLinkDto has no campaignName; fill it as null before parse (the schema
  // models it as nullable for display when a create echoes it back).
  const raw = await apiFetch<Record<string, unknown>[]>('/commission/me/links');
  const rows = raw.map((r) => ({ campaignName: null, ...r }));
  return ReferralLinkSchema.array().parse(rows);
}

export async function createReferralLink(
  req: CreateReferralLinkRequest,
  idempotencyKey: string,
): Promise<ReferralLink> {
  const raw = await apiFetch<Record<string, unknown>>('/commission/me/links', {
    method: 'POST',
    idempotency: idempotencyKey,
    // tenantId/targetDoctorId are server-resolved in the portal context.
    body: { tenantId: null, targetDoctorId: null, campaignName: req.campaignName ?? null },
  });
  return ReferralLinkSchema.parse({ campaignName: req.campaignName ?? null, ...raw });
}

export async function createPortalBooking(
  req: BrokerPortalBookingRequest,
  idempotencyKey: string,
): Promise<BrokerBookingResult> {
  const raw = await apiFetch<unknown>('/commission/me/bookings', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      patientPhone: req.patientPhone,
      patientName: req.patientName ?? null,
      patientAge: req.patientAge ?? null,
      patientGender: req.patientGender ?? null,
      slotId: req.slotId,
      doctorId: req.doctorId,
      departmentId: req.departmentId ?? null,
      chiefComplaint: req.chiefComplaint ?? null,
    },
  });
  return BrokerBookingResultSchema.parse(raw);
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

// ═════════════════════════════════════════════════════════════════════════════
// DEVELOPERS / API PLATFORM portal (Slice 02 platform_api) — READ LISTS only.
// ─────────────────────────────────────────────────────────────────────────────
// PLATFORM-ADMIN surface (gated `platform.api_clients.manage`; a tenant_owner
// gets 403 + no nav entry). The frontend zod contracts mirror the .NET DTOs 1:1,
// so the adapters are thin: apiFetch → zod-parse the RAW (bare-array) DTO →
// (mostly) return as-is. Two reconciliations:
//   1. ApiClientDto carries NO `status` field — the UI derives it from the
//      is_active/is_verified pair (same `statusOf` the mock applies).
//   2. There is NO "list-all webhooks" endpoint: GET /webhooks REQUIRES a
//      `clientId`. We fan out one call per client and flatten (tolerant per-call),
//      so the WebhooksTab sees the union across the tenant's clients.
// WRITES (register/rotate/createWebhook/status/scopes/rate-limits) stay on the
// existing mock/best-effort path — NOT wired here (do-no-harm on dangerous
// platform mutations). SECRETS are never returned by any of these READS.
// ═════════════════════════════════════════════════════════════════════════════

/** Derive the UI status from the DB's is_active/is_verified pair (mirrors the
 *  mock's statusOf — the ApiClientDto omits the computed `status`). */
function clientStatusOf(isActive: boolean, isVerified: boolean): ApiClient['status'] {
  if (!isActive) return 'suspended';
  if (!isVerified) return 'pending';
  return 'approved';
}

export async function listApiClients(): Promise<ApiClient[]> {
  const raw = await apiFetch<unknown[]>('/api-clients?skip=0&take=100');
  // The DTO has every ApiClient field except the computed `status`; fill it.
  const rows = (raw as Record<string, unknown>[]).map((r) => ({
    ...r,
    status: clientStatusOf(Boolean(r.isActive), Boolean(r.isVerified)),
  }));
  return ApiClientSchema.array().parse(rows);
}

export async function listScopes(): Promise<Scope[]> {
  const raw = await apiFetch<unknown[]>('/api-scopes');
  return ScopeSchema.array().parse(raw);
}

export async function listEventTypes(): Promise<EventType[]> {
  const raw = await apiFetch<unknown[]>('/webhooks/event-types');
  return EventTypeSchema.array().parse(raw);
}

/** No list-all webhooks endpoint exists — GET /webhooks needs a clientId. We
 *  resolve the tenant's clients first, then fetch each client's webhooks
 *  concurrently (a per-call failure is tolerated, treated as no webhooks), and
 *  flatten. An empty result renders the WebhooksTab empty state, never a crash. */
export async function listWebhooks(): Promise<WebhookSubscription[]> {
  const clientsRaw = await apiFetch<unknown[]>('/api-clients?skip=0&take=100');
  const clients = ApiClientSchema.array().parse(
    (clientsRaw as Record<string, unknown>[]).map((r) => ({
      ...r,
      status: clientStatusOf(Boolean(r.isActive), Boolean(r.isVerified)),
    })),
  );
  const lists = await Promise.all(
    clients.map((c) =>
      apiFetch<unknown[]>(`/webhooks?clientId=${encodeURIComponent(c.clientId)}`)
        .then((raw) => WebhookSubscriptionSchema.array().parse(raw))
        .catch(() => [] as WebhookSubscription[]),
    ),
  );
  return lists.flat();
}

// ── DEVELOPERS / API PLATFORM — WRITES + forensic reads (Slice 12) ────────────
// PLATFORM-ADMIN surface (gated `platform.api_clients.manage`). POSTs/PUTs carry
// the caller's stable Idempotency-Key. SECRETS (client secret / webhook signing
// secret) are returned plaintext exactly ONCE on register/rotate/createWebhook and
// flow straight from the mutation onSuccess into the one-time reveal panel — they
// are NEVER cached here, written to a query, or logged. Several mutations return
// 204 No Content; we synthesize the same ApiClientMutationResult shape the mock
// returns so the (invalidate-only) hooks are mode-blind — that result's `status`
// is non-load-bearing (the clients list refetch is the source of truth).

/** Register a new client (manual approval → created inactive/unverified). Returns
 *  the plaintext client secret ONCE. POST /api-clients (201). */
export async function registerApiClient(
  req: RegisterApiClientRequest,
  idempotencyKey: string,
): Promise<ApiClientSecretResult> {
  const raw = await apiFetch<unknown>('/api-clients', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      clientCode: req.clientCode,
      clientName: req.clientName,
      clientType: req.clientType,
      ownerEmail: req.ownerEmail,
      ownerOrganization: req.ownerOrganization ?? null,
      ownerTenantId: req.ownerTenantId ?? null,
      purpose: req.purpose,
    },
  });
  return ApiClientSecretResultSchema.parse(raw);
}

/** Rotate the client secret. Returns the new plaintext secret ONCE.
 *  POST /api-clients/{clientId}/rotate-secret (200). */
export async function rotateClientSecret(
  clientId: string,
  idempotencyKey: string,
): Promise<ApiClientSecretResult> {
  const raw = await apiFetch<unknown>(`/api-clients/${clientId}/rotate-secret`, {
    method: 'POST',
    idempotency: idempotencyKey,
  });
  return ApiClientSecretResultSchema.parse(raw);
}

/** Approve (verify+activate) / suspend a client. 204 → synthesize the mock's
 *  mutation result (status computed from the requested isActive/isVerified pair). */
export async function setClientStatus(
  clientId: string,
  next: { isActive: boolean; isVerified: boolean },
  idempotencyKey: string,
): Promise<ApiClientMutationResult> {
  await apiFetch<void>(`/api-clients/${clientId}/status`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body: { isActive: next.isActive, isVerified: next.isVerified, reason: null },
  });
  return ApiClientMutationResultSchema.parse({
    clientId,
    status: clientStatusOf(next.isActive, next.isVerified),
  });
}

/** Set per-client rate limits. PUT /api-clients/{clientId}/rate-limits (204).
 *  Rate limits don't change the client's status, so the synthesized result's
 *  status is a non-consumed placeholder — the clients list refetch is authoritative. */
export async function setClientRateLimits(
  clientId: string,
  limits: SetClientRateLimitsRequest,
  idempotencyKey: string,
): Promise<ApiClientMutationResult> {
  await apiFetch<void>(`/api-clients/${clientId}/rate-limits`, {
    method: 'PUT',
    idempotency: idempotencyKey,
    body: {
      rateLimitPerMinute: limits.rateLimitPerMinute,
      rateLimitPerDay: limits.rateLimitPerDay,
      burstLimit: limits.burstLimit,
    },
  });
  return ApiClientMutationResultSchema.parse({ clientId, status: 'approved' });
}

/** Grant/revoke the client's requestable scope set. PUT /api-clients/{clientId}/scopes
 *  (204). Scopes don't change status — the synthesized status is a non-consumed
 *  placeholder (the clients list refetch is authoritative). */
export async function setClientScopes(
  clientId: string,
  scopeKeys: string[],
  idempotencyKey: string,
): Promise<ApiClientMutationResult> {
  await apiFetch<void>(`/api-clients/${clientId}/scopes`, {
    method: 'PUT',
    idempotency: idempotencyKey,
    body: { scopeKeys },
  });
  return ApiClientMutationResultSchema.parse({ clientId, status: 'approved' });
}

/** Create a webhook subscription. Returns the signing secret ONCE. POST /webhooks
 *  (201). The server now binds the webhook's tenant from the JWT and IGNORES any
 *  body tenant_id, so we deliberately OMIT tenantId from the request body. */
export async function createWebhook(
  req: CreateWebhookRequest,
  idempotencyKey: string,
): Promise<CreateWebhookResult> {
  const raw = await apiFetch<unknown>('/webhooks', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      clientId: req.clientId,
      name: req.name,
      url: req.url,
      eventTypes: req.eventTypes,
      secret: req.secret ?? null,
      maxRetries: req.maxRetries,
      timeoutSeconds: req.timeoutSeconds,
    },
  });
  return CreateWebhookResultSchema.parse(raw);
}

/** Update mutable webhook fields (name/url/events/active). PUT /webhooks/{webhookId}
 *  (204) → return { webhookId } for mock parity (the webhooks list refetches). */
export async function updateWebhook(
  webhookId: string,
  req: UpdateWebhookRequest,
  idempotencyKey: string,
): Promise<{ webhookId: string }> {
  await apiFetch<void>(`/webhooks/${webhookId}`, {
    method: 'PUT',
    idempotency: idempotencyKey,
    body: {
      name: req.name ?? null,
      url: req.url ?? null,
      eventTypes: req.eventTypes ?? null,
      isActive: req.isActive ?? null,
    },
  });
  return { webhookId };
}

/** Delivery attempts for a webhook (newest first, metadata ONLY — no payload/
 *  secret). GET /webhooks/{webhookId}/deliveries → WebhookDeliveryDto[]. */
export async function listWebhookDeliveries(webhookId: string): Promise<WebhookDelivery[]> {
  const raw = await apiFetch<unknown[]>(`/webhooks/${webhookId}/deliveries`);
  return WebhookDeliverySchema.array().parse(raw);
}

/** Manually re-enqueue a failed/abandoned delivery. POST /webhooks/deliveries/
 *  {deliveryId}/retry (Idempotency-Key) → the re-enqueued row (status 'pending',
 *  attemptCount 0, nextRetryAt null). The API returns 404 (not found / other
 *  tenant) or 409 (status not retryable / subscription disabled); apiFetch throws
 *  an ApiError the mutation surfaces honestly (the retry action only appears on
 *  failed/abandoned rows). */
export async function retryWebhookDelivery(
  deliveryId: string,
  idempotencyKey: string,
): Promise<WebhookDelivery> {
  const raw = await apiFetch<unknown>(`/webhooks/deliveries/${deliveryId}/retry`, {
    method: 'POST',
    idempotency: idempotencyKey,
  });
  return WebhookDeliverySchema.parse(raw);
}

/** Paginated API request log (developers → Logs tab). Metadata ONLY — never
 *  bodies/headers/IP/PHI. GET /api-requests?clientId=&page=&pageSize=. */
export async function listApiRequestLogs(filter: ApiRequestLogFilter = {}): Promise<ApiRequestLogPage> {
  const params = new URLSearchParams();
  if (filter.clientId) params.set('clientId', filter.clientId);
  params.set('page', String(filter.page ?? 1));
  params.set('pageSize', String(filter.pageSize ?? 15));
  const raw = await apiFetch<unknown>(`/api-requests?${params.toString()}`);
  return ApiRequestLogPageSchema.parse(raw);
}

// ═════════════════════════════════════════════════════════════════════════════
// SECURITY & COMPLIANCE console (Slice 05 security_hardening) — READ LISTS only.
// ─────────────────────────────────────────────────────────────────────────────
// PLATFORM-ADMIN surface (each endpoint gated by a distinct platform.* perm). The
// frontend zod contracts mirror the SecurityController DTOs 1:1, so the adapters
// are pure pass-throughs: apiFetch → zod-parse → return. Several lists are
// legitimately EMPTY (deletion-certs / review-queue) → the tab renders its empty
// state, never an error. NO PHI: subject identity is a MASKED phone only; NO key
// material; verify/anchors carry hashes/metadata only.
// WRITES (break-glass / breach report / DPDP export+erase / deletion-cert gen /
// anchor) stay on the existing mock/best-effort path — NOT wired here (do-no-harm
// on dangerous platform mutations).
// ═════════════════════════════════════════════════════════════════════════════

/** GET /security/audit-chain/verify → single object (intact/breaks/lastVerifiedAt). */
export async function verifyAuditChain(): Promise<AuditChainVerify> {
  const raw = await apiFetch<unknown>('/security/audit-chain/verify');
  return AuditChainVerifySchema.parse(raw);
}

export async function listAnchors(): Promise<AuditAnchor[]> {
  const raw = await apiFetch<unknown[]>('/security/audit-chain/anchors?take=100');
  return AuditAnchorSchema.array().parse(raw);
}

export async function listDpdpRequests(): Promise<DpdpRequest[]> {
  const raw = await apiFetch<unknown[]>('/security/dpdp/requests?take=100');
  return DpdpRequestSchema.array().parse(raw);
}

export async function listBreaches(): Promise<Breach[]> {
  const raw = await apiFetch<unknown[]>('/security/breaches?take=100');
  return BreachSchema.array().parse(raw);
}

export async function listReviewQueue(): Promise<ReviewQueueItem[]> {
  const raw = await apiFetch<unknown[]>('/security/review-queue?take=100');
  return ReviewQueueItemSchema.array().parse(raw);
}

export async function listKeyStatus(): Promise<KeyStatus[]> {
  const raw = await apiFetch<unknown[]>('/security/keys');
  return KeyStatusSchema.array().parse(raw);
}

// ═════════════════════════════════════════════════════════════════════════════
// IAM — Roles & permissions privilege matrix (Slice 2). Base path /iam.
// READS are pure pass-throughs (the zod contracts mirror the IAM DTOs 1:1).
// WRITES (cell toggle, duplicate) carry an Idempotency-Key. The matrix toggle
// endpoints are idempotent: POST grants (checkbox ON), DELETE revokes (OFF).
// The DB re-checks editability (built-in roles 403 for non-super); toUserError
// surfaces that as a toast. PHI: none — these are role/permission metadata only.
// ═════════════════════════════════════════════════════════════════════════════

export async function listModules(): Promise<ModuleDto[]> {
  const raw = await apiFetch<unknown[]>('/iam/modules');
  return ModuleDtoSchema.array().parse(raw);
}

// ── CATALOG-PLANE CREATES (platform-governed, gated platform.permissions.manage) ──
// POST /iam/modules + POST /iam/permissions define NEW catalog entries (a module /
// a permission), distinct from the assignment plane (granting an existing
// permission to a role). Both carry an Idempotency-Key (de-duped on the server);
// 403 if the caller lacks platform.permissions.manage, 409 on a duplicate key —
// both surface via toUserError as a toast. PHI: none — catalog metadata only.

/** POST /iam/modules → 201 { resourceTypeId }. */
export async function createModule(
  req: CreateModuleRequest,
  idempotencyKey: string,
): Promise<CreateModuleResult> {
  const raw = await apiFetch<unknown>('/iam/modules', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      resourceKey: req.resourceKey,
      name: req.name,
      description: req.description?.trim() ? req.description.trim() : null,
      // Omit when unset → the server assigns a default order.
      displayOrder: req.displayOrder ?? null,
    },
  });
  return CreateModuleResultSchema.parse(raw);
}

/** POST /iam/permissions → 201 { permissionId }. A new permission is INERT until
 *  application code checks it — it becomes grantable in the matrix but enforces
 *  nothing on its own. */
export async function createPermission(
  req: CreatePermissionRequest,
  idempotencyKey: string,
): Promise<CreatePermissionResult> {
  const raw = await apiFetch<unknown>('/iam/permissions', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      permissionKey: req.permissionKey,
      resource: req.resource,
      action: req.action,
      scope: req.scope,
      description: req.description,
      isDangerous: req.isDangerous ?? false,
    },
  });
  return CreatePermissionResultSchema.parse(raw);
}

export async function listIamPermissions(module?: string): Promise<IamPermissionDto[]> {
  const qs = module ? `?module=${encodeURIComponent(module)}` : '';
  const raw = await apiFetch<unknown[]>(`/iam/permissions${qs}`);
  return IamPermissionDtoSchema.array().parse(raw);
}

/** GET /iam/roles/{roleId}/matrix — the heart of the screen. 404 → role unknown. */
export async function getRoleMatrix(roleId: string): Promise<RoleMatrix> {
  const raw = await apiFetch<unknown>(`/iam/roles/${roleId}/matrix`);
  return RoleMatrixSchema.parse(raw);
}

/** POST a grant (checkbox ON). Idempotent; 403 if the caller can't edit the role. */
export async function grantRolePermission(
  roleId: string,
  permissionId: string,
  idempotencyKey: string,
): Promise<RolePermissionToggleResult> {
  const raw = await apiFetch<unknown>(`/iam/roles/${roleId}/permissions/${permissionId}`, {
    method: 'POST',
    idempotency: idempotencyKey,
    // tenantId is resolved server-side from the JWT; grantable defaults true.
    body: { tenantId: null, grantable: true },
  });
  return RolePermissionToggleResultSchema.parse(raw);
}

/** DELETE a grant (checkbox OFF). Idempotent; 403 if the caller can't edit the role. */
export async function revokeRolePermission(
  roleId: string,
  permissionId: string,
  idempotencyKey: string,
): Promise<RolePermissionToggleResult> {
  const raw = await apiFetch<unknown>(`/iam/roles/${roleId}/permissions/${permissionId}`, {
    method: 'DELETE',
    idempotency: idempotencyKey,
  });
  return RolePermissionToggleResultSchema.parse(raw);
}

/** POST /iam/roles/duplicate — clone a role + its grants. 201 → { roleId }. */
export async function duplicateRole(
  req: DuplicateRoleRequest,
  idempotencyKey: string,
): Promise<DuplicateRoleResult> {
  const raw = await apiFetch<unknown>('/iam/roles/duplicate', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      sourceRoleId: req.sourceRoleId,
      newRoleKey: req.newRoleKey,
      newName: req.newName,
      description: req.description ?? null,
      tenantId: req.tenantId ?? null,
    },
  });
  return DuplicateRoleResultSchema.parse(raw);
}

/** GET /iam/users/{userId}/effective-access — resolved permission-key set.
 *  Omitting tenantId defaults to the caller's tenant server-side. */
export async function getEffectiveAccess(
  userId: string,
  tenantId?: string | null,
): Promise<EffectiveAccess> {
  const qs = tenantId ? `?tenantId=${encodeURIComponent(tenantId)}` : '';
  const raw = await apiFetch<unknown>(`/iam/users/${userId}/effective-access${qs}`);
  return EffectiveAccessSchema.parse(raw);
}

// ── ROLES + USERS + OVERRIDES (existing live endpoints used by Team & Roles) ──
// GET /roles (RoleDto[] mirrors RoleSchema 1:1) and GET /tenants/{id}/users
// (UserListItemDto[]) feed the Roles/Users tabs; POST /permission-overrides
// records a per-user override (deny-wins, reason mandatory, Idempotency-Key).

export async function listRoles(): Promise<Role[]> {
  const raw = await apiFetch<unknown[]>('/roles');
  return RoleSchema.array().parse(raw);
}

export async function listTenantUsers(): Promise<UserListItem[]> {
  const tenantId = getSessionSnapshot().tenantId;
  // The tenant comes from the path; the JWT claim re-scopes server-side anyway.
  const raw = await apiFetch<unknown[]>(`/tenants/${tenantId}/users`);
  // The server now MASKS the phone (DPDP) and returns the account security posture
  // (lockedUntil, mustChangePassword) + the user's roles in the tenant — so this is a
  // straight pass-through, no client-side masking. The list includes deactivated users
  // (isActive=false, no role chips) so the manage panel can reactivate them.
  const dtos = UserListItemDtoSchema.array().parse(raw);
  return dtos.map((d) =>
    UserListItemSchema.parse({
      userId: d.userId,
      email: d.email,
      fullName: d.fullName,
      maskedPhone: d.maskedPhone ?? null,
      isActive: d.isActive,
      mfaEnabled: d.mfaEnabled,
      lastLoginAt: d.lastLoginAt ?? null,
      lockedUntil: d.lockedUntil ?? null,
      mustChangePassword: d.mustChangePassword ?? false,
      roles: (d.roles ?? []).map((r) => ({
        userTenantRoleId: r.userTenantRoleId,
        roleId: r.roleId,
        roleKey: r.roleKey,
        name: r.name,
        isPrimary: r.isPrimary,
        expiresAt: r.expiresAt ?? null,
      })),
    }),
  );
}

/** POST /tenants/{tenantId}/users — invite/create a user (the tenant comes from the
 *  session, NEVER a body field). No password is sent: the server seeds a temp credential
 *  + must-change-password. `alreadyExisted`=true links an existing global identity. */
export async function createUser(
  req: CreateUserRequest,
  idempotencyKey: string,
): Promise<CreateUserResult> {
  const tenantId = getSessionSnapshot().tenantId;
  const raw = await apiFetch<unknown>(`/tenants/${tenantId}/users`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      email: req.email,
      fullName: req.fullName,
      phone: req.phone ?? null,
      preferredLanguage: req.preferredLanguage ?? 'en',
      initialRoleId: req.initialRoleId ?? null,
    },
  });
  return CreateUserResultSchema.parse(raw);
}

/** PUT /tenants/{tenantId}/users/{userId}/status — deactivate (revoke memberships in this
 *  tenant) or reactivate. 403 self/last-admin guards surface via toUserError. */
export async function setUserActive(
  userId: string,
  req: SetUserStatusRequest,
  idempotencyKey: string,
): Promise<SetUserStatusResult> {
  const tenantId = getSessionSnapshot().tenantId;
  const raw = await apiFetch<unknown>(`/tenants/${tenantId}/users/${userId}/status`, {
    method: 'PUT',
    idempotency: idempotencyKey,
    body: { isActive: req.isActive, reason: req.reason },
  });
  return SetUserStatusResultSchema.parse(raw);
}

/** PUT /tenants/{tenantId}/users/{userId} — edit profile (name/phone/language only). */
export async function updateUser(
  userId: string,
  req: UpdateUserProfileRequest,
  idempotencyKey: string,
): Promise<UpdateUserProfileResult> {
  const tenantId = getSessionSnapshot().tenantId;
  const raw = await apiFetch<unknown>(`/tenants/${tenantId}/users/${userId}`, {
    method: 'PUT',
    idempotency: idempotencyKey,
    body: {
      fullName: req.fullName,
      phone: req.phone ?? null,
      preferredLanguage: req.preferredLanguage ?? 'en',
    },
  });
  return UpdateUserProfileResultSchema.parse(raw);
}

/** POST /tenants/{tenantId}/users/{userId}/reset-access — force password change + unlock. */
export async function resetUserAccess(
  userId: string,
  reason: string,
  idempotencyKey: string,
): Promise<ResetAccessResult> {
  const tenantId = getSessionSnapshot().tenantId;
  const raw = await apiFetch<unknown>(`/tenants/${tenantId}/users/${userId}/reset-access`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body: { reason },
  });
  return ResetAccessResultSchema.parse(raw);
}

/** POST /role-assignments — assign a role to a user in the tenant. The new role then
 *  shows up in the user's row (the users list invalidates on success). 403 if the
 *  actor can't confer it / would escalate. */
export async function assignRole(
  req: AssignRoleRequest,
  idempotencyKey: string,
): Promise<AssignRoleResult> {
  const tenantId = req.tenantId ?? getSessionSnapshot().tenantId;
  const raw = await apiFetch<unknown>('/role-assignments', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      userId: req.userId,
      roleId: req.roleId,
      tenantId,
      expiresAt: req.expiresAt ?? null,
      isPrimary: req.isPrimary ?? false,
    },
  });
  return AssignRoleResultSchema.parse(raw);
}

/** POST /role-assignments/revoke — soft-revoke an assignment (never deletes). Idempotent
 *  (alreadyRevoked=true on a second call). A reason is mandatory and logged. */
export async function revokeRoleAssignment(
  userTenantRoleId: string,
  reason: string,
  idempotencyKey: string,
): Promise<RevokeRoleResult> {
  const raw = await apiFetch<unknown>('/role-assignments/revoke', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: { userTenantRoleId, reason },
  });
  return RevokeRoleResultSchema.parse(raw);
}

export async function setOverride(
  req: SetOverrideRequest,
  idempotencyKey: string,
): Promise<SetOverrideResult> {
  const raw = await apiFetch<unknown>('/permission-overrides', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      userId: req.userId,
      permissionKey: req.permissionKey,
      isAllowed: req.isAllowed,
      reason: req.reason,
      tenantId: req.tenantId ?? null,
      expiresAt: req.expiresAt ?? null,
    },
  });
  return SetOverrideResultSchema.parse(raw);
}

// ═════════════════════════════════════════════════════════════════════════════
// CLINICAL PHI + ABDM + CONSENT (Phase-3 slice 4) — base paths /patients,
// /prescriptions, /lab-reports, /abdm. THE MOST PHI-SENSITIVE SURFACE.
// ─────────────────────────────────────────────────────────────────────────────
// ACCESS MODEL (server-enforced; mirrored here so the seam swap is a no-op):
//  - Every clinical READ (prescriptions, lab reports, medical history, ABDM)
//    carries the declared X-Purpose-Of-Use header (apiFetch `purposeOfUse`). A
//    read WITHOUT it is a 422 — the feature query is DISABLED until the purpose
//    gate declares one, so that never reaches the wire. The CONSENT read is the
//    only clinical GET that does NOT take a purpose.
//  - A consent-denied read returns 403; the UI surfaces a contextual break-glass
//    affordance, POSTs /security/break-glass, then re-fetches the gated read.
//  - WRITES carry a stable caller-generated Idempotency-Key.
// PHI DISCIPLINE: decrypted detail (prescription/lab/history/abdm content) is
//  rendered but NEVER logged, NEVER placed in a URL/query param, and not persisted
//  beyond the Query cache's normal lifetime. The contracts mirror the C# DTOs 1:1,
//  so these adapters are thin: apiFetch (+purpose/idempotency) → zod-parse → return.
// ═════════════════════════════════════════════════════════════════════════════

// ── Consent (no purpose header — drives what the records screen shows) ─────────
export async function getPatientConsent(patientId: string): Promise<PatientConsent> {
  const raw = await apiFetch<unknown>(`/patients/${patientId}/consent`);
  return PatientConsentSchema.parse(raw);
}

// ── Prescriptions ─────────────────────────────────────────────────────────────
export async function listPrescriptions(
  patientId: string,
  purpose: string | undefined,
): Promise<PrescriptionListItem[]> {
  const raw = await apiFetch<unknown[]>(`/patients/${patientId}/prescriptions`, { purposeOfUse: purpose });
  return PrescriptionListItemSchema.array().parse(raw);
}

export async function getPrescription(
  prescriptionId: string,
  purpose: string | undefined,
): Promise<PrescriptionDetail> {
  const raw = await apiFetch<unknown>(`/prescriptions/${prescriptionId}`, { purposeOfUse: purpose });
  return PrescriptionDetailSchema.parse(raw);
}

export async function issuePrescription(
  req: IssuePrescriptionRequest,
  idempotencyKey: string,
): Promise<IssuePrescriptionResult> {
  const raw = await apiFetch<unknown>('/prescriptions', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      bookingId: req.bookingId,
      patientId: req.patientId,
      doctorId: req.doctorId,
      chiefComplaints: req.chiefComplaints ?? null,
      examination: req.examination ?? null,
      diagnosis: req.diagnosis ?? null,
      medications: req.medications,
      advice: req.advice ?? null,
      followUpInDays: req.followUpInDays ?? null,
    },
  });
  return IssuePrescriptionResultSchema.parse(raw);
}

// ── Lab reports ───────────────────────────────────────────────────────────────
export async function listLabReports(
  patientId: string,
  purpose: string | undefined,
): Promise<LabReportListItem[]> {
  const raw = await apiFetch<unknown[]>(`/patients/${patientId}/lab-reports`, { purposeOfUse: purpose });
  return LabReportListItemSchema.array().parse(raw);
}

export async function getLabReport(reportId: string, purpose: string | undefined): Promise<LabReportDetail> {
  const raw = await apiFetch<unknown>(`/lab-reports/${reportId}`, { purposeOfUse: purpose });
  return LabReportDetailSchema.parse(raw);
}

export async function uploadLabReport(
  req: UploadLabReportRequest,
  idempotencyKey: string,
): Promise<UploadLabReportResult> {
  const raw = await apiFetch<unknown>('/lab-reports', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      bookingId: req.bookingId,
      patientId: req.patientId,
      testName: req.testName,
      fileName: req.fileName ?? null,
      results: req.results,
      hasCriticalFindings: req.hasCriticalFindings,
    },
  });
  return UploadLabReportResultSchema.parse(raw);
}

/** POST /patients/{patientId}/lab-reports/{reportId}/deliver — mark a report
 *  delivered to the patient (WhatsApp). The result shape is { reportId }. */
export async function deliverLabReport(
  patientId: string,
  reportId: string,
  idempotencyKey: string,
): Promise<{ reportId: string }> {
  const raw = await apiFetch<unknown>(`/patients/${patientId}/lab-reports/${reportId}/deliver`, {
    method: 'POST',
    idempotency: idempotencyKey,
  });
  // The endpoint may return a richer DeliverLabReportResult; we only need the id.
  const id = typeof raw === 'string' ? raw : ((raw as { reportId?: string })?.reportId ?? reportId);
  return { reportId: id };
}

// ── Medical history (purpose-gated read; create/update writes) ────────────────
export async function listMedicalHistory(
  patientId: string,
  purpose: string | undefined,
): Promise<MedicalHistory[]> {
  const raw = await apiFetch<unknown[]>(`/patients/${patientId}/medical-history`, { purposeOfUse: purpose });
  return MedicalHistorySchema.array().parse(raw);
}

/** POST /patients/{patientId}/medical-history → 201 { historyId }. title/description
 *  are PHI captured server-side and encrypted at rest. */
export async function createMedicalHistory(
  patientId: string,
  req: CreateMedicalHistoryRequest,
  idempotencyKey: string,
): Promise<CreateMedicalHistoryResult> {
  const raw = await apiFetch<unknown>(`/patients/${patientId}/medical-history`, {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      recordType: req.recordType,
      title: req.title,
      description: req.description ?? null,
      severity: req.severity ?? null,
      icd10Code: req.icd10Code ?? null,
      startedDate: req.startedDate ?? null,
      endedDate: req.endedDate ?? null,
      isCritical: req.isCritical,
    },
  });
  // The endpoint returns { historyId } (or a bare Guid string).
  const historyId = typeof raw === 'string' ? raw : ((raw as { historyId?: string })?.historyId ?? crypto.randomUUID());
  return CreateMedicalHistoryResultSchema.parse({ historyId });
}

/** PUT /patients/{patientId}/medical-history/{historyId} → 200 bool (404 if not
 *  found). isActive=false retires the record. */
export async function updateMedicalHistory(
  patientId: string,
  historyId: string,
  req: UpdateMedicalHistoryRequest,
  idempotencyKey: string,
): Promise<boolean> {
  const raw = await apiFetch<unknown>(`/patients/${patientId}/medical-history/${historyId}`, {
    method: 'PUT',
    idempotency: idempotencyKey,
    body: {
      recordType: req.recordType,
      title: req.title,
      description: req.description ?? null,
      severity: req.severity ?? null,
      icd10Code: req.icd10Code ?? null,
      startedDate: req.startedDate ?? null,
      endedDate: req.endedDate ?? null,
      isActive: req.isActive,
      isCritical: req.isCritical,
    },
  });
  // A 200 with a bool body; treat anything truthy/empty as success (404 throws).
  return raw === false ? false : true;
}

// ── ABDM (consent-gated detail) ───────────────────────────────────────────────
export async function listAbdmRecords(
  patientId: string,
  purpose: string | undefined,
): Promise<AbdmRecordListItem[]> {
  const raw = await apiFetch<unknown[]>(`/patients/${patientId}/abdm-records`, { purposeOfUse: purpose });
  return AbdmRecordListItemSchema.array().parse(raw);
}

/** GET /abdm/records/{recordId} (decrypted metadata). Consent-gated server-side:
 *  a 403 surfaces as a consent-blocked state (the UI offers break-glass). The
 *  patientId is carried for the seam signature but the path keys on recordId. */
export async function getAbdmRecord(
  recordId: string,
  patientId: string,
  purpose: string | undefined,
): Promise<AbdmRecordDetail> {
  void patientId;
  const raw = await apiFetch<unknown>(`/abdm/records/${recordId}`, { purposeOfUse: purpose });
  return AbdmRecordDetailSchema.parse(raw);
}

export async function pushAbdmRecord(
  input: { patientId: string },
  idempotencyKey: string,
): Promise<PushAbdmRecordResult> {
  const raw = await apiFetch<unknown>('/abdm/records', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: { patientId: input.patientId },
  });
  // The endpoint returns { recordId } (or a bare Guid string).
  const recordId = typeof raw === 'string' ? raw : ((raw as { recordId?: string })?.recordId ?? crypto.randomUUID());
  return PushAbdmRecordResultSchema.parse({ recordId });
}

// ── Break-glass (emergency access) ────────────────────────────────────────────
// POST /security/break-glass { patientId, resourceType, resourceId, justification }
// → a grant id (Guid). After a successful grant the clinician may read a
// consent-denied record; the UI re-fetches the gated read. The justification is
// the clinician's typed reason (>=10 chars; enforced by the panel + the server).
export async function breakGlass(req: BreakGlassRequest, idempotencyKey: string): Promise<BreakGlassResult> {
  const raw = await apiFetch<unknown>('/security/break-glass', {
    method: 'POST',
    idempotency: idempotencyKey,
    body: {
      patientId: req.patientId,
      resourceType: req.resourceType,
      resourceId: req.resourceId,
      justification: req.justification,
    },
  });
  const grantId = typeof raw === 'string' ? raw : ((raw as { grantId?: string })?.grantId ?? crypto.randomUUID());
  return BreakGlassResultSchema.parse({ grantId });
}

// ═════════════════════════════════════════════════════════════════════════════
// AI ASSIST (no-show risk + triage) — two already-shipped backend capabilities.
// ─────────────────────────────────────────────────────────────────────────────
// Both are pass-throughs: apiFetch → zod-parse the RAW DTO → return it. The DTO's
// `available` flag (false = AI sibling unreachable) is preserved verbatim so the
// UI can render an "unavailable" state instead of a fabricated value.
// ═════════════════════════════════════════════════════════════════════════════

/** GET /api/v1/bookings/{bookingId}/no-show-risk → NoShowRiskDto. Gated
 *  docslot.booking.read; NO PHI, so NO purpose-of-use header. A 404 (booking not
 *  in tenant) surfaces as an ApiError the caller's query handles. */
export async function getNoShowRisk(bookingId: string): Promise<NoShowRisk> {
  const raw = await apiFetch<unknown>(`/bookings/${bookingId}/no-show-risk`);
  return NoShowRiskSchema.parse(raw);
}

/**
 * POST /api/v1/triage → TriageResultDto. Gated docslot.booking.create. NOT
 * idempotency-required (triage is advisory — no Idempotency-Key attached).
 *
 * DPDP purpose-of-use: the server REQUIRES X-Purpose-Of-Use (422 without it) when
 * `patientId` OR `bookingId` is present in the body (a patient/booking-bound
 * triage is a clinical access). When neither is present (pure free-text intake)
 * NO purpose header is needed. We forward `purposeOfUse` to apiFetch ONLY in the
 * bound case, mirroring the server's gate. PHI: `complaint` flows through the
 * request body only — it is never logged here, never placed in a query key, and
 * never returned in the result.
 */
export async function submitTriage(input: TriageRequestInput): Promise<TriageResult> {
  const boundToSubject = Boolean(input.patientId || input.bookingId);
  const raw = await apiFetch<unknown>('/triage', {
    method: 'POST',
    // Only the patient/booking-bound call carries the purpose header (server gate).
    purposeOfUse: boundToSubject ? input.purposeOfUse : undefined,
    body: {
      complaint: input.complaint,
      patientId: input.patientId ?? null,
      bookingId: input.bookingId ?? null,
      patientAge: input.patientAge ?? null,
    },
  });
  return TriageResultSchema.parse(raw);
}
