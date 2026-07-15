// Mock adapter over data.ts. Exposes the exact contract the dashboard consumes,
// shaped to mirror a platform.get_user_menus() / resolve_user_permissions()
// backend. Every function returns a Promise + zod-parses its payload, so it is a
// drop-in stand-in for the real api-client: feature hooks call these today and a
// thin apiFetch wrapper tomorrow.

// Auth + Team & Roles (Slice 01) live in ./rbac; re-exported so the single
// `@/lib/mock` import seam holds.
export * from './rbac';
// Developer / API Platform portal (Slice 02) lives in ./developers.
export * from './developers';
// Security & Compliance console (Slice 05) lives in ./security.
export * from './security';
// Team console Audit log (#86) + Active sessions (#87) live in ./team-audit-sessions.
export * from './team-audit-sessions';
// Team console token-based Invitations (#89) live in ./invitations.
export * from './invitations';
// Team console Security policy + IP allow-list (#91) live in ./security-policy.
export * from './security-policy';
// Clinical records (Slice 03b) live in ./clinical.
export * from './clinical';
// Commission / Care Partners (Slice 07) live in ./commission.
export * from './commission';
// AI assist (no-show risk + triage) lives in ./ai.
export * from './ai';
// Workspace Settings screen (Phase 1) lives in ./settings.
export * from './settings';

import { BOOKINGS, CONVERSATIONS, DAYS, DEPARTMENTS, DOCTORS, TIMES, buildSlotGrid } from '@/lib/data';
import { inr, maskPhone } from '@/lib/format';
import {
  AgentPanelSchema,
  AnalyticsSchema,
  BadgesResponseSchema,
  BookingMutationResultSchema,
  BookingRowSchema,
  CalendarGridSchema,
  ChatMessageSchema,
  CreateBookingResultSchema,
  DashboardSummarySchema,
  DepartmentLoadSchema,
  DoctorCardSchema,
  FloorDoctorSchema,
  MenusResponseSchema,
  PaymentLinkResultSchema,
  PermissionsResponseSchema,
  PractitionerSchema,
  SlotSchema,
  type AgentPanel,
  type Analytics,
  type BadgesResponse,
  type BookingMutationResult,
  type BookingRow,
  type CalendarGrid,
  type ChatMessageDTO,
  type CreateBookingResult,
  type DashboardSummary,
  type DepartmentLoad,
  type DoctorCard,
  type FloorDoctor,
  type MenusResponse,
  type PaymentLinkResult,
  type PermissionsResponse,
  type Practitioner,
  type Slot,
} from './contracts';

/** Map a prototype department id to a design-token color key (NO hex leaks). */
const DEPT_COLOR_KEY: Record<string, string> = {
  card: 'accent',
  ortho: 'info',
  gyn: 'primary',
  ped: 'primary',
  derm: 'warn',
  ent: 'primary',
  gen: 'muted',
};

/** Simulated network latency so skeleton states are exercised in dev. */
const LATENCY = 180;
function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY));
}

