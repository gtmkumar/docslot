// Mock adapter for the Developer / API Platform portal (Slice 02 platform_api).
// Shapes mirror the real .NET DTOs (mediq.SharedDataModel/Docslot/PlatformApi)
// and the seed in database/02_platform_api.sql, so the mock→real swap is a no-op
// for zod.
//
// SECURITY: the plaintext client secret / webhook signing secret exists ONLY on
// the create/rotate RESULT, generated at call time and returned once. It is
// NEVER stored in the seed, never on the list/get DTOs, and the mock has no way
// to re-fetch it — exactly like the server (only a hash persists).

import {
  ApiClientMutationResultSchema,
  ApiClientSchema,
  ApiClientSecretResultSchema,
  ApiRequestLogPageSchema,
  CreateWebhookResultSchema,
  EventTypeSchema,
  ScopeSchema,
  WebhookDeliverySchema,
  WebhookSubscriptionSchema,
  type ApiClient,
  type ApiClientMutationResult,
  type ApiClientSecretResult,
  type ApiClientStatus,
  type ApiRequestLogPage,
  type CreateWebhookRequest,
  type CreateWebhookResult,
  type EventType,
  type RegisterApiClientRequest,
  type Scope,
  type SetClientRateLimitsRequest,
  type UpdateWebhookRequest,
  type WebhookDelivery,
  type WebhookSubscription,
} from './contracts';

const LATENCY = 200;
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

/** Derive the UI status from the DB's is_active/is_verified pair. */
function statusOf(isActive: boolean, isVerified: boolean): ApiClientStatus {
  if (!isActive) return 'suspended';
  if (!isVerified) return 'pending';
  return 'approved';
}

/** Opaque secret generator (mock). The real server returns a CSPRNG secret. */
function newSecret(prefix: string): string {
  const rand = `${crypto.randomUUID()}${crypto.randomUUID()}`.replace(/-/g, '');
  return `${prefix}_${rand.slice(0, 40)}`;
}

// ── Scopes catalog (seed mirrors platform_api.api_scopes) ─────────────────────
const SCOPES: Scope[] = [
  { scopeKey: 'docslot.bookings.read', resource: 'bookings', action: 'read', description: 'Read booking data', isDangerous: false, requiresConsent: false },
  { scopeKey: 'docslot.patients.read', resource: 'patients', action: 'read', description: 'Read patient data', isDangerous: true, requiresConsent: true },
  { scopeKey: 'docslot.doctors.read', resource: 'doctors', action: 'read', description: 'Read doctor profiles', isDangerous: false, requiresConsent: false },
  { scopeKey: 'docslot.slots.read', resource: 'slots', action: 'read', description: 'Read available slots', isDangerous: false, requiresConsent: false },
  { scopeKey: 'docslot.prescriptions.read', resource: 'prescriptions', action: 'read', description: 'Read prescriptions', isDangerous: true, requiresConsent: true },
  { scopeKey: 'docslot.reports.read', resource: 'reports', action: 'read', description: 'Read lab reports', isDangerous: true, requiresConsent: true },
  { scopeKey: 'docslot.bookings.write', resource: 'bookings', action: 'create', description: 'Create/update bookings', isDangerous: false, requiresConsent: false },
  { scopeKey: 'docslot.patients.write', resource: 'patients', action: 'create', description: 'Create/update patient records', isDangerous: true, requiresConsent: false },
  { scopeKey: 'docslot.prescriptions.write', resource: 'prescriptions', action: 'create', description: 'Create prescriptions', isDangerous: true, requiresConsent: false },
  { scopeKey: 'docslot.reports.upload', resource: 'reports', action: 'create', description: 'Upload lab reports', isDangerous: false, requiresConsent: false },
  { scopeKey: 'docslot.bookings.approve', resource: 'bookings', action: 'approve', description: 'Approve pending bookings', isDangerous: true, requiresConsent: false },
  { scopeKey: 'docslot.bookings.cancel', resource: 'bookings', action: 'update', description: 'Cancel bookings', isDangerous: false, requiresConsent: false },
  { scopeKey: 'docslot.reports.deliver', resource: 'reports', action: 'update', description: 'Trigger report delivery via WhatsApp', isDangerous: false, requiresConsent: false },
  { scopeKey: 'docslot.abdm.records.fetch', resource: 'abdm_records', action: 'read', description: 'Fetch ABDM health records', isDangerous: true, requiresConsent: true },
  { scopeKey: 'docslot.abdm.records.push', resource: 'abdm_records', action: 'create', description: 'Push records to ABDM PHR', isDangerous: true, requiresConsent: true },
];

