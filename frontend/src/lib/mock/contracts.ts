// Contract shapes the dashboard consumes. These mirror what the .NET API will
// return so the backend can mirror them 1:1. All are zod schemas; the mock
// adapter and (later) the real api-client both parse through these, so the app
// only ever sees validated data.

import { z } from 'zod';

// ── Backend-driven navigation ────────────────────────────────────────────────
// Shapes below are aligned 1:1 with the Slice 01 .NET API (MenuNodeDto /
// PermissionSetDto / BadgesDto in mediq.SharedDataModel, camelCase over the wire)
// so the mock→real swap is a no-op for zod. The API serves `GET /api/v1/me/menus`
// as a BARE MenuNodeDto[] (the server already assembled the flat
// platform.get_user_menus() rows into a parent→children tree, permission- and
// tenant-type-filtered). There is no { tenantType, items } wrapper.

/** A node in the backend-driven navigation tree. Mirrors MenuNodeDto. */
export const MenuNodeSchema: z.ZodType<MenuNode> = z.lazy(() =>
  z.object({
    id: z.string(),
    parentId: z.string().nullable(),
    key: z.string(),
    label: z.string(),
    // Backend columns menu_label_hi / menu_icon are nullable — tolerate null.
    labelHi: z.string().nullable(),
    icon: z.string().nullable(),
    route: z.string().nullable(),
    sortOrder: z.number(),
    // Server-decided non-clickable group label (replaces the old route===null convention).
    isSectionHeader: z.boolean(),
    badgeSource: z.string().nullable(),
    children: z.array(MenuNodeSchema),
  }),
);

export interface MenuNode {
  /** navigation_menus.menu_id (UUID). */
  id: string;
  /** parent_menu_id; null at the root. */
  parentId: string | null;
  /** Stable dotted key, e.g. `bookings.today` (menu_key). The frontend keys off this. */
  key: string;
  /** English label (menu_label, NOT NULL). */
  label: string;
  /** Hindi label (menu_label_hi, nullable) — rendered with the Devanagari font token. */
  labelHi: string | null;
  /** Icon key → mapped to a lucide icon in the UI lookup (menu_icon, nullable). */
  icon: string | null;
  /** Route path (menu_url, nullable — section headers have none). */
  route: string | null;
  /** Sibling ordering (display_order). */
  sortOrder: number;
  /** True for non-clickable group labels (is_section_header). */
  isSectionHeader: boolean;
  /** Optional badge feed key (badge_source, nullable) → resolved via getBadges(). */
  badgeSource: string | null;
  /** Child nodes (empty, not null, for leaves). */
  children: MenuNode[];
}

/** `GET /api/v1/me/menus` → bare MenuNodeDto[]. */
export const MenusResponseSchema = z.array(MenuNodeSchema);
export type MenusResponse = z.infer<typeof MenusResponseSchema>;

/** Effective permission set. Mirrors PermissionSetDto (userId, tenantId, permissionKeys). */
export const PermissionsResponseSchema = z.object({
  userId: z.string(),
  tenantId: z.string(),
  permissionKeys: z.array(z.string()),
});
export type PermissionsResponse = z.infer<typeof PermissionsResponseSchema>;

/** Batched badge counts. Mirrors BadgesDto { counts: Record<badge_source, int> }. */
export const BadgesResponseSchema = z.object({
  counts: z.record(z.string(), z.number()),
});
export type BadgesResponse = z.infer<typeof BadgesResponseSchema>;

/** Top-strip KPIs on the Overview dashboard. */
export const DashboardSummarySchema = z.object({
  liveQueue: z.number(),
  liveQueueWhatsapp: z.number(),
  liveQueueWalkIn: z.number(),
  confirmedToday: z.number(),
  revenueToday: z.number(),
  noShowRate: z.number(),
  activeConversations: z.number(),
});
export type DashboardSummary = z.infer<typeof DashboardSummarySchema>;

// Canonical wire enums — mirror the SQL CHECK constraints (snake_case). Defined
// once and reused so the list/mutation payloads never drift from the API. Human
// display labels live in i18n (status.* / source.*), never here.
export const BookingStatusSchema = z.enum([
  'pending',
  'confirmed',
  'cancelled',
  'completed',
  'no_show',
  'rescheduled',
]);
export const BookingSourceSchema = z.enum(['whatsapp', 'dashboard', 'api', 'walk_in', 'phone_call']);

/** Minimal booking row the list endpoint returns (mirrors Booking domain type).
 *  PHI: phone is NEVER sent raw in a list/aggregate payload (DPDP). The list
 *  carries `maskedPhone` only; a row action needing the full number fetches it
 *  via a separate booking-detail call. Mirrors BookingListItemDto.MaskedPhone. */
export const BookingRowSchema = z.object({
  id: z.string(),
  token: z.number(),
  patient: z.string(),
  maskedPhone: z.string(),
  doctorName: z.string(),
  dept: z.string(),
  date: z.string(),
  time: z.string(),
  status: BookingStatusSchema,
  source: BookingSourceSchema,
  note: z.string(),
  createdAgo: z.string(),
});
export type BookingRow = z.infer<typeof BookingRowSchema>;

export const ChatMessageSchema = z.object({
  from: z.enum(['patient', 'bot']),
  text: z.string(),
  at: z.string(),
  interactive: z.array(z.string()).optional(),
  system: z.boolean().optional(),
});
export type ChatMessageDTO = z.infer<typeof ChatMessageSchema>;

// ── WhatsApp agent panel ─────────────────────────────────────────────────────
export const AgentPanelSchema = z.object({
  activeConversations: z.number(),
  /** 24h sparkline series (conversation volume), normalised 0..1. */
  sparkline: z.array(z.number()),
  avgResponseMins: z.number(),
  selfServedPct: z.number(),
  handedPct: z.number(),
  dropOffPct: z.number(),
  funnel: z.array(
    z.object({
      key: z.enum(['greeted', 'selectedDept', 'pickedSlot', 'confirmed']),
      count: z.number(),
      pct: z.number(),
    }),
  ),
});
export type AgentPanel = z.infer<typeof AgentPanelSchema>;

// ── Department load ──────────────────────────────────────────────────────────
export const DepartmentLoadSchema = z.object({
  id: z.string(),
  name: z.string(),
  /** token color key (e.g. 'card') used for the bar accent — NOT a hex. */
  colorKey: z.string(),
  booked: z.number(),
  capacity: z.number(),
});
export type DepartmentLoad = z.infer<typeof DepartmentLoadSchema>;

// ── On the floor now ─────────────────────────────────────────────────────────
export const FloorDoctorSchema = z.object({
  id: z.string(),
  name: z.string(),
  spec: z.string(),
  room: z.string(),
  /** Next slot, 24h Asia/Kolkata. */
  nextSlot: z.string(),
  seenToday: z.number(),
  initials: z.string(),
});
export type FloorDoctor = z.infer<typeof FloorDoctorSchema>;

// ── Slot availability (newBooking Slot step + bookTime) ──────────────────────
export const SlotSchema = z.object({
  /** 24h Asia/Kolkata. */
  time: z.string(),
  state: z.enum(['open', 'tight', 'full', 'blocked']),
  /** Live-mode only: the server slot GUID required to create a booking. The mock
   *  omits it (the mock create ignores the draft), so this is optional/additive. */
  slotId: z.string().optional(),
});
export type Slot = z.infer<typeof SlotSchema>;

export const PractitionerSchema = z.object({
  id: z.string(),
  name: z.string(),
  spec: z.string(),
  deptId: z.string(),
  fee: z.number(),
  room: z.string(),
  next: z.string(),
  initials: z.string(),
});
export type Practitioner = z.infer<typeof PractitionerSchema>;