// ── Backend-driven navigation ───────────────────────────────────────────────
// Shapes match the Slice 01 API exactly (bare MenuNodeDto[], assembled tree).
// `icon` is a stable key the frontend maps to a lucide icon. Section headers have
// isSectionHeader=true + route=null. `badgeSource` resolves against getBadges().
// id/parentId/sortOrder are carried to mirror the wire DTO (the UI keys off
// `key`, not id).
const NAV_NODE_DEFAULTS = { parentId: null, sortOrder: 0, isSectionHeader: false, children: [] as never[] };
const HOSPITAL_MENUS: MenusResponse = [
  { id: 'm-overview', key: 'overview', label: 'Overview', labelHi: 'अवलोकन', icon: 'layout-dashboard', route: '/', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 1 },
  { id: 'm-bookings', key: 'bookings', label: 'Bookings', labelHi: 'बुकिंग', icon: 'calendar-check', route: '/bookings', badgeSource: 'pending_bookings_count', ...NAV_NODE_DEFAULTS, sortOrder: 2 },
  { id: 'm-calendar', key: 'calendar', label: 'Calendar', labelHi: 'कैलेंडर', icon: 'calendar-days', route: '/calendar', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 3 },
  { id: 'm-doctors', key: 'doctors', label: 'Doctors', labelHi: 'डॉक्टर', icon: 'stethoscope', route: '/doctors', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 4 },
  { id: 'm-patients', key: 'patients', label: 'Patients', labelHi: 'मरीज़', icon: 'users', route: '/patients', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 5 },
  { id: 'm-analytics', key: 'analytics', label: 'Analytics', labelHi: 'विश्लेषण', icon: 'bar-chart-3', route: '/analytics', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 6 },
  { id: 'm-team', key: 'team', label: 'Team & roles', labelHi: 'टीम और भूमिकाएँ', icon: 'shield', route: '/team', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 7 },
  // NOTE (schema TODO, flagged to orchestrator): real menu seeding for the
  // developer portal isn't in 08_rbac_navigation.sql yet. Mocked here so the
  // backend-driven nav still renders it; it will need a navigation_menus row +
  // menu→permission map (platform.api_clients.manage) on the backend.
  { id: 'm-developers', key: 'developers', label: 'Developers', labelHi: 'डेवलपर', icon: 'code', route: '/developers', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 8 },
  // Security & Compliance console (Slice 05). Same schema TODO as developers:
  // 08_rbac_navigation.sql has no nav row for it yet — mocked here so the
  // backend-driven nav renders it; backend needs a navigation_menus row +
  // menu→platform.audit.verify_chain (or a dedicated security menu permission) map.
  { id: 'm-security', key: 'security', label: 'Security', labelHi: 'सुरक्षा', icon: 'shield-check', route: '/security', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 9 },
  // Care Partners (Slice 07). Customer-facing label "Care Partners" — NEVER
  // "brokers"/"referral partners" (MCI 6.4). Seeded in 08_rbac_navigation.sql and
  // mirrored here for mock parity. Gated by commission.broker.read.
  { id: 'm-care-partners', key: 'care_partners', label: 'Care Partners', labelHi: 'केयर पार्टनर', icon: 'handshake', route: '/care-partners', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 10 },
  // Care Partner self-service portal (Slice 07 broker self-service). Now seeded in
  // 08_rbac_navigation.sql as menu_key 'partner_portal' (icon 'wallet', /portal),
  // gated by the self-scoped commission.broker.read_self — distinct from the admin
  // Care Partners screen above (tenant-wide commission.broker.read). Mirrored here
  // for mock parity so real-mode and mock-mode render the same node.
  { id: 'm-portal', key: 'partner_portal', label: 'My Portal', labelHi: 'मेरा पोर्टल', icon: 'wallet', route: '/portal', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 11 },
  // AI Operations (Slice 15). The backend nav row + menu→permission map now EXIST in
  // 08_rbac_navigation.sql (menu_key 'ai_ops' → docslot.report.read OR docslot.medical_history.read,
  // matching the screen's two section gates); this mock node keeps flag-off parity. NON-PHI ops summaries only.
  { id: 'm-ai-ops', key: 'ai_ops', label: 'AI Operations', labelHi: 'एआई संचालन', icon: 'sparkles', route: '/ai-ops', badgeSource: null, ...NAV_NODE_DEFAULTS, sortOrder: 12 },
];

export function getMenus(): Promise<MenusResponse> {
  return delay(MenusResponseSchema.parse(HOSPITAL_MENUS));
}

