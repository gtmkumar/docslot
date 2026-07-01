// Mock adapter for the Team console's Audit log (#86) and Active sessions (#87).
// Shapes mirror mediq.SharedDataModel/Docslot/Security/{AuditReadDtos,
// SessionAdminDtos}.cs, so the mock→real swap is a no-op for zod.
//
// INVARIANTS baked in:
//  - NO PHI. Audit actors + session users are STAFF identities (the same people
//    directory as the People tab). resourceLabel is a humanized label, never
//    patient content. Rows carry an optional resolved `city` (#94, IGeoIpResolver);
//    some seeds leave it null to exercise the offline NullGeoIpResolver (raw-IP) path.
//  - Facets reflect the current date-range + search, INDEPENDENT of the selected
//    category/severity, so the counts stay stable while you toggle a facet.
//  - Session revokes mutate an in-memory list so a refetch (after invalidation)
//    reflects the change — same as the real endpoints dropping the row.
//  - Every state-changing POST takes a caller-generated Idempotency-Key (de-duped).

import {
  ActiveSessionSchema,
  AuditLogPageSchema,
  AuditLogRowSchema,
  RevokeAllSessionsResultSchema,
  type ActiveSession,
  type AuditCsvResult,
  type AuditLogFilter,
  type AuditLogPage,
  type AuditLogRow,
  type RevokeAllSessionsResult,
} from './contracts';

const LATENCY = 220;
function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY));
}

const idemCache = new Map<string, unknown>();
function withIdem<T>(key: string, compute: () => T): Promise<T> {
  if (idemCache.has(key)) return delay(idemCache.get(key) as T);
  const result = compute();
  idemCache.set(key, result);
  return delay(result);
}

const iso = (msAgo: number) => new Date(Date.now() - msAgo).toISOString();
const HOUR = 3_600_000;
const DAY = 86_400_000;

const ADMIN_USER_ID = '00000000-0000-0000-0000-0000000admin';

// #94 — a tiny offline stand-in for the server's IGeoIpResolver so the mock shows
// the "IP · city" row. Indian ranges resolve to a city; the foreign / Tor-exit IPs
// (185.220.101.44, 4.240.11.9) stay unmapped → city null, exercising the offline
// NullGeoIpResolver (raw-IP-only) path exactly as production does when unconfigured.
const CITY_BY_IP: Record<string, string> = {
  '103.21.244.12': 'Mumbai',
  '103.21.244.31': 'Pune',
  '49.36.12.88': 'New Delhi',
  '49.36.12.90': 'New Delhi',
  '49.36.44.19': 'Bengaluru',
};
const cityFor = (ip: string | null | undefined): string | null => (ip ? (CITY_BY_IP[ip] ?? null) : null);

// ── Audit timeline seed ──────────────────────────────────────────────────────
// Spread across categories, severities, and days so faceting, filtering,
// day-grouping, and pagination are all exercised. One impersonated row + one
// failed row to surface those UI states.
type Seed = Omit<AuditLogRow, 'auditId'> & { auditId?: string };