// ── Mutation results ─────────────────────────────────────────────────────────
export const BookingMutationResultSchema = z.object({
  id: z.string(),
  status: BookingStatusSchema,
});
export type BookingMutationResult = z.infer<typeof BookingMutationResultSchema>;

export const CreateBookingResultSchema = z.object({
  id: z.string(),
  token: z.number(),
  status: z.literal('confirmed'),
});
export type CreateBookingResult = z.infer<typeof CreateBookingResultSchema>;

export const PaymentLinkResultSchema = z.object({
  bookingId: z.string(),
  link: z.string(),
  amount: z.number(),
  expiresInMins: z.number(),
});
export type PaymentLinkResult = z.infer<typeof PaymentLinkResultSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// AUTH — mirrors mediq.SharedDataModel/Docslot/Auth/AuthDtos.cs (camelCase wire)
// ─────────────────────────────────────────────────────────────────────────────

/** `POST /api/v1/auth/login` body. Mirrors LoginRequest. */
export const LoginRequestSchema = z.object({
  email: z.string(),
  password: z.string(),
  tenantId: z.string().nullable().optional(),
  deviceInfo: z.string().nullable().optional(),
});
export type LoginRequest = z.infer<typeof LoginRequestSchema>;

/** login/refresh response. Mirrors TokenResponse. */
export const TokenResponseSchema = z.object({
  accessToken: z.string(),
  refreshToken: z.string(),
  expiresInSeconds: z.number(),
  userId: z.string(),
  activeTenantId: z.string().nullable(),
  mfaRequired: z.boolean(),
});
export type TokenResponse = z.infer<typeof TokenResponseSchema>;

/** One tenant the signed-in user may switch into. Mirrors MeTenantDto. */
export const MeTenantSchema = z.object({
  tenantId: z.string(),
  tenantCode: z.string(),
  displayName: z.string(),
  tenantType: z.string(),
  isPrimary: z.boolean(),
});
export type MeTenant = z.infer<typeof MeTenantSchema>;

// ── Support impersonation (issue #3) ─────────────────────────────────────────
// A support actor begins impersonation; the server mints an access token whose
// signed `impersonated_tenant` claim scopes every subsequent request to the
// target tenant. /end mints a CLEAN bundle (no claim). Mirrors the
// AuthController impersonation records (camelCase wire).

/** `POST /api/v1/auth/impersonation/begin` body. Mirrors BeginImpersonationRequest. */
export const BeginImpersonationRequestSchema = z.object({
  targetTenantId: z.string(),
  reason: z.string(),
  refreshToken: z.string(),
  targetUserId: z.string().nullable().optional(),
  ttlMinutes: z.number().nullable().optional(),
  breakGlass: z.boolean().nullable().optional(),
});
export type BeginImpersonationRequest = z.infer<typeof BeginImpersonationRequestSchema>;

/** `POST /api/v1/auth/impersonation/begin` result. The nested `token` is a full
 *  TokenResponse whose accessToken carries the `impersonated_tenant` claim. */
export const BeginImpersonationResultSchema = z.object({
  token: TokenResponseSchema,
  impersonationId: z.string(),
  targetTenantId: z.string(),
  expiresAtUtc: z.string(),
});
export type BeginImpersonationResult = z.infer<typeof BeginImpersonationResultSchema>;

/** `POST /api/v1/auth/impersonation/end` body. Mirrors EndImpersonationRequest. */
export const EndImpersonationRequestSchema = z.object({
  impersonationId: z.string(),
  refreshToken: z.string(),
});
export type EndImpersonationRequest = z.infer<typeof EndImpersonationRequestSchema>;

/** `POST /api/v1/auth/impersonation/end` result — a CLEAN TokenResponse bundle
 *  with NO `impersonated_tenant` claim. */
export const EndImpersonationResultSchema = TokenResponseSchema;
export type EndImpersonationResult = z.infer<typeof EndImpersonationResultSchema>;

/** `GET /api/v1/me`. Mirrors MeDto. */
export const MeSchema = z.object({
  userId: z.string(),
  email: z.string(),
  fullName: z.string(),
  preferredLanguage: z.string(),
  timezone: z.string(),
  mfaEnabled: z.boolean(),
  activeTenantId: z.string().nullable(),
  tenants: z.array(MeTenantSchema),
});
export type Me = z.infer<typeof MeSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// ADMIN / RBAC — mirrors mediq.SharedDataModel/Docslot/Admin/AdminDtos.cs
// ─────────────────────────────────────────────────────────────────────────────

/** A user within the active tenant. Mirrors UserListItemDto. PHI: phone is masked
 *  in the list adapter (DPDP) — raw phone never in an aggregate payload. */
export const UserListItemSchema = z.object({
  userId: z.string(),
  email: z.string(),
  fullName: z.string(),
  maskedPhone: z.string().nullable(),
  isActive: z.boolean(),
  mfaEnabled: z.boolean(),
  lastLoginAt: z.string().nullable(),
  // Roles the user holds in this tenant — assembled for the list row (the backend
  // join over user_tenant_roles → roles). Each carries the assignment id so the
  // manage panel can revoke without a second lookup.
  roles: z.array(
    z.object({
      userTenantRoleId: z.string(),
      roleId: z.string(),
      roleKey: z.string(),
      name: z.string(),
      isPrimary: z.boolean(),
      expiresAt: z.string().nullable(),
    }),
  ),
});
export type UserListItem = z.infer<typeof UserListItemSchema>;

/** `POST /tenants/{id}/users` body. Mirrors CreateUserRequest. */
export const CreateUserRequestSchema = z.object({
  email: z.string(),
  fullName: z.string(),
  phone: z.string().nullable().optional(),
  password: z.string().nullable().optional(),
  preferredLanguage: z.string().default('en'),
  initialRoleId: z.string().nullable().optional(),
});
export type CreateUserRequest = z.infer<typeof CreateUserRequestSchema>;

export const CreateUserResultSchema = z.object({ userId: z.string() });
export type CreateUserResult = z.infer<typeof CreateUserResultSchema>;

/** A role. Mirrors RoleDto. `isSystem` roles are read-only; `tenantId===null` = system. */
export const RoleSchema = z.object({
  roleId: z.string(),
  roleKey: z.string(),
  name: z.string(),
  scope: z.enum(['platform', 'tenant']),
  isSystem: z.boolean(),
  tenantId: z.string().nullable(),
});
export type Role = z.infer<typeof RoleSchema>;

/** A permission in the registry (for role/override pickers). Mirrors platform.permissions. */
export const PermissionDefSchema = z.object({
  permissionKey: z.string(),
  resource: z.string(),
  action: z.string(),
  scope: z.enum(['platform', 'tenant', 'self']),
  description: z.string(),
  /** Requires extra confirmation in the UI (platform.permissions.is_dangerous). */
  isDangerous: z.boolean(),
});
export type PermissionDef = z.infer<typeof PermissionDefSchema>;

/** `POST /api/v1/role-assignments` body. Mirrors AssignRoleRequest. */
export const AssignRoleRequestSchema = z.object({
  userId: z.string(),
  roleId: z.string(),
  tenantId: z.string().nullable().optional(),
  expiresAt: z.string().nullable().optional(),
  isPrimary: z.boolean().default(false),
});
export type AssignRoleRequest = z.infer<typeof AssignRoleRequestSchema>;

export const AssignRoleResultSchema = z.object({ userTenantRoleId: z.string() });
export type AssignRoleResult = z.infer<typeof AssignRoleResultSchema>;

/** `POST /api/v1/permission-overrides` body. Mirrors SetOverrideRequest.
 *  Reason is MANDATORY; isAllowed=false (deny) wins over any role grant. */