// ── Effective permissions (resolve_user_permissions, deny-wins applied) ───────
// The signed-in demo user's effective set (Priyanka R — a Tenant Admin per
// getMe()), shaped as PermissionSetDto. Includes the reception/booking keys AND
// the Team-&-Roles admin keys so the RBAC admin surface is exercisable in the
// demo. Canonical keys — match platform.permissions.permission_key in the SQL
// schema (verified against database/01_platform_core.sql + 03_docslot.sql), so
// the mock→real /me/permissions swap doesn't flip every gate to deny.
const SIGNED_IN_PERMISSIONS: PermissionsResponse = {
  userId: '00000000-0000-0000-0000-0000000admin',
  tenantId: '00000000-0000-0000-0000-00000000ap01',
  permissionKeys: [
    // Reception / booking
    'docslot.booking.read',
    'docslot.booking.create',
    'docslot.booking.approve',
    'docslot.booking.cancel',
    'docslot.booking.reschedule',
    // docslot.booking.complete gates BOTH complete AND check-in (the backend's
    // check-in endpoint is gated by docslot.booking.complete). no_show gates the
    // no-show action.
    'docslot.booking.complete',
    'docslot.booking.no_show',
    'docslot.patient.read',
    'docslot.patient.update',
    'docslot.doctor.read',
    'docslot.doctor.create',
    'docslot.slot.read',
    'docslot.analytics.read',
    // Team & roles admin
    'tenant.users.read',
    'tenant.users.create',
    'tenant.users.update',
    'tenant.roles.assign',
    'platform.overrides.read',
    'platform.overrides.grant',
    'platform.roles.manage',
    // Security policy (#91) — view + edit the tenant policy, manage the IP allow-list.
    // Granted so the Security tab renders a fully editable surface flag-off.
    'tenant.settings.read',
    'tenant.settings.update',
    'platform.ip_allowlist.manage',
    // Developer / API platform portal (Slice 02).
    'platform.api_clients.manage',
    // Security & Compliance console (Slice 05) — super_admin / DPO scope.
    'platform.audit.verify_chain',
    'platform.audit.anchor',
    'platform.audit.read',
    'platform.export_requests.process',
    'platform.deletion.certify',
    'platform.breach.read',
    'platform.anomalies.review',
    'platform.encryption_keys.read',
    'docslot.medical_access.break_glass',
    // Clinical records (Slice 03b) — the most PHI-sensitive surface.
    'docslot.prescription.read',
    'docslot.prescription.create',
    'docslot.report.read',
    'docslot.report.upload',
    'docslot.report.deliver',
    'docslot.medical_history.read',
    // Paper-prescription intake + manual history (Slice: paper-Rx). Granted in the
    // demo so the "Add paper Rx" / "Add record" launchers, the OCR "Extract with AI"
    // action, and per-row Verify/Edit are all exercisable flag-off.
    'docslot.medical_history.intake',
    'docslot.medical_history.create',
    'docslot.medical_history.update',
    'docslot.abdm.records.read',
    'docslot.abdm.records.create',
    // Commission / Care Partners (Slice 07). Granted the FULL set in the demo so
    // both sides of the approve≠execute split (and blacklist) are exercisable —
    // the UI still gates each action on its own key independently, so removing
    // any one key here hides exactly that action.
    'commission.broker.read',
    'commission.broker.invite',
    'commission.broker.suspend',
    'commission.broker.activate',
    'commission.broker.blacklist',
    'commission.attribution.read',
    'commission.attribution.override',
    'commission.rules.read',
    'commission.rules.create',
    'commission.rules.approve',
    'commission.payouts.read',
    'commission.payouts.approve',
    'commission.payouts.execute',
    'commission.dispute.raise',
    'commission.dispute.resolve',
    'commission.campaign.manage',
    // TDS / Form 16A issuance for a paid payout (section 194H).
    'commission.tds.issue',
    // Broker self-service portal (Care Partner's OWN data; server resolves
    // broker_id from the JWT). Granted in the demo so the /portal surface is
    // exercisable. Each portal action still gates on its own key independently.
    'commission.broker.read_self',
    'commission.broker.generate_link_self',
    'commission.broker.create_booking_self',
  ],
};

export function getPermissions(): Promise<PermissionsResponse> {
  return delay(PermissionsResponseSchema.parse(SIGNED_IN_PERMISSIONS));
}

// ── Batched badge counts (badge_source keys) ─────────────────────────────────
export function getBadges(): Promise<BadgesResponse> {
  const pending = BOOKINGS.filter((b) => b.status === 'pending').length;
  return delay(BadgesResponseSchema.parse({ counts: { pending_bookings_count: pending } }));
}

// ── Dashboard top-strip summary ──────────────────────────────────────────────
export function getDashboardSummary(): Promise<DashboardSummary> {
  const pending = BOOKINGS.filter((b) => b.status === 'pending');
  const summary = {
    liveQueue: pending.length,
    liveQueueWhatsapp: pending.filter((b) => b.source === 'whatsapp').length,
    liveQueueWalkIn: pending.filter((b) => b.source !== 'whatsapp').length,
    confirmedToday: BOOKINGS.filter((b) => b.status === 'confirmed').length,
    revenueToday: 38400,
    noShowRate: 4.2,
    activeConversations: 184,
  };
  return delay(DashboardSummarySchema.parse(summary));
}