// ── Event types (seed mirrors platform_api.api_event_types) ───────────────────
const EVENT_TYPES: EventType[] = [
  { eventType: 'docslot.booking.created', resource: 'booking', action: 'created', description: 'New booking created', requiresScope: 'docslot.bookings.read', isActive: true },
  { eventType: 'docslot.booking.approved', resource: 'booking', action: 'approved', description: 'Booking approved', requiresScope: 'docslot.bookings.read', isActive: true },
  { eventType: 'docslot.booking.cancelled', resource: 'booking', action: 'cancelled', description: 'Booking cancelled', requiresScope: 'docslot.bookings.read', isActive: true },
  { eventType: 'docslot.booking.completed', resource: 'booking', action: 'completed', description: 'Booking marked complete', requiresScope: 'docslot.bookings.read', isActive: true },
  { eventType: 'docslot.booking.no_show', resource: 'booking', action: 'no_show', description: 'Patient did not show up', requiresScope: 'docslot.bookings.read', isActive: true },
  { eventType: 'docslot.patient.registered', resource: 'patient', action: 'created', description: 'New patient registered', requiresScope: 'docslot.patients.read', isActive: true },
  { eventType: 'docslot.patient.consent_granted', resource: 'patient', action: 'consent_granted', description: 'Patient granted data consent', requiresScope: 'docslot.patients.read', isActive: true },
  { eventType: 'docslot.patient.consent_revoked', resource: 'patient', action: 'consent_revoked', description: 'Patient revoked data consent', requiresScope: 'docslot.patients.read', isActive: true },
  { eventType: 'docslot.report.uploaded', resource: 'report', action: 'created', description: 'Lab report uploaded', requiresScope: 'docslot.reports.read', isActive: true },
  { eventType: 'docslot.report.delivered', resource: 'report', action: 'delivered', description: 'Report delivered to patient', requiresScope: 'docslot.reports.read', isActive: true },
];

// ── API clients (seed — NO secrets persisted) ────────────────────────────────
interface SeedClient {
  clientId: string;
  clientCode: string;
  clientName: string;
  clientType: 'first_party' | 'partner' | 'public';
  ownerTenantId: string | null;
  ownerEmail: string;
  ownerOrganization: string | null;
  isActive: boolean;
  isVerified: boolean;
  rateLimitPerMinute: number;
  rateLimitPerDay: number;
  burstLimit: number;
  grantedScopes: string[];
  createdAt: string;
  lastUsedAt: string | null;
}

const CLIENTS: SeedClient[] = [
  {
    clientId: 'c-apollo-hms', clientCode: 'apollo-hms', clientName: 'Apollo HMS Integration', clientType: 'partner',
    ownerTenantId: '00000000-0000-0000-0000-00000000ap01', ownerEmail: 'dev@apollo-hms.in', ownerOrganization: 'Apollo HMS Pvt Ltd',
    isActive: true, isVerified: true, rateLimitPerMinute: 120, rateLimitPerDay: 50000, burstLimit: 200,
    grantedScopes: ['docslot.bookings.read', 'docslot.slots.read', 'docslot.doctors.read', 'docslot.bookings.write'],
    createdAt: '2026-03-02T10:00:00+05:30', lastUsedAt: '2026-06-14T08:55:00+05:30',
  },
  {
    clientId: 'c-star-ins', clientCode: 'star-insurance', clientName: 'Star Insurance Claims', clientType: 'partner',
    ownerTenantId: null, ownerEmail: 'api@starinsurance.in', ownerOrganization: 'Star Health Insurance',
    isActive: true, isVerified: true, rateLimitPerMinute: 60, rateLimitPerDay: 10000, burstLimit: 100,
    grantedScopes: ['docslot.bookings.read', 'docslot.reports.read'],
    createdAt: '2026-04-18T14:30:00+05:30', lastUsedAt: '2026-06-13T21:10:00+05:30',
  },
  {
    clientId: 'c-pharmeasy', clientCode: 'pharmeasy', clientName: 'PharmEasy Rx Sync', clientType: 'partner',
    ownerTenantId: null, ownerEmail: 'integrations@pharmeasy.in', ownerOrganization: 'PharmEasy',
    isActive: true, isVerified: false, rateLimitPerMinute: 60, rateLimitPerDay: 10000, burstLimit: 100,
    grantedScopes: ['docslot.prescriptions.read'],
    createdAt: '2026-06-10T09:00:00+05:30', lastUsedAt: null,
  },
  {
    clientId: 'c-legacy', clientCode: 'legacy-portal', clientName: 'Legacy Web Portal', clientType: 'first_party',
    ownerTenantId: '00000000-0000-0000-0000-00000000ap01', ownerEmail: 'it@apollocare.in', ownerOrganization: null,
    isActive: false, isVerified: true, rateLimitPerMinute: 30, rateLimitPerDay: 5000, burstLimit: 50,
    grantedScopes: ['docslot.bookings.read'],
    createdAt: '2025-11-01T12:00:00+05:30', lastUsedAt: '2026-02-20T16:00:00+05:30',
  },
];