export const SetOverrideRequestSchema = z.object({
  userId: z.string(),
  permissionKey: z.string(),
  isAllowed: z.boolean(),
  reason: z.string(),
  tenantId: z.string().nullable().optional(),
  expiresAt: z.string().nullable().optional(),
});
export type SetOverrideRequest = z.infer<typeof SetOverrideRequestSchema>;

export const SetOverrideResultSchema = z.object({ overrideId: z.string() });
export type SetOverrideResult = z.infer<typeof SetOverrideResultSchema>;

/** An active per-user override row (for the manage panel's current-overrides list). */
export const UserOverrideSchema = z.object({
  overrideId: z.string(),
  permissionKey: z.string(),
  isAllowed: z.boolean(),
  reason: z.string(),
  expiresAt: z.string().nullable(),
});
export type UserOverride = z.infer<typeof UserOverrideSchema>;

/** One row of the "why does X have Y" effective-permission explainer.
 *  Mirrors platform.v_user_effective_permissions (source ∈ role | override_grant). */
export const EffectivePermissionSchema = z.object({
  permissionKey: z.string(),
  source: z.enum(['role', 'override_grant']),
  /** When source==='role', which role granted it (UI affordance, not in the raw view). */
  via: z.string().nullable(),
});
export type EffectivePermission = z.infer<typeof EffectivePermissionSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// DEVELOPER / API PLATFORM (Slice 02 platform_api) — mirrors
// mediq.SharedDataModel/Docslot/PlatformApi/{ApiClient,OAuth,Webhook}Dtos.cs
// (camelCase wire). SECRETS: the plaintext secret/token is only present on the
// create/rotate RESULT schemas; the list/get DTOs NEVER carry it.
// ─────────────────────────────────────────────────────────────────────────────

/** UI status, derived from is_active + is_verified (the DB has no single status
 *  column): unverified → 'pending', verified+active → 'approved', inactive →
 *  'suspended'. Carried explicitly so the list doesn't recompute it everywhere. */
export const ApiClientStatusSchema = z.enum(['pending', 'approved', 'suspended']);
export type ApiClientStatus = z.infer<typeof ApiClientStatusSchema>;

/** API client summary. Mirrors ApiClientDto. NEVER carries the secret/hash. */
export const ApiClientSchema = z.object({
  clientId: z.string(),
  clientCode: z.string(),
  clientName: z.string(),
  clientType: z.enum(['first_party', 'partner', 'public']),
  ownerTenantId: z.string().nullable(),
  ownerEmail: z.string(),
  ownerOrganization: z.string().nullable(),
  isActive: z.boolean(),
  isVerified: z.boolean(),
  status: ApiClientStatusSchema,
  rateLimitPerMinute: z.number(),
  rateLimitPerDay: z.number(),
  burstLimit: z.number(),
  grantedScopes: z.array(z.string()),
  createdAt: z.string(),
  lastUsedAt: z.string().nullable(),
});
export type ApiClient = z.infer<typeof ApiClientSchema>;

/** `POST /api/v1/api-clients` body. Mirrors RegisterApiClientRequest. */
export const RegisterApiClientRequestSchema = z.object({
  clientCode: z.string(),
  clientName: z.string(),
  clientType: z.enum(['first_party', 'partner', 'public']),
  ownerEmail: z.string(),
  ownerOrganization: z.string().nullable().optional(),
  ownerTenantId: z.string().nullable().optional(),
  purpose: z.string(),
});
export type RegisterApiClientRequest = z.infer<typeof RegisterApiClientRequestSchema>;

/** Register/rotate result. Mirrors ApiClientSecretResult. The plaintext
 *  `clientSecret` is returned ONCE and never re-fetchable. */
export const ApiClientSecretResultSchema = z.object({
  clientId: z.string(),
  clientCode: z.string(),
  clientSecret: z.string(),
});
export type ApiClientSecretResult = z.infer<typeof ApiClientSecretResultSchema>;

/** Approve/suspend a client. Mirrors SetClientStatusRequest. */
export const SetClientStatusRequestSchema = z.object({
  isActive: z.boolean(),
  isVerified: z.boolean(),
  reason: z.string().nullable().optional(),
});
export type SetClientStatusRequest = z.infer<typeof SetClientStatusRequestSchema>;

/** Set per-client rate limits. Mirrors SetClientRateLimitsRequest. */
export const SetClientRateLimitsRequestSchema = z.object({
  rateLimitPerMinute: z.number(),
  rateLimitPerDay: z.number(),
  burstLimit: z.number(),
});
export type SetClientRateLimitsRequest = z.infer<typeof SetClientRateLimitsRequestSchema>;

/** Grant/revoke client scopes. Mirrors SetClientScopesRequest. */
export const SetClientScopesRequestSchema = z.object({
  scopeKeys: z.array(z.string()),
});
export type SetClientScopesRequest = z.infer<typeof SetClientScopesRequestSchema>;

/** One API scope from the registry. Mirrors ScopeDto / platform_api.api_scopes. */
export const ScopeSchema = z.object({
  scopeKey: z.string(),
  resource: z.string(),
  action: z.string(),
  description: z.string(),
  isDangerous: z.boolean(),
  requiresConsent: z.boolean(),
});
export type Scope = z.infer<typeof ScopeSchema>;

/** Webhook subscription. Mirrors WebhookSubscriptionDto. NEVER carries the secret. */
export const WebhookSubscriptionSchema = z.object({
  webhookId: z.string(),
  clientId: z.string(),
  tenantId: z.string().nullable(),
  name: z.string(),
  url: z.string(),
  eventTypes: z.array(z.string()),
  maxRetries: z.number(),
  retryBackoff: z.string(),
  timeoutSeconds: z.number(),
  isActive: z.boolean(),
  consecutiveFailures: z.number(),
  lastSuccessAt: z.string().nullable(),
  lastFailureAt: z.string().nullable(),
  autoDisabledAt: z.string().nullable(),
  createdAt: z.string(),
});
export type WebhookSubscription = z.infer<typeof WebhookSubscriptionSchema>;

/** `POST /api/v1/webhooks` body. Mirrors CreateWebhookRequest. */
export const CreateWebhookRequestSchema = z.object({
  clientId: z.string(),
  tenantId: z.string().nullable().optional(),
  name: z.string(),
  url: z.string(),
  eventTypes: z.array(z.string()),
  secret: z.string().nullable().optional(),
  maxRetries: z.number().default(5),
  timeoutSeconds: z.number().default(30),
});
export type CreateWebhookRequest = z.infer<typeof CreateWebhookRequestSchema>;

/** Create-webhook result. Mirrors CreateWebhookResult. SigningSecret shown ONCE. */
export const CreateWebhookResultSchema = z.object({
  webhookId: z.string(),
  signingSecret: z.string(),
});
export type CreateWebhookResult = z.infer<typeof CreateWebhookResultSchema>;

/** Update mutable webhook fields. Mirrors UpdateWebhookRequest. */
export const UpdateWebhookRequestSchema = z.object({
  name: z.string().nullable().optional(),
  url: z.string().nullable().optional(),
  eventTypes: z.array(z.string()).nullable().optional(),
  isActive: z.boolean().nullable().optional(),
});
export type UpdateWebhookRequest = z.infer<typeof UpdateWebhookRequestSchema>;

/** One delivery attempt. Mirrors WebhookDeliveryDto. */
export const WebhookDeliverySchema = z.object({
  deliveryId: z.string(),
  webhookId: z.string(),
  eventType: z.string(),
  eventId: z.string(),
  status: z.enum(['pending', 'processing', 'success', 'failed', 'abandoned']),
  attemptCount: z.number(),
  responseStatusCode: z.number().nullable(),
  responseTimeMs: z.number().nullable(),
  errorMessage: z.string().nullable(),
  nextRetryAt: z.string().nullable(),
  createdAt: z.string(),
  deliveredAt: z.string().nullable(),
});
export type WebhookDelivery = z.infer<typeof WebhookDeliverySchema>;