// ── Bookings list ────────────────────────────────────────────────────────────
// PHI: phone is masked HERE (server-side equivalent) so the raw number never
// leaves the adapter in a list payload (DPDP). Detail panels fetch the full
// number separately.
export function listBookings(): Promise<BookingRow[]> {
  const rows: BookingRow[] = BOOKINGS.map((b) =>
    BookingRowSchema.parse({
      id: b.id,
      token: b.token,
      patient: b.patient,
      maskedPhone: maskPhone(b.phone),
      doctorName: b.doctorName,
      dept: b.dept,
      date: b.date,
      time: b.time,
      status: b.status,
      source: b.source,
      note: b.note,
      createdAgo: b.createdAgo,
      // Demographics for the queue row subline (masked phone stays the only PHI).
      age: b.age,
      gender: b.gender,
      bookedByType: b.bookedByType,
      behalfRelation: b.behalfRelation,
      patientConsentStatus: b.patientConsentStatus,
    }),
  );
  return delay(rows);
}

// ── Conversation thread for a booking ────────────────────────────────────────
export function getConversation(bookingId: string): Promise<ChatMessageDTO[]> {
  const thread = (CONVERSATIONS[bookingId] ?? []).map((m) => ChatMessageSchema.parse(m));
  return delay(thread);
}

// ── WhatsApp agent panel ─────────────────────────────────────────────────────
export function getAgentPanel(): Promise<AgentPanel> {
  // Deterministic sparkline so it renders stable across reloads.
  const sparkline = Array.from({ length: 24 }, (_, i) => {
    const v = (Math.sin(i / 2.4) + 1) / 2;
    return Math.round(v * 100) / 100;
  });
  const panel = {
    activeConversations: 184,
    sparkline,
    avgResponseMins: 1.4,
    selfServedPct: 91,
    handedPct: 38,
    dropOffPct: 6,
    funnel: [
      { key: 'greeted' as const, count: 284, pct: 100 },
      { key: 'selectedDept' as const, count: 258, pct: 91 },
      { key: 'pickedSlot' as const, count: 181, pct: 64 },
      { key: 'confirmed' as const, count: 126, pct: 44 },
    ],
  };
  return delay(AgentPanelSchema.parse(panel));
}

// ── Department load today ────────────────────────────────────────────────────
export function getDepartmentLoad(): Promise<DepartmentLoad[]> {
  const rows = DEPARTMENTS.map((d, i) => {
    const capacity = (d.count + 2) * 6;
    // Deterministic booked count derived from the seed values in the prototype.
    const booked = Math.min(capacity, Math.round(capacity * (0.45 + ((i * 13) % 40) / 100)));
    return DepartmentLoadSchema.parse({
      id: d.id,
      name: d.name,
      colorKey: DEPT_COLOR_KEY[d.id] ?? 'muted',
      booked,
      capacity,
    });
  });
  return delay(rows);
}

// ── On the floor now ─────────────────────────────────────────────────────────
export function getFloorDoctors(): Promise<FloorDoctor[]> {
  const rows = DOCTORS.slice(0, 6).map((d) =>
    FloorDoctorSchema.parse({
      id: d.id,
      name: d.name,
      spec: d.spec,
      room: d.room,
      nextSlot: d.next,
      seenToday: d.today,
      initials: d.img,
    }),
  );
  return delay(rows);
}

// ── Practitioners for a department (newBooking Slot step) ────────────────────
export function listPractitioners(deptId?: string): Promise<Practitioner[]> {
  const rows = DOCTORS.filter((d) => !deptId || d.deptId === deptId).map((d) =>
    PractitionerSchema.parse({
      id: d.id,
      name: d.name,
      spec: d.spec,
      deptId: d.deptId,
      fee: d.fee,
      room: d.room,
      next: d.next,
      initials: d.img,
    }),
  );
  return delay(rows);
}