const RAW: Seed[] = [
  {
    occurredAt: iso(8 * 60_000), actorUserId: ADMIN_USER_ID, actorName: 'Priyanka R', actorEmail: 'priyanka@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Approved booking', rawAction: 'docslot.booking.approve', resourceType: 'booking', resourceLabel: 'BKG-2026-07-01042', resourceId: 'bkg-042',
    category: 'Bookings', severity: 'Informational', ipAddress: '103.21.244.12', success: true, errorCode: null,
  },
  {
    occurredAt: iso(35 * 60_000), actorUserId: 'u-2', actorName: 'Dr. Arjun Sharma', actorEmail: 'arjun.sharma@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Cancelled booking', rawAction: 'docslot.booking.cancel', resourceType: 'booking', resourceLabel: 'BKG-2026-07-01039', resourceId: 'bkg-039',
    category: 'Bookings', severity: 'Warning', ipAddress: '103.21.244.31', success: true, errorCode: null,
  },
  {
    occurredAt: iso(2 * HOUR), actorUserId: 'u-3', actorName: 'Meena R', actorEmail: 'meena.r@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Viewed patient record', rawAction: 'docslot.patient.read', resourceType: 'patient', resourceLabel: 'Patient +91 ····· ··572', resourceId: 'pat-1',
    category: 'Patients', severity: 'Informational', ipAddress: '49.36.12.88', success: true, errorCode: null,
  },
  {
    occurredAt: iso(3 * HOUR), actorUserId: 'u-2', actorName: 'Dr. Arjun Sharma', actorEmail: 'arjun.sharma@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Break-glass access', rawAction: 'security.break_glass.record', resourceType: 'medical_history', resourceLabel: 'Emergency access — cardiac', resourceId: 'mh-9',
    category: 'Security', severity: 'Critical', ipAddress: '49.36.12.90', success: true, errorCode: null,
  },
  {
    occurredAt: iso(5 * HOUR), actorUserId: 'u-4', actorName: 'Rohit Billing', actorEmail: 'rohit.billing@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Failed sign-in', rawAction: 'auth.login', resourceType: 'session', resourceLabel: null, resourceId: null,
    category: 'Security', severity: 'Warning', ipAddress: '185.220.101.44', success: false, errorCode: 'invalid_credentials',
  },
  {
    occurredAt: iso(6 * HOUR), actorUserId: ADMIN_USER_ID, actorName: 'Priyanka R', actorEmail: 'priyanka@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Executed payout', rawAction: 'commission.payout.execute', resourceType: 'payout', resourceLabel: 'PAYOUT-2026-06-0117', resourceId: 'po-117',
    category: 'Payments', severity: 'Critical', ipAddress: '103.21.244.12', success: true, errorCode: null,
  },
  {
    occurredAt: iso(1 * DAY + 2 * HOUR), actorUserId: ADMIN_USER_ID, actorName: 'Priyanka R', actorEmail: 'priyanka@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Assigned role', rawAction: 'tenant.roles.assign', resourceType: 'user', resourceLabel: 'Meena R → Front desk', resourceId: 'u-3',
    category: 'Team', severity: 'Informational', ipAddress: '103.21.244.12', success: true, errorCode: null,
  },
  {
    occurredAt: iso(1 * DAY + 4 * HOUR), actorUserId: 'u-99', actorName: 'DocSlot Support', actorEmail: 'support@docslot.io',
    impersonatorUserId: 'u-99', impersonatorName: 'DocSlot Support',
    action: 'Updated tenant settings', rawAction: 'tenant.settings.update', resourceType: 'settings', resourceLabel: 'Booking window · 30 days', resourceId: null,
    category: 'Settings', severity: 'Warning', ipAddress: '4.240.11.9', success: true, errorCode: null,
  },
  {
    occurredAt: iso(2 * DAY), actorUserId: 'u-3', actorName: 'Meena R', actorEmail: 'meena.r@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Exported analytics', rawAction: 'docslot.analytics.export', resourceType: 'report', resourceLabel: 'Revenue · June 2026', resourceId: null,
    category: 'Analytics', severity: 'Informational', ipAddress: '49.36.12.88', success: true, errorCode: null,
  },
  {
    occurredAt: iso(3 * DAY), actorUserId: 'u-2', actorName: 'Dr. Arjun Sharma', actorEmail: 'arjun.sharma@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Issued prescription', rawAction: 'docslot.prescription.issue', resourceType: 'prescription', resourceLabel: 'PRX-2026-06-00981', resourceId: 'prx-981',
    category: 'Patients', severity: 'Informational', ipAddress: '103.21.244.31', success: true, errorCode: null,
  },
  {
    occurredAt: iso(4 * DAY), actorUserId: ADMIN_USER_ID, actorName: 'Priyanka R', actorEmail: 'priyanka@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Registered API client', rawAction: 'platform.api_clients.manage', resourceType: 'api_client', resourceLabel: 'Partner integration · Practo', resourceId: 'cli-7',
    category: 'Settings', severity: 'Warning', ipAddress: '103.21.244.12', success: true, errorCode: null,
  },
  {
    occurredAt: iso(6 * DAY), actorUserId: 'u-3', actorName: 'Meena R', actorEmail: 'meena.r@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Confirmed booking', rawAction: 'docslot.booking.confirm', resourceType: 'booking', resourceLabel: 'BKG-2026-06-00912', resourceId: 'bkg-912',
    category: 'Bookings', severity: 'Informational', ipAddress: '49.36.12.88', success: true, errorCode: null,
  },
  {
    occurredAt: iso(9 * DAY), actorUserId: 'u-2', actorName: 'Dr. Arjun Sharma', actorEmail: 'arjun.sharma@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Deactivated user', rawAction: 'tenant.users.update', resourceType: 'user', resourceLabel: 'Rohit Billing', resourceId: 'u-4',
    category: 'Team', severity: 'Warning', ipAddress: '103.21.244.31', success: true, errorCode: null,
  },
  {
    occurredAt: iso(12 * DAY), actorUserId: ADMIN_USER_ID, actorName: 'Priyanka R', actorEmail: 'priyanka@apollocare.in',
    impersonatorUserId: null, impersonatorName: null,
    action: 'Reported breach', rawAction: 'security.breach.report', resourceType: 'breach', resourceLabel: 'Report bucket misconfig', resourceId: 'b-2',
    category: 'Security', severity: 'Critical', ipAddress: '103.21.244.12', success: true, errorCode: null,
  },
];