/** One subscribable event type. Mirrors EventTypeDto / platform_api.api_event_types. */
export const EventTypeSchema = z.object({
  eventType: z.string(),
  resource: z.string(),
  action: z.string(),
  description: z.string(),
  requiresScope: z.string().nullable(),
  isActive: z.boolean(),
});
export type EventType = z.infer<typeof EventTypeSchema>;

/** One API request log row. Built to spec from platform_api.api_requests (no
 *  backend DTO yet — reconciliation pass will confirm field names). */
export const ApiRequestLogSchema = z.object({
  requestId: z.string(),
  clientId: z.string().nullable(),
  clientName: z.string().nullable(),
  method: z.string(),
  path: z.string(),
  /** Scope used to authorise the call, if any (from the token). */
  scopeUsed: z.string().nullable(),
  statusCode: z.number(),
  responseTimeMs: z.number().nullable(),
  occurredAt: z.string(),
});
export type ApiRequestLog = z.infer<typeof ApiRequestLogSchema>;

/** Cursor/offset page wrapper for the request-log list. */
export const ApiRequestLogPageSchema = z.object({
  items: z.array(ApiRequestLogSchema),
  total: z.number(),
  page: z.number(),
  pageSize: z.number(),
});
export type ApiRequestLogPage = z.infer<typeof ApiRequestLogPageSchema>;

/** Mutation result for approve/suspend/rate-limit/scopes/webhook-update/retry. */
export const ApiClientMutationResultSchema = z.object({
  clientId: z.string(),
  status: ApiClientStatusSchema,
});
export type ApiClientMutationResult = z.infer<typeof ApiClientMutationResultSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// SECURITY & COMPLIANCE (Slice 05 security_hardening) — mirrors SecurityController
// result records in mediq.Application/Features/Security (camelCase wire).
// SENSITIVE SURFACE: no PHI on screen. Subject identity is a MASKED phone only;
// the deletion certificate's plaintext is shown once on the erase result.
// ─────────────────────────────────────────────────────────────────────────────

/** One mismatched link in the audit hash chain. Mirrors AuditChainBreak. */
export const AuditChainBreakSchema = z.object({
  sequence: z.number(),
  auditId: z.string(),
  expectedHash: z.string(),
  actualHash: z.string(),
});
export type AuditChainBreak = z.infer<typeof AuditChainBreakSchema>;

/** `GET /security/audit-chain/verify`. Mirrors AuditChainVerifyResult.
 *  `lastVerifiedAt` is a UI convenience (not in the raw record — flagged). */
export const AuditChainVerifySchema = z.object({
  intact: z.boolean(),
  breaks: z.array(AuditChainBreakSchema),
  lastVerifiedAt: z.string().nullable(),
});
export type AuditChainVerify = z.infer<typeof AuditChainVerifySchema>;

/** `POST /security/audit-chain/anchor` result. Mirrors AuditAnchorResult. */
export const AuditAnchorResultSchema = z.object({
  anchorId: z.string(),
  headSequence: z.number(),
  headHash: z.string(),
});
export type AuditAnchorResult = z.infer<typeof AuditAnchorResultSchema>;

/** One past anchor (for the anchor-history list). Mirrors platform.audit_anchors.
 *  NO backend GET yet — built to spec (flagged). */
export const AuditAnchorSchema = z.object({
  anchorId: z.string(),
  chainHeadSequence: z.number(),
  chainHeadHash: z.string(),
  anchorType: z.enum(['transparency_log', 'notary_api', 'blockchain', 'paper_print', 'external_storage']),
  anchorReference: z.string(),
  anchoredAt: z.string(),
});
export type AuditAnchor = z.infer<typeof AuditAnchorSchema>;

/** `POST /security/dpdp/export` result. Mirrors DataExportResult. `bundleJson` is
 *  the FHIR-R4 bundle — we expose only its size/checksum + a download handle, never
 *  render it inline (it contains the subject's data). */
export const DataExportResultSchema = z.object({
  requestId: z.string(),
  format: z.string(),
  recordCount: z.number(),
  checksum: z.string(),
  /** Opaque download handle for the bundle (not the bundle contents). */
  downloadToken: z.string(),
});
export type DataExportResult = z.infer<typeof DataExportResultSchema>;

/** `POST /security/dpdp/erase` result — the deletion certificate. Mirrors
 *  ErasureResult, enriched with cert metadata for the once-shown certificate view. */
export const ErasureResultSchema = z.object({
  certificateId: z.string(),
  destroyedKeyIds: z.array(z.string()),
  preHash: z.string(),
  postHash: z.string(),
  signatureAlgorithm: z.string(),
  digitalSignature: z.string(),
  certifiedAt: z.string(),
  deletedRecordCounts: z.record(z.string(), z.number()),
});
export type ErasureResult = z.infer<typeof ErasureResultSchema>;

/** DPDP rights request row (export / erasure / correction). Mirrors
 *  platform.data_deletion_requests (+ request kind). NO backend GET yet (flagged).
 *  PHI: subject identity is a MASKED phone only. */
export const DpdpRequestSchema = z.object({
  requestId: z.string(),
  kind: z.enum(['export', 'erasure', 'correction']),
  subjectMaskedPhone: z.string(),
  status: z.enum(['pending', 'processing', 'completed', 'rejected']),
  scope: z.string(),
  reason: z.string().nullable(),
  gracePeriodEndsAt: z.string().nullable(),
  createdAt: z.string(),
});
export type DpdpRequest = z.infer<typeof DpdpRequestSchema>;

/** A breach register row. Mirrors platform.breach_log. NO backend GET yet (flagged).
 *  72h DPB clock = detectedAt + 72h vs reportedToDpbAt. */
export const BreachSchema = z.object({
  breachId: z.string(),
  breachType: z.string(),
  severity: z.enum(['low', 'medium', 'high', 'critical']),
  description: z.string(),
  affectedRecordCount: z.number().nullable(),
  detectedAt: z.string(),
  reportedToDpbAt: z.string().nullable(),
  resolvedAt: z.string().nullable(),
});
export type Breach = z.infer<typeof BreachSchema>;

/** A security review-queue item. Mirrors platform.v_security_review_queue.
 *  NO backend GET yet (flagged). PHI: only a masked subject ref, never a name. */
export const ReviewQueueItemSchema = z.object({
  source: z.enum(['anomaly', 'break_glass', 'consent_revocation']),
  itemId: z.string(),
  severity: z.enum(['low', 'medium', 'high', 'critical']),
  occurredAt: z.string(),
  description: z.string(),
  /** Display label for the acting user (no email/PHI — e.g. "Dr. A.S."). */
  actorLabel: z.string().nullable(),
  subjectMaskedPhone: z.string().nullable(),
});
export type ReviewQueueItem = z.infer<typeof ReviewQueueItemSchema>;

/** An encryption-key health row. Mirrors platform.v_key_rotation_status.
 *  NO backend GET yet (flagged). NO key material — only metadata + rotation status. */
export const KeyStatusSchema = z.object({
  keyId: z.string(),
  tenantName: z.string().nullable(),
  dataClass: z.string(),
  activatedAt: z.string(),
  nextRotationDueAt: z.string().nullable(),
  rotationStatus: z.enum(['ok', 'due_soon', 'overdue']),
  daysUntilRotation: z.number().nullable(),
  usageCount: z.number(),
});
export type KeyStatus = z.infer<typeof KeyStatusSchema>;