// ── Available slots for a practitioner (newBooking Slot + bookTime) ──────────
export function listSlots(_doctorId: string): Promise<Slot[]> {
  const rows = TIMES.map((time, i) => {
    const seed = (i * 17 + 7) % 10;
    const state: Slot['state'] = seed < 1 ? 'blocked' : seed < 3 ? 'full' : seed < 4 ? 'tight' : 'open';
    return SlotSchema.parse({ time, state });
  });
  return delay(rows);
}

// ── Mutations ────────────────────────────────────────────────────────────────
// These simulate the .NET API. Every state-changing POST takes an
// `idempotencyKey` — generated ONCE per logical action by the caller — which the
// real apiFetch attaches as the `Idempotency-Key` header. A double-submit reuses
// the same key, so the server de-dupes and never double-confirms or double-charges.
//
// The mock honours this by caching results per key: a second call with the same
// key returns the first result instead of re-running, mirroring server de-dup.
const idemCache = new Map<string, unknown>();
function withIdem<T>(key: string, compute: () => T): Promise<T> {
  if (idemCache.has(key)) return delay(idemCache.get(key) as T);
  const result = compute();
  idemCache.set(key, result);
  return delay(result);
}

export function approveBooking(bookingId: string, idempotencyKey: string): Promise<BookingMutationResult> {
  return withIdem(idempotencyKey, () =>
    BookingMutationResultSchema.parse({ id: bookingId, status: 'confirmed' }),
  );
}

export function cancelBooking(
  bookingId: string,
  _reason: string,
  idempotencyKey: string,
): Promise<BookingMutationResult> {
  return withIdem(idempotencyKey, () =>
    BookingMutationResultSchema.parse({ id: bookingId, status: 'cancelled' }),
  );
}

export function createBooking(_draft: unknown, idempotencyKey: string): Promise<CreateBookingResult> {
  return withIdem(idempotencyKey, () => {
    const token = 500 + Math.floor(Math.random() * 99);
    return CreateBookingResultSchema.parse({ id: `B-${2900 + token}`, token, status: 'confirmed' });
  });
}

export function sendPaymentLink(input: {
  bookingId: string;
  amount: number;
  expiresInMins: number;
  idempotencyKey: string;
}): Promise<PaymentLinkResult> {
  return withIdem(input.idempotencyKey, () =>
    PaymentLinkResultSchema.parse({
      bookingId: input.bookingId,
      link: `upi://pay?pa=apollocare@hdfcbank&am=${input.amount}&tn=${input.bookingId}`,
      amount: input.amount,
      expiresInMins: input.expiresInMins,
    }),
  );
}

// ── Doctors directory (/doctors) ─────────────────────────────────────────────
// Practitioner cards with today's OPD load. Capacity is deterministic so the bar
// renders stable across reloads. colorKey reuses the dept→token map (no hex).
export function listDoctorCards(): Promise<DoctorCard[]> {
  // OPD windows per department id (explicit Asia/Kolkata, deterministic).
  const HOURS: Record<string, string> = {
    card: '09:00–13:00',
    ortho: '11:00–15:00',
    gyn: '10:00–14:00',
    ped: '09:30–13:30',
    derm: '13:00–17:00',
    ent: '14:00–18:00',
    gen: '09:00–17:00',
  };
  const rows = DOCTORS.map((d) => {
    const dept = DEPARTMENTS.find((x) => x.id === d.deptId);
    // Capacity sits just above today's count so the bar is meaningfully partial.
    const capacity = Math.max(d.today + 2, Math.round(d.today * 1.25) + 2);
    return DoctorCardSchema.parse({
      id: d.id,
      name: d.name,
      spec: d.spec,
      deptId: d.deptId,
      deptName: dept?.name ?? d.spec,
      colorKey: DEPT_COLOR_KEY[d.deptId] ?? 'muted',
      qualification: d.qual,
      feeInr: d.fee,
      room: d.room,
      rating: d.rating,
      initials: d.img,
      todayCount: d.today,
      todayCapacity: capacity,
      hours: HOURS[d.deptId] ?? '09:00–17:00',
      nextSlot: d.next,
    });
  });
  return delay(rows);
}