const AUDIT: AuditLogRow[] = RAW.map((r, i) =>
  // Enrich each row with a resolved city (#94) unless the seed set one explicitly.
  AuditLogRowSchema.parse({ auditId: r.auditId ?? `aud-${i + 1}`, ...r, city: r.city ?? cityFor(r.ipAddress) }),
);

function inRange(row: AuditLogRow, fromMs: number, toMs: number): boolean {
  const t = new Date(row.occurredAt).getTime();
  return t >= fromMs && t <= toMs;
}

function matchesSearch(row: AuditLogRow, q: string): boolean {
  if (!q) return true;
  const hay = [row.action, row.rawAction, row.actorName, row.actorEmail, row.resourceLabel, row.resourceType]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();
  return hay.includes(q);
}

function facetsOf(rows: AuditLogRow[], key: 'category' | 'severity'): { key: string; count: number }[] {
  const map = new Map<string, number>();
  for (const r of rows) map.set(r[key], (map.get(r[key]) ?? 0) + 1);
  return [...map.entries()].map(([k, count]) => ({ key: k, count })).sort((a, b) => b.count - a.count);
}

/** The rows in-range + matching search, but NOT narrowed by the selected
 *  category/severity — the basis for stable facet counts. */
function baseFiltered(filter: AuditLogFilter): AuditLogRow[] {
  const fromMs = new Date(filter.from).getTime();
  const toMs = new Date(filter.to).getTime();
  const q = (filter.search ?? '').trim().toLowerCase();
  return AUDIT.filter((r) => inRange(r, fromMs, toMs) && matchesSearch(r, q)).sort(
    (a, b) => new Date(b.occurredAt).getTime() - new Date(a.occurredAt).getTime(),
  );
}

function narrowed(rows: AuditLogRow[], filter: AuditLogFilter): AuditLogRow[] {
  return rows.filter(
    (r) =>
      (!filter.category || r.category === filter.category) &&
      (!filter.severity || r.severity === filter.severity),
  );
}

export function listAuditLog(filter: AuditLogFilter): Promise<AuditLogPage> {
  const base = baseFiltered(filter);
  const items = narrowed(base, filter);
  const start = (filter.page - 1) * filter.pageSize;
  const pageItems = items.slice(start, start + filter.pageSize);
  return delay(
    AuditLogPageSchema.parse({
      page: filter.page,
      pageSize: filter.pageSize,
      total: items.length,
      items: pageItems,
      // Facets are computed over the range+search set (not category/severity), so
      // the counts don't collapse to the current selection.
      categoryFacets: facetsOf(base, 'category'),
      severityFacets: facetsOf(base, 'severity'),
      from: filter.from,
      to: filter.to,
    }),
  );
}