function toClient(c: SeedClient): ApiClient {
  return ApiClientSchema.parse({
    clientId: c.clientId,
    clientCode: c.clientCode,
    clientName: c.clientName,
    clientType: c.clientType,
    ownerTenantId: c.ownerTenantId,
    ownerEmail: c.ownerEmail,
    ownerOrganization: c.ownerOrganization,
    isActive: c.isActive,
    isVerified: c.isVerified,
    status: statusOf(c.isActive, c.isVerified),
    rateLimitPerMinute: c.rateLimitPerMinute,
    rateLimitPerDay: c.rateLimitPerDay,
    burstLimit: c.burstLimit,
    grantedScopes: c.grantedScopes,
    createdAt: c.createdAt,
    lastUsedAt: c.lastUsedAt,
  });
}

// ── Webhooks (seed — NO secrets persisted) ───────────────────────────────────
interface SeedWebhook {
  webhookId: string;
  clientId: string;
  tenantId: string | null;
  name: string;
  url: string;
  eventTypes: string[];
  isActive: boolean;
  consecutiveFailures: number;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  autoDisabledAt: string | null;
  createdAt: string;
}

const WEBHOOKS: SeedWebhook[] = [
  {
    webhookId: 'wh-1', clientId: 'c-apollo-hms', tenantId: '00000000-0000-0000-0000-00000000ap01',
    name: 'Booking sync', url: 'https://hooks.apollo-hms.in/docslot/bookings',
    eventTypes: ['docslot.booking.created', 'docslot.booking.approved', 'docslot.booking.cancelled'],
    isActive: true, consecutiveFailures: 0, lastSuccessAt: '2026-06-14T08:50:00+05:30', lastFailureAt: null, autoDisabledAt: null,
    createdAt: '2026-03-05T10:00:00+05:30',
  },
  {
    webhookId: 'wh-2', clientId: 'c-star-ins', tenantId: null,
    name: 'Claims report feed', url: 'https://api.starinsurance.in/webhooks/reports',
    eventTypes: ['docslot.report.delivered'],
    isActive: true, consecutiveFailures: 2, lastSuccessAt: '2026-06-12T18:00:00+05:30', lastFailureAt: '2026-06-13T21:10:00+05:30', autoDisabledAt: null,
    createdAt: '2026-04-20T14:30:00+05:30',
  },
];

function toWebhook(w: SeedWebhook): WebhookSubscription {
  return WebhookSubscriptionSchema.parse({
    webhookId: w.webhookId,
    clientId: w.clientId,
    tenantId: w.tenantId,
    name: w.name,
    url: w.url,
    eventTypes: w.eventTypes,
    maxRetries: 5,
    retryBackoff: 'exponential',
    timeoutSeconds: 30,
    isActive: w.isActive,
    consecutiveFailures: w.consecutiveFailures,
    lastSuccessAt: w.lastSuccessAt,
    lastFailureAt: w.lastFailureAt,
    autoDisabledAt: w.autoDisabledAt,
    createdAt: w.createdAt,
  });
}