// ── Calendar week grid (/calendar) ───────────────────────────────────────────
// Reuses the prototype's deterministic load grid (buildSlotGrid → time→day cell
// states). Today is the column matching the current IST weekday within the
// rendered Mon–Sun week (the demo week is fixed; we mark Wed as "today" to mirror
// the prototype highlight). All states map to legend tokens in the screen.
export function getCalendarGrid(): Promise<CalendarGrid> {
  const grid = buildSlotGrid();
  // The demo week is the prototype's fixed "Mon 21 … Sun 27"; highlight the
  // weekday that matches today in IST, falling back to index 2 (Wed) so the
  // TODAY column is always visible in the static demo.
  const istWeekday = new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Kolkata', weekday: 'short' }).format(
    new Date(),
  );
  const order = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  const todayIdx = order.indexOf(istWeekday) >= 0 ? order.indexOf(istWeekday) : 2;

  const columns = DAYS.map((label, di) => {
    const [weekday, dayOfMonth] = label.split(' ');
    return {
      key: `d-${di}`,
      label,
      weekday,
      dayOfMonth,
      isToday: di === todayIdx,
      cells: TIMES.map((time) => {
        const cell = grid[time][di];
        return { state: cell.state, booked: cell.booked, capacity: cell.cap };
      }),
    };
  });

  return delay(
    CalendarGridSchema.parse({
      times: TIMES,
      rangeLabel: '21–27 Jun 2026',
      columns,
    }),
  );
}

// ── Analytics (/analytics) ───────────────────────────────────────────────────
// Aggregate dashboard only — no per-patient rows. Numbers are deterministic.
export function getAnalytics(): Promise<Analytics> {
  // Weekly stacked volume — WhatsApp vs direct (walk-in + phone).
  const volume = [
    { weekday: 'Mon', whatsapp: 42, direct: 18 },
    { weekday: 'Tue', whatsapp: 51, direct: 21 },
    { weekday: 'Wed', whatsapp: 64, direct: 19 },
    { weekday: 'Thu', whatsapp: 58, direct: 24 },
    { weekday: 'Fri', whatsapp: 73, direct: 22 },
    { weekday: 'Sat', whatsapp: 81, direct: 31 },
    { weekday: 'Sun', whatsapp: 38, direct: 12 },
  ];
  const total = volume.reduce((sum, v) => sum + v.whatsapp + v.direct, 0);
  const whatsappTotal = volume.reduce((sum, v) => sum + v.whatsapp, 0);
  const whatsappShare = Math.round((whatsappTotal / total) * 100);

  const topDepartments = [
    { id: 'card', name: 'Cardiology', colorKey: DEPT_COLOR_KEY.card, bookings: 318 },
    { id: 'gen', name: 'General Medicine', colorKey: DEPT_COLOR_KEY.gen, bookings: 286 },
    { id: 'gyn', name: 'Gynaecology', colorKey: DEPT_COLOR_KEY.gyn, bookings: 241 },
    { id: 'ortho', name: 'Orthopedics', colorKey: DEPT_COLOR_KEY.ortho, bookings: 173 },
    { id: 'ped', name: 'Paediatrics', colorKey: DEPT_COLOR_KEY.ped, bookings: 152 },
    { id: 'derm', name: 'Dermatology', colorKey: DEPT_COLOR_KEY.derm, bookings: 98 },
  ];

  const funnel = [
    { key: 'startedChat' as const, count: 1840, pct: 100 },
    { key: 'pickedDept' as const, count: 1502, pct: 82 },
    { key: 'pickedDoctor' as const, count: 1188, pct: 65 },
    { key: 'pickedSlot' as const, count: 964, pct: 52 },
    { key: 'confirmed' as const, count: 812, pct: 44 },
  ];

  return delay(
    AnalyticsSchema.parse({
      kpis: [
        { key: 'totalBookings', value: total.toLocaleString('en-IN'), deltaPct: 12.4, higherIsBetter: true, caption: null },
        { key: 'whatsappShare', value: `${whatsappShare}%`, deltaPct: 6.1, higherIsBetter: true, caption: null },
        { key: 'noShowRate', value: '4.2%', deltaPct: -1.3, higherIsBetter: false, caption: 'vs 8% industry' },
        { key: 'revenue', value: inr(842000), deltaPct: 9.8, higherIsBetter: true, caption: null },
      ],
      volume,
      topDepartments,
      funnel,
    }),
  );
}