function csvCell(value: string | null | undefined): string {
  const s = value ?? '';
  return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function exportAuditLog(filter: AuditLogFilter): Promise<AuditCsvResult> {
  const rows = narrowed(baseFiltered(filter), filter);
  const header = [
    'OccurredAt', 'Actor', 'Email', 'Impersonator', 'Action', 'RawAction',
    'Category', 'Severity', 'ResourceType', 'Resource', 'IpAddress', 'Success', 'ErrorCode',
  ];
  const lines = rows.map((r) =>
    [
      r.occurredAt, r.actorName, r.actorEmail, r.impersonatorName, r.action, r.rawAction,
      r.category, r.severity, r.resourceType, r.resourceLabel, r.ipAddress,
      r.success ? 'true' : 'false', r.errorCode,
    ]
      .map(csvCell)
      .join(','),
  );
  const content = [header.join(','), ...lines].join('\n');
  const stamp = new Date().toISOString().slice(0, 10);
  return delay({ fileName: `audit-log-${stamp}.csv`, content });
}

// ── Active sessions seed (mutable — revokes drop rows) ───────────────────────
let SESSIONS: ActiveSession[] = [
  ActiveSessionSchema.parse({
    sessionId: 's-1', userId: ADMIN_USER_ID, userName: 'Priyanka R', userEmail: 'priyanka@apollocare.in',
    ipAddress: '103.21.244.12', city: cityFor('103.21.244.12'), startedAt: iso(2 * HOUR), lastActivityAt: iso(1 * 60_000), expiresAt: iso(-10 * HOUR), isSelf: true,
  }),
  ActiveSessionSchema.parse({
    sessionId: 's-2', userId: ADMIN_USER_ID, userName: 'Priyanka R', userEmail: 'priyanka@apollocare.in',
    ipAddress: '49.36.44.19', city: cityFor('49.36.44.19'), startedAt: iso(3 * DAY), lastActivityAt: iso(20 * HOUR), expiresAt: iso(-2 * DAY), isSelf: false,
  }),
  ActiveSessionSchema.parse({
    sessionId: 's-3', userId: 'u-2', userName: 'Dr. Arjun Sharma', userEmail: 'arjun.sharma@apollocare.in',
    ipAddress: '103.21.244.31', city: cityFor('103.21.244.31'), startedAt: iso(5 * HOUR), lastActivityAt: iso(3 * 60_000), expiresAt: iso(-8 * HOUR), isSelf: false,
  }),
  ActiveSessionSchema.parse({
    sessionId: 's-4', userId: 'u-3', userName: 'Meena R', userEmail: 'meena.r@apollocare.in',
    ipAddress: '49.36.12.88', city: cityFor('49.36.12.88'), startedAt: iso(1 * DAY), lastActivityAt: iso(42 * 60_000), expiresAt: iso(-6 * HOUR), isSelf: false,
  }),
];

export function listActiveSessions(take = 100): Promise<ActiveSession[]> {
  return delay(SESSIONS.slice(0, take).map((s) => ActiveSessionSchema.parse(s)));
}

export function revokeSession(sessionId: string, idempotencyKey: string): Promise<boolean> {
  return withIdem(idempotencyKey, () => {
    const before = SESSIONS.length;
    SESSIONS = SESSIONS.filter((s) => s.sessionId !== sessionId);
    return SESSIONS.length < before;
  });
}

export function revokeAllSessions(userId: string, idempotencyKey: string): Promise<RevokeAllSessionsResult> {
  return withIdem(idempotencyKey, () => {
    const revokedCount = SESSIONS.filter((s) => s.userId === userId).length;
    SESSIONS = SESSIONS.filter((s) => s.userId !== userId);
    return RevokeAllSessionsResultSchema.parse({ userId, revokedCount });
  });
}