const DELIVERIES: Record<string, WebhookDelivery[]> = {
  'wh-1': [
    { deliveryId: 'd-1', webhookId: 'wh-1', eventType: 'docslot.booking.created', eventId: crypto.randomUUID(), status: 'success', attemptCount: 1, responseStatusCode: 200, responseTimeMs: 142, errorMessage: null, nextRetryAt: null, createdAt: '2026-06-14T08:50:00+05:30', deliveredAt: '2026-06-14T08:50:00+05:30' },
    { deliveryId: 'd-2', webhookId: 'wh-1', eventType: 'docslot.booking.approved', eventId: crypto.randomUUID(), status: 'success', attemptCount: 1, responseStatusCode: 200, responseTimeMs: 98, errorMessage: null, nextRetryAt: null, createdAt: '2026-06-14T08:51:00+05:30', deliveredAt: '2026-06-14T08:51:00+05:30' },
  ],
  'wh-2': [
    { deliveryId: 'd-3', webhookId: 'wh-2', eventType: 'docslot.report.delivered', eventId: crypto.randomUUID(), status: 'failed', attemptCount: 3, responseStatusCode: 503, responseTimeMs: 30000, errorMessage: 'Upstream timeout', nextRetryAt: '2026-06-14T10:00:00+05:30', createdAt: '2026-06-13T21:10:00+05:30', deliveredAt: null },
    { deliveryId: 'd-4', webhookId: 'wh-2', eventType: 'docslot.report.delivered', eventId: crypto.randomUUID(), status: 'success', attemptCount: 1, responseStatusCode: 200, responseTimeMs: 220, errorMessage: null, nextRetryAt: null, createdAt: '2026-06-12T18:00:00+05:30', deliveredAt: '2026-06-12T18:00:00+05:30' },
  ],
};

// ── API request logs (seed) ──────────────────────────────────────────────────
const LOG_PATHS = [
  { method: 'GET', path: '/api/v1/bookings', scope: 'docslot.bookings.read' },
  { method: 'POST', path: '/api/v1/bookings', scope: 'docslot.bookings.write' },
  { method: 'GET', path: '/api/v1/slots', scope: 'docslot.slots.read' },
  { method: 'GET', path: '/api/v1/reports/RPT-2026-06-00481', scope: 'docslot.reports.read' },
  { method: 'GET', path: '/api/v1/doctors', scope: 'docslot.doctors.read' },
];
const LOG_CLIENTS = CLIENTS.filter((c) => c.lastUsedAt);

function buildLogs() {
  return Array.from({ length: 60 }, (_, i) => {
    const client = LOG_CLIENTS[i % LOG_CLIENTS.length];
    const p = LOG_PATHS[i % LOG_PATHS.length];
    const seed = (i * 37 + 11) % 100;
    const statusCode = seed < 78 ? 200 : seed < 86 ? 201 : seed < 92 ? 401 : seed < 96 ? 429 : 500;
    return {
      requestId: `req-${1000 + i}`,
      clientId: client.clientId,
      clientName: client.clientName,
      method: p.method,
      path: p.path,
      scopeUsed: p.scope,
      statusCode,
      responseTimeMs: 40 + (seed % 60) * 3,
      occurredAt: new Date(Date.now() - i * 7 * 60_000).toISOString(),
    };
  });
}
const LOGS = buildLogs();

// ─────────────────────────────────────────────────────────────────────────────
// QUERIES
// ─────────────────────────────────────────────────────────────────────────────

export function listApiClients(): Promise<ApiClient[]> {
  return delay(CLIENTS.map(toClient));
}

export function listScopes(): Promise<Scope[]> {
  return delay(SCOPES.map((s) => ScopeSchema.parse(s)));
}

export function listEventTypes(): Promise<EventType[]> {
  return delay(EVENT_TYPES.map((e) => EventTypeSchema.parse(e)));
}

export function listWebhooks(): Promise<WebhookSubscription[]> {
  return delay(WEBHOOKS.map(toWebhook));
}

export function listWebhookDeliveries(webhookId: string): Promise<WebhookDelivery[]> {
  return delay((DELIVERIES[webhookId] ?? []).map((d) => WebhookDeliverySchema.parse(d)));
}

export interface ApiRequestLogFilter {
  clientId?: string | null;
  page?: number;
  pageSize?: number;
}