/** Generic created-id result for breach/break-glass POSTs. */
export const SecurityCreatedSchema = z.object({ id: z.string() });
export type SecurityCreated = z.infer<typeof SecurityCreatedSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// CLINICAL PHI (Slice 03b) — mirrors mediq.SharedDataModel/Docslot/Clinical
// (camelCase wire). THE MOST PHI-SENSITIVE SURFACE.
//  - Decrypted detail shapes are returned ONLY to authorized callers; the UI
//    must have a declared purpose-of-use + active consent before fetching them.
//  - List shapes carry NO clinical content (numbers/status/date only).
//  - Patient identity in clinical context is a MASKED phone.
// ─────────────────────────────────────────────────────────────────────────────

/** Declared purpose-of-use (DPDP) — matches the SQL CHECK on declared_purpose. */
export const PurposeOfUseSchema = z.enum([
  'treatment',
  'follow_up',
  'emergency',
  'consultation',
  'research',
  'audit',
  'patient_request',
]);
export type PurposeOfUse = z.infer<typeof PurposeOfUseSchema>;

/** Consent status — matches docslot consent.status CHECK. NO backend GET yet (flagged). */
export const ConsentStatusSchema = z.enum(['requested', 'granted', 'denied', 'revoked', 'expired']);
export type ConsentStatus = z.infer<typeof ConsentStatusSchema>;

/** A patient's clinical-access context (consent state for general PHI + ABDM).
 *  NO backend GET yet — built to spec (flagged). PHI: masked phone only. */
export const PatientConsentSchema = z.object({
  patientId: z.string(),
  maskedPhone: z.string(),
  /** Consent covering clinical PHI reads. */
  clinicalConsent: ConsentStatusSchema,
  /** ABDM-specific consent (records require it active). */
  abdmConsent: ConsentStatusSchema,
  consentExpiresAt: z.string().nullable(),
});
export type PatientConsent = z.infer<typeof PatientConsentSchema>;

// ---- Prescriptions ----------------------------------------------------------
/** List row — NO clinical content. Mirrors PrescriptionListItemDto. */
export const PrescriptionListItemSchema = z.object({
  prescriptionId: z.string(),
  prescriptionNumber: z.string().nullable(),
  doctorId: z.string(),
  doctorName: z.string(),
  status: z.string(),
  createdAt: z.string(),
});
export type PrescriptionListItem = z.infer<typeof PrescriptionListItemSchema>;

/** One medication line, parsed from PrescriptionDto.medicationsJson. */
export const MedicationSchema = z.object({
  name: z.string(),
  dose: z.string(),
  frequency: z.string(),
  duration: z.string(),
});
export type Medication = z.infer<typeof MedicationSchema>;

/** Decrypted detail. Mirrors PrescriptionDto (medicationsJson parsed to array). */
export const PrescriptionDetailSchema = z.object({
  prescriptionId: z.string(),
  prescriptionNumber: z.string().nullable(),
  patientId: z.string(),
  doctorId: z.string(),
  doctorName: z.string(),
  chiefComplaints: z.string().nullable(),
  examination: z.string().nullable(),
  diagnosis: z.string().nullable(),
  medications: z.array(MedicationSchema),
  advice: z.string().nullable(),
  followUpInDays: z.number().nullable(),
  status: z.string(),
  createdAt: z.string(),
});
export type PrescriptionDetail = z.infer<typeof PrescriptionDetailSchema>;

/** Issue-prescription body. Mirrors IssuePrescriptionRequest (medications array
 *  serialised to medicationsJson at the seam). */
export const IssuePrescriptionRequestSchema = z.object({
  bookingId: z.string(),
  patientId: z.string(),
  doctorId: z.string(),
  chiefComplaints: z.string().nullable().optional(),
  examination: z.string().nullable().optional(),
  diagnosis: z.string().nullable().optional(),
  medications: z.array(MedicationSchema),
  advice: z.string().nullable().optional(),
  followUpInDays: z.number().nullable().optional(),
});
export type IssuePrescriptionRequest = z.infer<typeof IssuePrescriptionRequestSchema>;

export const IssuePrescriptionResultSchema = z.object({
  prescriptionId: z.string(),
  prescriptionNumber: z.string().nullable(),
});
export type IssuePrescriptionResult = z.infer<typeof IssuePrescriptionResultSchema>;

// ---- Lab reports ------------------------------------------------------------
/** List row — NO clinical content. (No backend list endpoint yet — flagged.) */
export const LabReportListItemSchema = z.object({
  reportId: z.string(),
  reportNumber: z.string().nullable(),
  testName: z.string(),
  status: z.enum(['pending', 'delivered']),
  hasCriticalFindings: z.boolean(),
  createdAt: z.string(),
});
export type LabReportListItem = z.infer<typeof LabReportListItemSchema>;

/** One structured result row, parsed from LabReportDto.structuredResultsJson. */
export const LabResultRowSchema = z.object({
  analyte: z.string(),
  value: z.string(),
  unit: z.string().nullable(),
  refRange: z.string().nullable(),
  flag: z.enum(['normal', 'high', 'low', 'critical']).nullable(),
});
export type LabResultRow = z.infer<typeof LabResultRowSchema>;

/** Decrypted detail. Mirrors LabReportDto (structuredResultsJson parsed to rows). */
export const LabReportDetailSchema = z.object({
  reportId: z.string(),
  reportNumber: z.string().nullable(),
  patientId: z.string(),
  testName: z.string(),
  fileName: z.string().nullable(),
  results: z.array(LabResultRowSchema),
  status: z.enum(['pending', 'delivered']),
  hasCriticalFindings: z.boolean(),
  createdAt: z.string(),
});
export type LabReportDetail = z.infer<typeof LabReportDetailSchema>;

export const UploadLabReportRequestSchema = z.object({
  bookingId: z.string(),
  patientId: z.string(),
  testName: z.string(),
  fileName: z.string().nullable().optional(),
  results: z.array(LabResultRowSchema),
  hasCriticalFindings: z.boolean(),
});
export type UploadLabReportRequest = z.infer<typeof UploadLabReportRequestSchema>;

export const UploadLabReportResultSchema = z.object({
  reportId: z.string(),
  reportNumber: z.string().nullable(),
});
export type UploadLabReportResult = z.infer<typeof UploadLabReportResultSchema>;

// ---- Medical history --------------------------------------------------------
/** Decrypted timeline entry. Mirrors MedicalHistoryDto. */
export const MedicalHistorySchema = z.object({
  historyId: z.string(),
  recordType: z.string(),
  title: z.string(),
  description: z.string().nullable(),
  isActive: z.boolean(),
  isCritical: z.boolean(),
  addedAt: z.string(),
});
export type MedicalHistory = z.infer<typeof MedicalHistorySchema>;

// ---- ABDM (consent-gated) ---------------------------------------------------
/** List row — NO clinical content. (No backend list endpoint yet — flagged.) */
export const AbdmRecordListItemSchema = z.object({
  recordId: z.string(),
  recordType: z.string(),
  abhaNumber: z.string(),
  isLinkedToPhr: z.boolean(),
  createdAt: z.string(),
});
export type AbdmRecordListItem = z.infer<typeof AbdmRecordListItemSchema>;

/** Decrypted detail. Mirrors AbdmRecordDto (FHIR bundle exposed as metadata only). */
export const AbdmRecordDetailSchema = z.object({
  recordId: z.string(),
  patientId: z.string(),
  abhaNumber: z.string(),
  recordType: z.string(),
  /** Size only — the FHIR bundle contents are not rendered inline. */
  fhirResourceCount: z.number(),
  isLinkedToPhr: z.boolean(),
  createdAt: z.string(),
});
export type AbdmRecordDetail = z.infer<typeof AbdmRecordDetailSchema>;

export const PushAbdmRecordResultSchema = z.object({ recordId: z.string() });
export type PushAbdmRecordResult = z.infer<typeof PushAbdmRecordResultSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// COMMISSION / CARE PARTNERS (Slice 07) — mirrors
// mediq.SharedDataModel/Docslot/Commission/CommissionDtos.cs (camelCase).
// LEGAL/SAFETY:
//  - Customer-facing term is "Care Partner" (MCI 6.4). UI strings never say
//    "broker"/"referral partner". Code/types may say broker (matching the DTO).
//  - DPDP: NO full PAN (masked input only; never serialised in any DTO). Patient
//    identity in attribution context = first name + MASKED phone only.
//  - PCPNDT: excludesPndt is ALWAYS true (CHECK-enforced) — shown as an enforced,
//    non-toggleable guarantee.
//  - Money: payout approve and execute are DISTINCT (different permissions).
// ─────────────────────────────────────────────────────────────────────────────

export const BrokerTypeSchema = z.enum([
  'medical_rep',
  'corporate_hr',
  'insurance_panel',
  'aggregator_agent',
  'community_worker',
  'hotel_concierge',
  'individual',
  'platform_partner',
]);
export type BrokerType = z.infer<typeof BrokerTypeSchema>;

export const TierLevelSchema = z.enum(['basic', 'silver', 'gold', 'platinum']);
export type TierLevel = z.infer<typeof TierLevelSchema>;

/** Care Partner profile. Mirrors BrokerDto — NO PAN, NO full bank account.
 *  `maskedPhone` is the display-safe phone (the raw BrokerDto.Phone is masked at
 *  the adapter for the UI; full phone is never rendered in lists). */
export const BrokerSchema = z.object({
  brokerId: z.string(),
  maskedPhone: z.string(),
  fullName: z.string(),
  email: z.string().nullable(),
  brokerType: BrokerTypeSchema,
  tierLevel: TierLevelSchema,
  panVerified: z.boolean(),
  gstVerified: z.boolean(),
  isActive: z.boolean(),
  isBlacklisted: z.boolean(),
  /** "Care Partner" — the customer-facing term (BrokerDto.CarePartnerLabel). */
  carePartnerLabel: z.string(),
});
export type Broker = z.infer<typeof BrokerSchema>;

/** Register a Care Partner. Mirrors RegisterBrokerRequest. PAN is a masked input
 *  here and is NOT echoed back in the result (RegisterBrokerResult). */
export const RegisterBrokerRequestSchema = z.object({
  phone: z.string(),
  fullName: z.string(),
  email: z.string().nullable().optional(),
  brokerType: BrokerTypeSchema,
  pan: z.string().nullable().optional(),
  gstNumber: z.string().nullable().optional(),
  tierLevel: TierLevelSchema.optional(),
});
export type RegisterBrokerRequest = z.infer<typeof RegisterBrokerRequestSchema>;

export const RegisterBrokerResultSchema = z.object({
  brokerId: z.string(),
  alreadyExisted: z.boolean(),
});
export type RegisterBrokerResult = z.infer<typeof RegisterBrokerResultSchema>;

// ---- Commission rules -------------------------------------------------------
export const CalcTypeSchema = z.enum(['flat', 'percentage', 'tiered_table']);
export type CalcType = z.infer<typeof CalcTypeSchema>;

/** A rate card. Mirrors CommissionRuleDto. `excludesPndt` is always true. */
export const CommissionRuleSchema = z.object({
  ruleId: z.string(),
  ruleName: z.string(),
  ruleKey: z.string(),
  calcType: CalcTypeSchema,
  flatAmountInr: z.number().nullable(),
  percentage: z.number().nullable(),
  minCommissionInr: z.number().nullable(),
  maxCommissionInr: z.number().nullable(),
  maxMonthlyPerBrokerInr: z.number().nullable(),
  priority: z.number(),
  firstBookingOnly: z.boolean(),
  isActive: z.boolean(),
  /** PCPNDT — CHECK-forced true. Never editable. */
  excludesPndt: z.literal(true),
});
export type CommissionRule = z.infer<typeof CommissionRuleSchema>;

export const CreateCommissionRuleRequestSchema = z.object({
  ruleName: z.string(),
  ruleKey: z.string(),
  calcType: CalcTypeSchema,
  flatAmountInr: z.number().nullable().optional(),
  percentage: z.number().nullable().optional(),
  minCommissionInr: z.number().nullable().optional(),
  maxCommissionInr: z.number().nullable().optional(),
  maxMonthlyPerBrokerInr: z.number().nullable().optional(),
  priority: z.number(),
  firstBookingOnly: z.boolean(),
});
export type CreateCommissionRuleRequest = z.infer<typeof CreateCommissionRuleRequestSchema>;

// ---- Attribution ------------------------------------------------------------
export const AttributionSourceSchema = z.enum([
  'referral_link',
  'broker_portal_booking',
  'whatsapp_template',
  'post_hoc_claim',
  'qr_scan',
]);
export type AttributionSource = z.infer<typeof AttributionSourceSchema>;

export const VerificationStatusSchema = z.enum([
  'pending',
  'auto_verified',
  'patient_confirmed',
  'patient_denied',
  'no_response',
  'admin_override',
]);
export type VerificationStatus = z.infer<typeof VerificationStatusSchema>;

/** One attribution ledger row. Mirrors AttributionResultDto, enriched with
 *  display-safe context. PHI: patient = first name + MASKED phone only. */
export const AttributionSchema = z.object({
  attributionId: z.string(),
  bookingRef: z.string(),
  brokerId: z.string(),
  brokerName: z.string(),
  patientFirstName: z.string(),
  patientMaskedPhone: z.string(),
  attributionSource: AttributionSourceSchema,
  verificationStatus: VerificationStatusSchema,
  commissionStatus: z.string(),
  commissionAmountInr: z.number().nullable(),
  fraudScore: z.number(),
  fraudFlags: z.array(z.string()),
  createdAt: z.string(),
});
export type Attribution = z.infer<typeof AttributionSchema>;

// ---- Payouts ----------------------------------------------------------------
export const PayoutStatusSchema = z.enum([
  'pending',
  'approved',
  'processing',
  'paid',
  'failed',
  'on_hold',
  'reversed',
]);
export type PayoutStatus = z.infer<typeof PayoutStatusSchema>;

/** Payout batch with full tax breakdown. Mirrors PayoutDto — no PAN, only math.
 *  Status drives the approve→execute UI: 'pending'→approve→'approved'(awaiting
 *  execution)→execute→'processing'/'paid'. */
export const PayoutSchema = z.object({
  payoutId: z.string(),
  brokerId: z.string(),
  brokerName: z.string(),
  periodStart: z.string(),
  periodEnd: z.string(),
  attributionCount: z.number(),
  grossAmountInr: z.number(),
  tdsRate: z.number(),
  tdsAmountInr: z.number(),
  gstRate: z.number().nullable(),
  gstAmountInr: z.number(),
  netAmountInr: z.number(),
  status: PayoutStatusSchema,
  paymentReference: z.string().nullable(),
});
export type Payout = z.infer<typeof PayoutSchema>;

/** Result of approve/execute. Mirrors PayoutActionResult. */
export const PayoutActionResultSchema = z.object({
  payoutId: z.string(),
  status: PayoutStatusSchema,
  paymentReference: z.string().nullable(),
});
export type PayoutActionResult = z.infer<typeof PayoutActionResultSchema>;

// ---- Disputes ---------------------------------------------------------------
export const DisputeStatusSchema = z.enum([
  'open',
  'investigating',
  'resolved_broker_wins',
  'resolved_tenant_wins',
  'resolved_compromise',
  'closed_no_action',
]);
export type DisputeStatus = z.infer<typeof DisputeStatusSchema>;