export function listApiRequestLogs(filter: ApiRequestLogFilter = {}): Promise<ApiRequestLogPage> {
  const page = filter.page ?? 1;
  const pageSize = filter.pageSize ?? 15;
  const filtered = filter.clientId ? LOGS.filter((l) => l.clientId === filter.clientId) : LOGS;
  const start = (page - 1) * pageSize;
  const items = filtered.slice(start, start + pageSize);
  return delay(ApiRequestLogPageSchema.parse({ items, total: filtered.length, page, pageSize }));
}

// ─────────────────────────────────────────────────────────────────────────────
// MUTATIONS (Idempotency-Key per logical action; SECRET shown once on result)
// ─────────────────────────────────────────────────────────────────────────────

export function registerApiClient(
  req: RegisterApiClientRequest,
  idempotencyKey: string,
): Promise<ApiClientSecretResult> {
  return withIdem(idempotencyKey, () =>
    // Created inactive/unverified (manual approval). The secret is returned ONCE.
    ApiClientSecretResultSchema.parse({
      clientId: crypto.randomUUID(),
      clientCode: req.clientCode,
      clientSecret: newSecret('sk_live'),
    }),
  );
}

export function rotateClientSecret(clientId: string, idempotencyKey: string): Promise<ApiClientSecretResult> {
  const client = CLIENTS.find((c) => c.clientId === clientId);
  return withIdem(idempotencyKey, () =>
    ApiClientSecretResultSchema.parse({
      clientId,
      clientCode: client?.clientCode ?? 'unknown',
      clientSecret: newSecret('sk_live'),
    }),
  );
}

export function setClientStatus(
  clientId: string,
  next: { isActive: boolean; isVerified: boolean },
  idempotencyKey: string,
): Promise<ApiClientMutationResult> {
  return withIdem(idempotencyKey, () =>
    ApiClientMutationResultSchema.parse({ clientId, status: statusOf(next.isActive, next.isVerified) }),
  );
}

export function setClientRateLimits(
  clientId: string,
  _limits: SetClientRateLimitsRequest,
  idempotencyKey: string,
): Promise<ApiClientMutationResult> {
  const client = CLIENTS.find((c) => c.clientId === clientId);
  return withIdem(idempotencyKey, () =>
    ApiClientMutationResultSchema.parse({
      clientId,
      status: statusOf(client?.isActive ?? true, client?.isVerified ?? false),
    }),
  );
}

export function setClientScopes(
  clientId: string,
  _scopeKeys: string[],
  idempotencyKey: string,
): Promise<ApiClientMutationResult> {
  const client = CLIENTS.find((c) => c.clientId === clientId);
  return withIdem(idempotencyKey, () =>
    ApiClientMutationResultSchema.parse({
      clientId,
      status: statusOf(client?.isActive ?? true, client?.isVerified ?? false),
    }),
  );
}

export function createWebhook(req: CreateWebhookRequest, idempotencyKey: string): Promise<CreateWebhookResult> {
  return withIdem(idempotencyKey, () =>
    CreateWebhookResultSchema.parse({
      webhookId: crypto.randomUUID(),
      // Use the caller's secret if supplied, else generate — returned ONCE.
      signingSecret: req.secret && req.secret.length > 0 ? req.secret : newSecret('whsec'),
    }),
  );
}

export function updateWebhook(
  webhookId: string,
  _req: UpdateWebhookRequest,
  idempotencyKey: string,
): Promise<{ webhookId: string }> {
  return withIdem(idempotencyKey, () => ({ webhookId }));
}

export function retryWebhookDelivery(deliveryId: string, idempotencyKey: string): Promise<WebhookDelivery> {
  // Mirror the LIVE endpoint's re-enqueued row EXACTLY so flag-off and flag-on
  // agree and zod parses identically: a manual retry resets the row to a fresh
  // pending attempt (status 'pending', attemptCount 0, nextRetryAt null) for the
  // drain to pick up.
  return withIdem(idempotencyKey, () =>
    WebhookDeliverySchema.parse({
      deliveryId,
      webhookId: 'wh-2',
      eventType: 'docslot.report.delivered',
      eventId: crypto.randomUUID(),
      status: 'pending',
      attemptCount: 0,
      responseStatusCode: null,
      responseTimeMs: null,
      errorMessage: null,
      nextRetryAt: null,
      createdAt: new Date().toISOString(),
      deliveredAt: null,
    }),
  );
}