/** A dispute row. Mirrors DisputeDto, enriched for the list. */
export const DisputeSchema = z.object({
  disputeId: z.string(),
  attributionId: z.string(),
  bookingRef: z.string(),
  brokerName: z.string(),
  raisedBy: z.string(),
  disputeReason: z.string(),
  status: DisputeStatusSchema,
  raisedAt: z.string(),
});
export type Dispute = z.infer<typeof DisputeSchema>;

export const RaiseDisputeRequestSchema = z.object({
  attributionId: z.string(),
  disputeReason: z.string(),
  description: z.string(),
});
export type RaiseDisputeRequest = z.infer<typeof RaiseDisputeRequestSchema>;

export const ResolveDisputeRequestSchema = z.object({
  disputeId: z.string(),
  status: DisputeStatusSchema,
  resolutionNotes: z.string().nullable().optional(),
  amountAdjustmentInr: z.number().nullable().optional(),
});
export type ResolveDisputeRequest = z.infer<typeof ResolveDisputeRequestSchema>;

/** Generic created-id result for commission POSTs. */
export const CommissionCreatedSchema = z.object({ id: z.string() });
export type CommissionCreated = z.infer<typeof CommissionCreatedSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// DOCTORS DIRECTORY (/doctors) — practitioner cards with OPD load. Mirrors the
// docslot.doctors + today's booking aggregate. NO PHI. `colorKey` is a token key
// (never a hex). Built to spec — no dedicated backend list DTO yet (flagged).
// ─────────────────────────────────────────────────────────────────────────────
export const DoctorCardSchema = z.object({
  id: z.string(),
  name: z.string(),
  spec: z.string(),
  deptId: z.string(),
  deptName: z.string(),
  /** token color key for the specialty tag / load bar accent — NOT a hex. */
  colorKey: z.string(),
  qualification: z.string(),
  feeInr: z.number(),
  room: z.string(),
  rating: z.number(),
  initials: z.string(),
  /** Appointments seen/booked today. */
  todayCount: z.number(),
  /** Today's OPD capacity (denominator for the load bar). */
  todayCapacity: z.number(),
  /** Today's OPD window, explicit Asia/Kolkata (e.g. "09:00–13:00"). */
  hours: z.string(),
  /** Next free slot, 24h Asia/Kolkata. */
  nextSlot: z.string(),
});
export type DoctorCard = z.infer<typeof DoctorCardSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// CALENDAR (/calendar) — week-view capacity heatmap. One column per weekday, one
// cell per time row. Mirrors a slot-availability aggregate. NO PHI.
// ─────────────────────────────────────────────────────────────────────────────
export const CalendarCellSchema = z.object({
  /** open | tight (almost full) | full | blocked | off (off-hours). */
  state: z.enum(['open', 'tight', 'full', 'blocked', 'off']),
  booked: z.number(),
  capacity: z.number(),
});
export type CalendarCell = z.infer<typeof CalendarCellSchema>;

export const CalendarColumnSchema = z.object({
  /** ISO-ish day key. */
  key: z.string(),
  /** Display label, e.g. "Mon 21". */
  label: z.string(),
  /** Weekday short label, e.g. "Mon". */
  weekday: z.string(),
  /** Day-of-month, e.g. "21". */
  dayOfMonth: z.string(),
  isToday: z.boolean(),
  cells: z.array(CalendarCellSchema),
});
export type CalendarColumn = z.infer<typeof CalendarColumnSchema>;

export const CalendarGridSchema = z.object({
  /** Time-row labels, 24h Asia/Kolkata. */
  times: z.array(z.string()),
  /** Human range label for the header, e.g. "21–27 Jun 2026". */
  rangeLabel: z.string(),
  columns: z.array(CalendarColumnSchema),
});
export type CalendarGrid = z.infer<typeof CalendarGridSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// ANALYTICS (/analytics) — KPI cards, weekly stacked volume, top departments,
// WhatsApp conversation funnel. Aggregate only — NO PHI, NO per-patient rows.
// Built to spec — no dedicated backend analytics DTO yet (flagged).
// ─────────────────────────────────────────────────────────────────────────────
export const AnalyticsKpiSchema = z.object({
  /** stable key → i18n label lookup. */
  key: z.enum(['totalBookings', 'whatsappShare', 'noShowRate', 'revenue']),
  /** Pre-formatted display value (e.g. "1,284", "68%", "₹4.2L"). */
  value: z.string(),
  /** Signed percentage delta vs the previous period (e.g. +12.4). */
  deltaPct: z.number(),
  /** True when a positive delta is GOOD (revenue↑ good; no-show↑ bad). */
  higherIsBetter: z.boolean(),
  /** Optional comparator caption, e.g. "vs 8% industry". */
  caption: z.string().nullable(),
});
export type AnalyticsKpi = z.infer<typeof AnalyticsKpiSchema>;

/** One weekday's booking volume split by channel (stacked bar). */
export const VolumeBarSchema = z.object({
  weekday: z.string(),
  whatsapp: z.number(),
  /** walk-in + phone, grouped as "direct". */
  direct: z.number(),
});
export type VolumeBar = z.infer<typeof VolumeBarSchema>;

export const TopDepartmentSchema = z.object({
  id: z.string(),
  name: z.string(),
  colorKey: z.string(),
  bookings: z.number(),
});
export type TopDepartment = z.infer<typeof TopDepartmentSchema>;

export const FunnelStepSchema = z.object({
  key: z.enum(['startedChat', 'pickedDept', 'pickedDoctor', 'pickedSlot', 'confirmed']),
  count: z.number(),
  pct: z.number(),
});
export type FunnelStep = z.infer<typeof FunnelStepSchema>;

export const AnalyticsSchema = z.object({
  kpis: z.array(AnalyticsKpiSchema),
  volume: z.array(VolumeBarSchema),
  topDepartments: z.array(TopDepartmentSchema),
  funnel: z.array(FunnelStepSchema),
});
export type Analytics = z.infer<typeof AnalyticsSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// LIVE API — RAW LIST DTOs (consumed only by lib/backend/real.ts behind the
// VITE_USE_REAL_API flag). These mirror the .NET success DTOs 1:1 (RAW, no
// wrapper). lib/backend ADAPTS each of these into the existing app-facing shapes
// above (BookingRow, DoctorCard, DashboardSummary, …) so the screens never change.
// In mock mode none of these are parsed — the mock seam serves the app-facing
// shapes directly.
// ─────────────────────────────────────────────────────────────────────────────

/** `GET /api/v1/bookings` row. Mirrors BookingListItemDto. PHI: maskedPhone only.
 *  WIRE NOTE: the API now serializes `status`/`source`/`gender`/`language` as the
 *  canonical snake_case STRING tokens (matching the SQL CHECK + the frontend zod
 *  enums). lib/backend passes the strings straight through to BookingRowSchema.
 *  The schema stays string|number-tolerant so a regression to int indices would
 *  surface at the adapter rather than mis-render. */
export const BookingListItemDtoSchema = z.object({
  bookingId: z.string(),
  bookingNumber: z.string().nullable(),
  tokenNumber: z.number().nullable(),
  patientDisplayName: z.string(),
  maskedPhone: z.string().nullable(),
  age: z.number().nullable(),
  gender: z.union([z.number(), z.string()]).nullable(),
  doctorName: z.string().nullable(),
  departmentName: z.string().nullable(),
  slotStart: z.string(),
  slotEnd: z.string().nullable(),
  status: z.union([z.number(), z.string()]),
  source: z.union([z.number(), z.string()]),
  note: z.string().nullable(),
  createdAt: z.string(),
  language: z.union([z.number(), z.string()]).nullable(),
});
export type BookingListItemDto = z.infer<typeof BookingListItemDtoSchema>;

/** `GET /api/v1/patients` row. Mirrors PatientListItemDto. PHI: maskedPhone only. */
export const PatientListItemDtoSchema = z.object({
  patientId: z.string(),
  fullName: z.string(),
  maskedPhone: z.string().nullable(),
  age: z.number().nullable(),
  gender: z.string().nullable(),
  preferredLanguage: z.string().nullable(),
});
export type PatientListItemDto = z.infer<typeof PatientListItemDtoSchema>;

/** App-facing patient list row (mock + real both produce this; PatientsScreen
 *  consumes it). PHI: maskedPhone only — clinical content never here. */
export const PatientRowSchema = z.object({
  id: z.string(),
  name: z.string(),
  maskedPhone: z.string(),
  age: z.number().nullable(),
  gender: z.string().nullable(),
  preferredLanguage: z.string().nullable(),
});
export type PatientRow = z.infer<typeof PatientRowSchema>;

/** `GET /api/v1/doctors` row. Mirrors DoctorDto. NO PHI. The trailing fields are
 *  ADDITIVE + NULLABLE (directory enrichment): real department name, today's
 *  booked/capacity OPD load, average rating, today's OPD window (HH:mm:ss), and
 *  the next free slot (nullable ISO). lib/backend adapts these into DoctorCard. */
export const DoctorDtoSchema = z.object({
  doctorId: z.string(),
  fullName: z.string(),
  displayName: z.string().nullable(),
  specialization: z.string().nullable(),
  departmentId: z.string().nullable(),
  consultationFee: z.number().nullable(),
  isAcceptingNewPatients: z.boolean(),
  departmentName: z.string().nullable().optional(),
  todayBooked: z.number().nullable().optional(),
  todayCapacity: z.number().nullable().optional(),
  rating: z.number().nullable().optional(),
  /** "HH:mm:ss" Asia/Kolkata. */
  todayHoursStart: z.string().nullable().optional(),
  todayHoursEnd: z.string().nullable().optional(),
  /** Next free slot, ISO datetime (+05:30), or null. */
  nextAvailableSlot: z.string().nullable().optional(),
});
export type DoctorDto = z.infer<typeof DoctorDtoSchema>;

/** `GET /api/v1/doctors/{doctorId}/slots?date=YYYY-MM-DD` row. Mirrors SlotDto
 *  (RAW). `startTime`/`endTime` serialize as "HH:mm:ss"; `status` is the canonical
 *  slot-state token ("available" | "held" | "booked" | "blocked" | …). lib/backend
 *  keeps only status==="available" and adapts each into the app-facing Slot
 *  (carrying the slotId required to create a booking). NO PHI. */
export const SlotDtoSchema = z.object({
  slotId: z.string(),
  doctorId: z.string(),
  slotDate: z.string(),
  startTime: z.string(),
  endTime: z.string(),
  status: z.string(),
  currentCount: z.number(),
  maxCount: z.number(),
});
export type SlotDto = z.infer<typeof SlotDtoSchema>;

/** `GET /api/v1/analytics?period=`. Mirrors AnalyticsDto (RAW). Percentages are
 *  0..100; revenue is a number, currency "INR". lib/backend adapts this into the
 *  app-facing Analytics shape (pre-formatted KPI strings, channel-split volume,
 *  colour-keyed top departments, enum-keyed funnel). NO PHI — aggregates only. */
export const AnalyticsDtoSchema = z.object({
  kpis: z.object({
    totalBookings: z.number(),
    whatsappSharePct: z.number(),
    noShowRatePct: z.number(),
    revenue: z.number(),
    revenueCurrency: z.string(),
  }),
  weeklyVolume: z.array(
    z.object({ weekday: z.string(), whatsapp: z.number(), other: z.number() }),
  ),
  topDepartments: z.array(
    z.object({ departmentName: z.string(), bookings: z.number() }),
  ),
  funnel: z.array(
    z.object({ stage: z.string(), count: z.number(), pct: z.number() }),
  ),
});
export type AnalyticsDto = z.infer<typeof AnalyticsDtoSchema>;

// ── LIVE — mutation request/result DTOs (consumed by lib/backend/real.ts) ─────

/** `POST /api/v1/bookings` body. Mirrors CreateBookingRequest. Idempotency-Key is
 *  sent as a header (apiFetch idempotency:true), NOT in the body. */
export const CreateBookingRequestDtoSchema = z.object({
  slotId: z.string(),
  doctorId: z.string(),
  departmentId: z.string().nullable().optional(),
  patientPhone: z.string(),
  patientName: z.string().nullable().optional(),
  patientAge: z.number().nullable().optional(),
  patientGender: z.string().nullable().optional(),
  bookingType: z.string(),
  bookedVia: z.string(),
  chiefComplaint: z.string().nullable().optional(),
  issueOpdToken: z.boolean(),
});
export type CreateBookingRequestDto = z.infer<typeof CreateBookingRequestDtoSchema>;

/** `POST /api/v1/bookings` result. Mirrors CreateBookingResult. */
export const CreateBookingResultDtoSchema = z.object({
  bookingId: z.string(),
  bookingNumber: z.string().nullable(),
  tokenNumber: z.number().nullable(),
});
export type CreateBookingResultDto = z.infer<typeof CreateBookingResultDtoSchema>;

/** Booking action result. Mirrors BookingActionResultDto (tolerant/passthrough so
 *  additive fields like WasReplayed don't break parsing). */
export const BookingActionResultDtoSchema = z
  .object({
    bookingId: z.string().optional(),
    status: z.union([z.number(), z.string()]).optional(),
  })
  .passthrough();
export type BookingActionResultDto = z.infer<typeof BookingActionResultDtoSchema>;

/** `POST /api/v1/doctors` result. Mirrors CreateDoctorResult (201). */
export const CreateDoctorResultDtoSchema = z.object({
  doctorId: z.string(),
  fullName: z.string(),
  departmentId: z.string().nullable(),
});
export type CreateDoctorResultDto = z.infer<typeof CreateDoctorResultDtoSchema>;

/** `POST /api/v1/patients` body. Mirrors RegisterPatientRequest. */
export const RegisterPatientRequestDtoSchema = z.object({
  phoneNumber: z.string(),
  fullName: z.string().nullable().optional(),
  age: z.number().nullable().optional(),
  gender: z.string().nullable().optional(),
  preferredLanguage: z.string().default('en'),
});
export type RegisterPatientRequestDto = z.infer<typeof RegisterPatientRequestDtoSchema>;

/** `POST /api/v1/patients` result. Mirrors RegisterPatientResult. */
export const RegisterPatientResultDtoSchema = z.object({
  patientId: z.string(),
  alreadyExisted: z.boolean(),
});
export type RegisterPatientResultDto = z.infer<typeof RegisterPatientResultDtoSchema>;

/** `GET /api/v1/dashboard/summary`. Mirrors DashboardSummaryDto (verified against
 *  the running API): { liveQueueCount, confirmedTodayCount, todayRevenue,
 *  revenueCurrency, noShowRate (FRACTION 0..1), asOf }. lib/backend adapts these
 *  into the app-facing DashboardSummary, filling 0 for fields the API doesn't
 *  emit yet (the WhatsApp/walk-in split, activeConversations) so the strip never
 *  crashes. Tolerant/passthrough so additive backend fields don't break parsing. */
export const DashboardSummaryDtoSchema = z
  .object({
    liveQueueCount: z.number().nullable().optional(),
    confirmedTodayCount: z.number().nullable().optional(),
    todayRevenue: z.number().nullable().optional(),
    revenueCurrency: z.string().nullable().optional(),
    /** Fraction in [0,1] — multiplied by 100 at the adapter for the % display. */
    noShowRate: z.number().nullable().optional(),
    asOf: z.string().nullable().optional(),
  })
  .passthrough();
export type DashboardSummaryDto = z.infer<typeof DashboardSummaryDtoSchema>;
