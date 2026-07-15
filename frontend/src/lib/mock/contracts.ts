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
  // checked_in (Phase 1): a confirmed patient who has arrived at the desk.
  'checked_in',
  'cancelled',
  'completed',
  'no_show',
  'rescheduled',
]);
export const BookingSourceSchema = z.enum(['whatsapp', 'dashboard', 'api', 'walk_in', 'phone_call']);

// Behalf / consent wire enums (Phase 1) — mirror the SQL CHECK constraints. Human
// display labels live in i18n (behalf.* / consent.*), never here.
export const BookedByTypeSchema = z.enum(['self', 'behalf']);
export const BehalfRelationSchema = z.enum(['family', 'friend', 'neighbour', 'care_partner', 'other']);
export const PatientConsentStatusSchema = z.enum([
  'not_required',
  'pending',
  'confirmed',
  'denied',
  'expired',
]);

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
  // Patient demographics for the reception-queue row subline ("31F · +91 ····· ·····").
  // PHI: age/gender are low-sensitivity demographics — the raw phone is NEVER carried
  // (maskedPhone only, above). Nullable/defaulted so a pre-existing row (mock or a
  // pre-Phase live payload) parses unchanged and the subline degrades gracefully.
  age: z.number().int().nonnegative().nullable().default(null),
  gender: z.enum(['F', 'M', 'O']).nullable().default(null),
  // Behalf / consent (Phase 1, read-only). Optional with safe defaults so a
  // pre-Phase-1 row (or the mock, which omits them) still parses — a `self`
  // booking with no consent requirement.
  bookedByType: BookedByTypeSchema.default('self'),
  behalfRelation: BehalfRelationSchema.nullable().default(null),
  patientConsentStatus: PatientConsentStatusSchema.default('not_required'),
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

/** `GET /api/v1/bookings/{bookingId}/conversation` row. Mirrors
 *  ConversationMessageDto (from wa_message_log). `direction` is inbound (from the
 *  patient) / outbound (from the bot/tenant); `content` is the message text (may
 *  be null for non-text message types). lib/backend adapts each into the
 *  app-facing ChatMessage the WhatsApp-mirrored thread consumes. NO PHI beyond the
 *  message body the patient themselves sent over WhatsApp. */
export const ConversationMessageDtoSchema = z.object({
  logId: z.string(),
  direction: z.enum(['inbound', 'outbound']),
  messageType: z.string(),
  content: z.string().nullable(),
  status: z.string().nullable(),
  sentAt: z.string(),
});
export type ConversationMessageDto = z.infer<typeof ConversationMessageDtoSchema>;

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
  // A newly-created booking is 'pending' server-side (it still needs approval),
  // so this is the full status enum — NOT a 'confirmed' literal. The earlier
  // literal mis-reported every fresh booking as confirmed; consumers only read
  // the token, so widening to the enum keeps them working while telling the truth.
  status: BookingStatusSchema,
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
// AI ASSIST — no-show risk + triage. Two ALREADY-SHIPPED backend capabilities
// surfaced read-only in the reception desk. Both DTOs carry an `available` flag:
// false means the AI sibling service is unreachable, so the UI renders an
// "unavailable" state and NEVER a fabricated score/assessment. Mirrors
// NoShowRiskDto / TriageResultDto (camelCase wire). NO PHI in the no-show DTO; the
// triage `complaint` IS PHI and is the REQUEST only — it is never echoed in these
// result shapes and is never persisted/keyed client-side.
// ─────────────────────────────────────────────────────────────────────────────

/** Risk band — shared by no-show (low/medium/high) and triage urgency (+emergency). */
export const RiskBandSchema = z.enum(['low', 'medium', 'high']);
export type RiskBand = z.infer<typeof RiskBandSchema>;

/** `GET /api/v1/bookings/{bookingId}/no-show-risk` → NoShowRiskDto. NO PHI. When
 *  available=false the model is unreachable (probability/band null) → "unavailable"
 *  chip. probability is a 0..1 fraction (rendered as a %). */
export const NoShowRiskSchema = z.object({
  bookingId: z.string(),
  available: z.boolean(),
  probability: z.number().nullable(),
  band: RiskBandSchema.nullable(),
  modelName: z.string().nullable(),
  source: z.string().nullable(),
});
export type NoShowRisk = z.infer<typeof NoShowRiskSchema>;

/** Triage urgency band — adds 'emergency' above the shared low/medium/high. */
export const UrgencyBandSchema = z.enum(['low', 'medium', 'high', 'emergency']);
export type UrgencyBand = z.infer<typeof UrgencyBandSchema>;

/** A doctor the triage suggested. consultationFee/nextAvailableSlot may be null.
 *  Mirrors SuggestedDoctorDto. */
export const SuggestedDoctorSchema = z.object({
  doctorId: z.string(),
  fullName: z.string(),
  specialization: z.string().nullable(),
  consultationFee: z.number().nullable(),
  nextAvailableSlot: z.string().nullable(),
});
export type SuggestedDoctor = z.infer<typeof SuggestedDoctorSchema>;

/** `POST /api/v1/triage` → TriageResultDto. available=false → "triage unavailable"
 *  (never a fabricated assessment). redFlags/symptoms default to [] so an absent
 *  array still parses. */
export const TriageResultSchema = z.object({
  available: z.boolean(),
  urgencyBand: UrgencyBandSchema.nullable(),
  department: z.string().nullable(),
  redFlags: z.array(z.string()).default([]),
  symptoms: z.array(z.string()).default([]),
  suggestedDoctors: z.array(SuggestedDoctorSchema).default([]),
  runId: z.string().nullable(),
  source: z.string().nullable(),
});
export type TriageResult = z.infer<typeof TriageResultSchema>;

/** `POST /api/v1/triage` body. The `complaint` is PHI. patientId/bookingId are
 *  optional; when EITHER is present the server REQUIRES X-Purpose-Of-Use (422
 *  without it). The reception intake path passes neither (pure free-text). */
export interface TriageRequestInput {
  complaint: string;
  patientId?: string;
  bookingId?: string;
  patientAge?: number;
  /** Forwarded to X-Purpose-Of-Use ONLY for the patient/booking-bound call. */
  purposeOfUse?: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// AI DOCUMENT SURFACES (Slice 11 + 14) — OCR lab-report extraction + RAG ask over
// a patient's indexed medical history, plus the non-PHI operational reads.
// Mirrors mediq.SharedDataModel/Docslot/Ai/AiDocumentDtos.cs (camelCase wire).
//
// PHI POSTURE:
//  - The OCR analyte VALUES and the RAG ANSWER are PHI → returned by a MUTATION,
//    never cached in a query key (the feature hooks use useMutation).
//  - The RAG QUESTION is PHI → a mutation VARIABLE only (request body); never a
//    query key, never logged, never echoed (the result never carries it back).
//  - Both PHI POSTs are patient-bound → the caller forwards X-Purpose-Of-Use.
//  - The two OPERATIONAL reads (extractions list + RAG status) are non-PHI
//    summaries → cacheable as ordinary queries.
//  - `available:false` is a valid success (AI sibling unreachable) → the UI renders
//    a fail-safe "unavailable" state and NEVER fabricates a result.
// Optional fields are `.nullish()` (the .NET serializer omits null on the wire) and
// arrays `.default([])` so an absent collection still parses.
// ─────────────────────────────────────────────────────────────────────────────

/** A single extracted lab analyte (OCR). The `value` IS PHI. Mirrors AnalyteDto. */
export const OcrAnalyteSchema = z.object({
  test: z.string(),
  value: z.number(),
  unit: z.string().nullish(),
  refLow: z.number(),
  refHigh: z.number(),
  flag: z.string(),
});
export type OcrAnalyte = z.infer<typeof OcrAnalyteSchema>;

/** `POST /api/v1/lab-reports/extract` → OcrExtractionDto. analytes (PHI) default to
 *  [] so an absent array still parses; available=false → "extraction unavailable"
 *  (never a fabricated result). Mirrors OcrExtractionDto (rawTextPreview omitted). */
export const OcrExtractionSchema = z.object({
  available: z.boolean(),
  extractionId: z.string().nullish(),
  ocrEngine: z.string().nullish(),
  overallConfidence: z.number().nullish(),
  requiresHumanReview: z.boolean().nullish(),
  abnormalCount: z.number().nullish(),
  analytes: z.array(OcrAnalyteSchema).default([]),
  source: z.string().nullish(),
});
export type OcrExtraction = z.infer<typeof OcrExtractionSchema>;

/** `POST /api/v1/lab-reports/extract` body + the patient-bound purpose-of-use. NO
 *  client source path is accepted — the server resolves the report blob. */
export interface ExtractLabReportInput {
  relatedPatientId: string;
  relatedBookingId?: string;
  /** Forwarded to X-Purpose-Of-Use (REQUIRED — the extraction is patient-bound). */
  purposeOfUse: string;
}

/** A citation backing a RAG answer (points at a medical-history record). Mirrors
 *  RagCitationDto. */
export const RagCitationSchema = z.object({
  historyId: z.string(),
  recordType: z.string().nullish(),
  title: z.string().nullish(),
  severity: z.string().nullish(),
  score: z.number(),
});
export type RagCitation = z.infer<typeof RagCitationSchema>;

/** `POST /api/v1/patients/{patientId}/rag/ask` → RagAnswerDto. The `answer` is PHI;
 *  the question is NEVER echoed back. available=false → "answer unavailable".
 *  `mode` is 'extractive' | 'llm'. citations default to []. Mirrors RagAnswerDto. */
export const RagAnswerSchema = z.object({
  available: z.boolean(),
  patientId: z.string(),
  answer: z.string().nullish(),
  mode: z.string().nullish(),
  citations: z.array(RagCitationSchema).default([]),
  retrieved: z.number().nullish(),
  source: z.string().nullish(),
});
export type RagAnswer = z.infer<typeof RagAnswerSchema>;

/** `POST /api/v1/patients/{patientId}/rag/ask` body + purpose. The `question` is
 *  PHI — a mutation variable only (never a query key, never logged/echoed). */
export interface RagAskInput {
  patientId: string;
  question: string;
  /** Forwarded to X-Purpose-Of-Use (REQUIRED — the ask is patient-bound). */
  purposeOfUse: string;
}

// ── AI operational reads (non-PHI summaries) ──────────────────────────────────

/** One extraction SUMMARY (header only — never the analyte values). Mirrors
 *  OcrExtractionSummaryDto. */
export const OcrExtractionSummarySchema = z.object({
  extractionId: z.string(),
  sourceType: z.string(),
  status: z.string(),
  overallConfidence: z.number().nullish(),
  requiresHumanReview: z.boolean(),
  abnormalCount: z.number(),
  createdAt: z.string(),
});
export type OcrExtractionSummary = z.infer<typeof OcrExtractionSummarySchema>;

/** `GET /api/v1/ai/extractions?limit=` → OcrExtractionListDto (summaries only, no
 *  PHI analyte values). Mirrors OcrExtractionListDto. */
export const OcrExtractionListSchema = z.object({
  available: z.boolean(),
  extractions: z.array(OcrExtractionSummarySchema).default([]),
  source: z.string().nullish(),
});
export type OcrExtractionList = z.infer<typeof OcrExtractionListSchema>;

/** A RAG knowledge base's summary counts. Mirrors RagKnowledgeBaseDto. */
export const RagKnowledgeBaseSchema = z.object({
  kbKey: z.string(),
  name: z.string(),
  documentCount: z.number(),
});
export type RagKnowledgeBase = z.infer<typeof RagKnowledgeBaseSchema>;

/** `GET /api/v1/ai/rag/status` → RagStatusDto (operational counts; NO PHI). Mirrors
 *  RagStatusDto. */
export const RagStatusSchema = z.object({
  available: z.boolean(),
  embeddings: z.number().nullish(),
  patientsIndexed: z.number().nullish(),
  knowledgeBases: z.array(RagKnowledgeBaseSchema).default([]),
  source: z.string().nullish(),
});
export type RagStatus = z.infer<typeof RagStatusSchema>;

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

/** `GET /api/v1/tenants?skip&take` row. Mirrors TenantDto (platform.tenants),
 *  gated by `platform.tenants.read` (super_admin). Used by the begin-impersonation
 *  target selector. Trailing fields are additive context; the picker only needs
 *  tenantId + displayName + tenantCode. */
export const TenantListItemSchema = z.object({
  tenantId: z.string(),
  tenantCode: z.string(),
  displayName: z.string(),
  tenantType: z.string(),
  primaryEmail: z.string().nullable().optional(),
  status: z.string().nullable().optional(),
  country: z.string().nullable().optional(),
  city: z.string().nullable().optional(),
});
export type TenantListItem = z.infer<typeof TenantListItemSchema>;

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

/** A role the user holds in the ACTIVE tenant (from platform.roles). `name` is the
 *  English display name rendered as-is (backend-driven — never branch on roleKey). */
export const MeRoleSchema = z.object({
  roleKey: z.string(),
  name: z.string(),
});
export type MeRole = z.infer<typeof MeRoleSchema>;

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
  // Roles for the active tenant. Optional-with-default so a stale API (or the mock
  // seam) that omits it still parses — the Sidebar falls back to the i18n label.
  roles: z.array(MeRoleSchema).default([]),
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
  // Presence (#87 bonus): most-recent active session last_activity_at. Drives the
  // People "Online" dot (recent → green) vs a "last seen" line. Nullable/defaulted
  // (a user with no active session has no activity timestamp).
  lastActivityAt: z.string().nullable().default(null),
  // Account security posture for the manage panel: when the account is locked, and whether a
  // password change is pending (after an admin reset). Both nullable/defaulted for resilience.
  lockedUntil: z.string().nullable().default(null),
  mustChangePassword: z.boolean().default(false),
  // Org SCOPE (#90) — a DISPLAY-only branch + department for the membership. Never
  // confers permissions. Null branch = "All branches"; null/blank department = "All
  // departments". `branchName` is the resolved label for the row (the id drives the
  // People "All branches" filter). All defaulted so older responses parse unchanged.
  branchId: z.string().nullable().default(null),
  branchName: z.string().nullable().default(null),
  department: z.string().nullable().default(null),
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

/** RAW shape the LIVE `GET /tenants/{tenantId}/users` returns. The server now MASKS
 *  the phone (DPDP — raw phone never crosses the wire in an aggregate), so this
 *  carries `maskedPhone` directly + the account security posture (lockedUntil,
 *  mustChangePassword) and the user's roles in the tenant. lib/backend/real.ts passes
 *  it through 1:1. Tolerant/passthrough so additive backend fields don't break
 *  parsing; roles defaults to [] for resilience if ever omitted. */
export const UserListItemDtoSchema = z
  .object({
    userId: z.string(),
    email: z.string(),
    fullName: z.string(),
    maskedPhone: z.string().nullable().optional(),
    isActive: z.boolean(),
    mfaEnabled: z.boolean(),
    lastLoginAt: z.string().nullable().optional(),
    // #87 bonus — appended nullable/optional so the existing shape is preserved
    // when the server omits it (older builds) → the People "Online" dot degrades
    // gracefully to a last-login line.
    lastActivityAt: z.string().nullable().optional(),
    lockedUntil: z.string().nullable().optional(),
    mustChangePassword: z.boolean().optional().default(false),
    // #90 — org scope (display-only): appended nullable/optional so older builds that
    // omit them still parse (the row degrades to "All branches / All departments").
    branchId: z.string().nullable().optional(),
    branchName: z.string().nullable().optional(),
    department: z.string().nullable().optional(),
    roles: z
      .array(
        z.object({
          userTenantRoleId: z.string(),
          roleId: z.string(),
          roleKey: z.string(),
          name: z.string(),
          isPrimary: z.boolean(),
          expiresAt: z.string().nullable().optional(),
        }),
      )
      .optional()
      .default([]),
  })
  .passthrough();
export type UserListItemDto = z.infer<typeof UserListItemDtoSchema>;

/** A tenant's physical branch/location (#90). Mirrors `BranchDto`. An organizational
 *  DISPLAY attribute only — it heads the People "All branches" filter + the header
 *  "N branches" stat and never confers permissions. `code` is an optional short label. */
export const BranchSchema = z.object({
  branchId: z.string(),
  name: z.string(),
  code: z.string().nullable().optional(),
  isActive: z.boolean(),
});
export type Branch = z.infer<typeof BranchSchema>;

/** `PUT /tenants/{id}/users/{userId}/scope` body — set a member's org scope (DISPLAY
 *  only). Null `branchId` = "All branches"; null/blank `department` = "All departments".
 *  Routed server-side through `platform.set_membership_scope`, which writes ONLY
 *  branch_id/department (never role_id) so it can never change effective access. */
export interface SetMemberScopeRequest {
  branchId: string | null;
  department: string | null;
}

/** Result of the scope write. Echoes the affected membership row + its new scope. */
export const SetMemberScopeResultSchema = z.object({
  userTenantRoleId: z.string(),
  branchId: z.string().nullable(),
  department: z.string().nullable(),
});
export type SetMemberScopeResult = z.infer<typeof SetMemberScopeResultSchema>;

/** `POST /tenants/{id}/users` body. Mirrors CreateUserRequest. */
export const CreateUserRequestSchema = z.object({
  email: z.string(),
  fullName: z.string(),
  phone: z.string().nullable().optional(),
  preferredLanguage: z.string().default('en'),
  initialRoleId: z.string().nullable().optional(),
});
export type CreateUserRequest = z.infer<typeof CreateUserRequestSchema>;

/** `alreadyExisted`=true when the email matched a global identity and we only linked a
 *  new tenant membership (the existing profile is never overwritten). */
export const CreateUserResultSchema = z.object({
  userId: z.string(),
  alreadyExisted: z.boolean().optional().default(false),
});
export type CreateUserResult = z.infer<typeof CreateUserResultSchema>;

// ── EXPORT + BULK IMPORT (#95) ─────────────────────────────────────────────────
// GET /tenants/{id}/users/export (gated tenant.users.read) streams a text/csv of
// the tenant's members; POST /tenants/{id}/users/bulk-import (gated tenant.users.create)
// provisions each parsed row via the single-user path (per-row atomic; role subject
// to the R3 no-escalation guard). The CSV export result is built client-side (the
// real endpoint streams text/csv with a Content-Disposition filename) and NEVER cached.

/** One import row, parsed client-side from the pasted/uploaded CSV. `roleKey` is
 *  OPTIONAL — omit it and the invitee is provisioned with no tenant role. */
export const BulkImportRowSchema = z.object({
  email: z.string(),
  fullName: z.string(),
  roleKey: z.string().nullable().optional(),
});
export type BulkImportRow = z.infer<typeof BulkImportRowSchema>;

/** `POST /tenants/{id}/users/bulk-import` body. Batch cap is 500 server-side (oversize
 *  → 422); the panel enforces the same cap before it POSTs. */
export const BulkImportUsersRequestSchema = z.object({
  rows: z.array(BulkImportRowSchema),
});
export type BulkImportUsersRequest = z.infer<typeof BulkImportUsersRequestSchema>;

/** Per-row outcome. `status` is left a string (not an enum) so an additive backend
 *  status doesn't break parsing; the UI lower-cases it for its label/tint lookup and
 *  falls back to a neutral pill for anything unknown. `row` is the 1-based input index. */
export const BulkImportResultRowSchema = z.object({
  row: z.number(),
  email: z.string(),
  status: z.string(),
  message: z.string().nullable().optional().default(''),
});
export type BulkImportResultRow = z.infer<typeof BulkImportResultRowSchema>;

/** Summary + per-row results. The summary counts are authoritative (server-computed);
 *  the panel renders them directly rather than recomputing from `rows`. Passthrough so
 *  additive backend fields survive. */
export const BulkImportResultSchema = z
  .object({
    total: z.number(),
    created: z.number(),
    linked: z.number(),
    skipped: z.number(),
    errored: z.number(),
    rows: z.array(BulkImportResultRowSchema),
  })
  .passthrough();
export type BulkImportResult = z.infer<typeof BulkImportResultSchema>;

/** The CSV download result the seam hands to the toolbar to trigger a browser download.
 *  Same shape as {@link AuditCsvResult}; kept distinct for call-site clarity. Never cached. */
export interface UserCsvResult {
  fileName: string;
  content: string;
}

// ── User lifecycle (deactivate/reactivate, edit profile, reset access) ─────────
/** `PUT /tenants/{id}/users/{userId}/status`. Reason mandatory when deactivating. */
export const SetUserStatusRequestSchema = z.object({
  isActive: z.boolean(),
  reason: z.string(),
});
export type SetUserStatusRequest = z.infer<typeof SetUserStatusRequestSchema>;

export const SetUserStatusResultSchema = z.object({ userId: z.string(), isActive: z.boolean() });
export type SetUserStatusResult = z.infer<typeof SetUserStatusResultSchema>;

/** `PUT /tenants/{id}/users/{userId}`. Whitelisted fields only (never email/auth/status). */
export const UpdateUserProfileRequestSchema = z.object({
  fullName: z.string(),
  phone: z.string().nullable().optional(),
  preferredLanguage: z.enum(['en', 'hi']).default('en'),
});
export type UpdateUserProfileRequest = z.infer<typeof UpdateUserProfileRequestSchema>;

export const UpdateUserProfileResultSchema = z.object({ userId: z.string() });
export type UpdateUserProfileResult = z.infer<typeof UpdateUserProfileResultSchema>;

/** `POST /tenants/{id}/users/{userId}/reset-access`. Reason mandatory; flags only (no plaintext). */
export const ResetAccessRequestSchema = z.object({ reason: z.string() });
export type ResetAccessRequest = z.infer<typeof ResetAccessRequestSchema>;

export const ResetAccessResultSchema = z.object({ userId: z.string() });
export type ResetAccessResult = z.infer<typeof ResetAccessResultSchema>;

/** A role. Mirrors RoleDto. `isSystem` roles are read-only; `tenantId===null` = system. */
export const RoleSchema = z.object({
  roleId: z.string(),
  roleKey: z.string(),
  name: z.string(),
  scope: z.enum(['platform', 'tenant']),
  isSystem: z.boolean(),
  tenantId: z.string().nullable(),
  /** #84 — distinct active users holding this role (revoked/expired assignments
   *  excluded). Mirrors RoleDto.MemberCount; defaults to 0 for producers that
   *  synthesise a fresh role (create/duplicate results carry no members yet). */
  memberCount: z.number().int().nonnegative().default(0),
});
export type Role = z.infer<typeof RoleSchema>;

/** `POST /api/v1/roles` result. Mirrors CreateRoleResult — the new role's id. The
 *  create DTO is EMPTY-role only (no permissionKeys); initial grants are attached
 *  afterwards via the per-grant guarded /iam/roles/{id}/permissions/{permId} endpoint. */
export const CreateRoleResultSchema = z.object({ roleId: z.string() });
export type CreateRoleResult = z.infer<typeof CreateRoleResultSchema>;

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

/** `POST /api/v1/role-assignments/revoke` result. Mirrors RevokeRoleResult. */
export const RevokeRoleResultSchema = z.object({
  userTenantRoleId: z.string(),
  alreadyRevoked: z.boolean(),
});
export type RevokeRoleResult = z.infer<typeof RevokeRoleResultSchema>;

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

/** One row of the tenant-wide per-user override list (#85). Mirrors
 *  TenantPermissionOverrideDto — carries the TARGET user's identity inline so the
 *  list renders without an N+1 user lookup. `isAllowed=false` is a deny (deny-wins
 *  over any role grant); `active` = effective right now (started, not expired, not
 *  revoked). `effectiveFrom` is when the override starts applying. */
export const TenantPermissionOverrideSchema = z.object({
  overrideId: z.string(),
  userId: z.string(),
  userDisplayName: z.string(),
  userEmail: z.string(),
  permissionKey: z.string(),
  isAllowed: z.boolean(),
  reason: z.string(),
  effectiveFrom: z.string(),
  expiresAt: z.string().nullable(),
  active: z.boolean(),
});
export type TenantPermissionOverride = z.infer<typeof TenantPermissionOverrideSchema>;

/** `GET /api/v1/iam/overrides` result. Mirrors TenantOverridesListDto — every
 *  per-user override in the caller's tenant plus a server-computed `count` (drives
 *  the Roles & permissions "Per-user overrides" sub-tab badge). Gated server-side
 *  on platform.overrides.read (distinct from the dangerous platform.overrides.grant). */
export const TenantOverridesListSchema = z.object({
  count: z.number().int().nonnegative(),
  overrides: z.array(TenantPermissionOverrideSchema),
});
export type TenantOverridesList = z.infer<typeof TenantOverridesListSchema>;

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
  /** #88a — count of API calls (any status) attributed to this client in the last
   *  24h. Mirrors ApiClientDto.RequestsLast24h; defaults to 0 for freshly-registered
   *  clients (and for producers that synthesise a client without traffic). */
  requestsLast24h: z.number().int().nonnegative().default(0),
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
  /** #88b — fraction (0..1) of deliveries that SUCCEEDED in the last 7 days, or
   *  null when there were no deliveries in the window (server divide-by-zero guard).
   *  Mirrors WebhookSubscriptionDto.DeliverySuccessRate7d (double?). Format at the
   *  edge as a percentage; null renders as "no deliveries". */
  deliverySuccessRate7d: z.number().nullable().default(null),
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
  status: z.string(), // widened: SecurityController passes the raw DB status; the CHECK allows more values (#53)
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
// AUDIT LOG (#86) — mirrors mediq.SharedDataModel/Docslot/Security/AuditReadDtos.cs
// (camelCase wire). Surfaced in the Team console "Audit log" tab, gated on
// tenant.audit.read. NO PHI: actor is a staff identity (same directory as People);
// resourceLabel is a server-humanized label; ipAddress carries an optional resolved
// `city` (#94, IGeoIpResolver) — null offline (NullGeoIpResolver) → raw IP only.
// ─────────────────────────────────────────────────────────────────────────────

/** Derived category bucket. The server humanizes raw verbs into these. */
export const AuditCategorySchema = z.enum([
  'Bookings',
  'Patients',
  'Payments',
  'Team',
  'Settings',
  'Security',
  'Analytics',
  'Other',
]);
export type AuditCategory = z.infer<typeof AuditCategorySchema>;

/** Derived severity. */
export const AuditSeveritySchema = z.enum(['Informational', 'Warning', 'Critical']);
export type AuditSeverity = z.infer<typeof AuditSeveritySchema>;

/** One faceted count (category or severity → rows in the current range/search). */
export const AuditFacetCountSchema = z.object({
  key: z.string(),
  count: z.number(),
});
export type AuditFacetCount = z.infer<typeof AuditFacetCountSchema>;

/** A single audit-timeline row. Mirrors AuditLogRowDto. `action` is humanized,
 *  `rawAction` is the original verb (shown as a mono sub-label). */
export const AuditLogRowSchema = z.object({
  auditId: z.string(),
  occurredAt: z.string(),
  actorUserId: z.string().nullable(),
  actorName: z.string().nullable(),
  actorEmail: z.string().nullable(),
  impersonatorUserId: z.string().nullable(),
  impersonatorName: z.string().nullable(),
  action: z.string(),
  rawAction: z.string(),
  resourceType: z.string(),
  resourceLabel: z.string().nullable(),
  resourceId: z.string().nullable(),
  category: z.string(),
  severity: z.string(),
  ipAddress: z.string().nullable(),
  /** Resolved city for `ipAddress` (#94, IGeoIpResolver enrichment). Null when the
   *  resolver is the offline NullGeoIpResolver or the IP can't be located; the row
   *  then shows just the raw IP. Optional so pre-#94 payloads still parse. */
  city: z.string().nullable().optional(),
  success: z.boolean(),
  errorCode: z.string().nullable(),
});
export type AuditLogRow = z.infer<typeof AuditLogRowSchema>;

/** A page of audit rows + the category/severity facets + the resolved window.
 *  Mirrors AuditLogPageDto. Tolerant (passthrough) so additive server fields don't
 *  break parsing. */
export const AuditLogPageSchema = z
  .object({
    page: z.number(),
    pageSize: z.number(),
    total: z.number(),
    items: z.array(AuditLogRowSchema),
    categoryFacets: z.array(AuditFacetCountSchema).default([]),
    severityFacets: z.array(AuditFacetCountSchema).default([]),
    from: z.string(),
    to: z.string(),
  })
  .passthrough();
export type AuditLogPage = z.infer<typeof AuditLogPageSchema>;

/** Client-side filter for the audit list + CSV export. `from`/`to` are ISO strings
 *  (the UI defaults to the last 30 days). Not a wire schema — request input only. */
export interface AuditLogFilter {
  page: number;
  pageSize: number;
  from: string;
  to: string;
  category?: string | null;
  severity?: string | null;
  search?: string | null;
}

/** The CSV export result the seam hands to the component to trigger a download.
 *  Constructed client-side (the real endpoint streams text/csv with a filename in
 *  Content-Disposition); never cached in a query key. */
export interface AuditCsvResult {
  fileName: string;
  content: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// ACTIVE SESSIONS (#87) — mirrors mediq.SharedDataModel/Docslot/Security/
// SessionAdminDtos.cs (camelCase wire). Surfaced in the Team console "Security"
// tab, gated on tenant.users.update. NO PHI (staff identities only).
// ─────────────────────────────────────────────────────────────────────────────

export const ActiveSessionSchema = z.object({
  sessionId: z.string(),
  userId: z.string(),
  userName: z.string(),
  userEmail: z.string().nullable(),
  ipAddress: z.string().nullable(),
  /** Resolved city for `ipAddress` (#94, IGeoIpResolver enrichment). Null when the
   *  resolver is the offline NullGeoIpResolver or the IP can't be located; the row
   *  then shows just the raw IP. Optional so pre-#94 payloads still parse. */
  city: z.string().nullable().optional(),
  startedAt: z.string(),
  lastActivityAt: z.string(),
  expiresAt: z.string(),
  /** True for the caller's own current session (revoking it signs the caller out). */
  isSelf: z.boolean(),
});
export type ActiveSession = z.infer<typeof ActiveSessionSchema>;

/** Result of signing out all of a user's sessions. Mirrors RevokeAllSessionsResult. */
export const RevokeAllSessionsResultSchema = z.object({
  userId: z.string(),
  revokedCount: z.number(),
});
export type RevokeAllSessionsResult = z.infer<typeof RevokeAllSessionsResultSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// SECURITY POLICY (issue #91, epic #80 Phase C) — mirrors
// mediq.SharedDataModel/Docslot/Security/SecurityPolicyDtos.cs (camelCase wire).
// Surfaced in the Team console "Security" tab, gated on tenant.settings.read
// (view) / tenant.settings.update (edit). The policy lives in
// platform.tenants.settings->'security' (no new table); absent keys merge over
// code defaults (all gates OFF, masking ON, minLen 8), so an unconfigured tenant
// still returns a fully-populated object. EVERY field is really enforced in the
// request path (login / password-set / patient-read) — no dead toggles. NO PHI.
// ─────────────────────────────────────────────────────────────────────────────

/** 2FA-enforcement tiers. Server-validated on write; anything else is rejected. */
export const MfaPolicySchema = z.enum(['optional', 'owners_admins', 'all']);
export type MfaPolicy = z.infer<typeof MfaPolicySchema>;

/** The editable policy fields (the `PUT /security/policy` body shape). Kept as its own
 *  object schema — NOT derived via Omit from the passthrough view below, since a
 *  passthrough index signature would collapse an Omit to `{ [k]: unknown }`. */
export const SecurityPolicyFieldsSchema = z.object({
  mfaPolicy: MfaPolicySchema,
  minPasswordLength: z.number().int(),
  idleTimeoutMinutes: z.number().int(),
  requireNewDeviceVerification: z.boolean(),
  restrictLoginHours: z.boolean(),
  loginHoursStart: z.string(),
  loginHoursEnd: z.string(),
  doctorsExemptFromHours: z.boolean(),
  ipAllowlistEnabled: z.boolean(),
  maskSensitiveForReceptionist: z.boolean(),
});
/** `PUT /security/policy` body — the editable fields only. Request input. */
export type SecurityPolicyInput = z.infer<typeof SecurityPolicyFieldsSchema>;

/** The effective security policy + the derived pending-2FA-enrolment staff count.
 *  Mirrors SecurityPolicyDto. `staffPendingMfaEnrolment` is READ-ONLY (server-derived,
 *  recomputed on every GET/PUT) — never sent back on update. Tolerant (passthrough)
 *  so additive server fields don't break parsing. */
export const SecurityPolicyViewSchema = SecurityPolicyFieldsSchema.extend({
  /** Derived: active staff subject to a REQUIRED-2FA tier who still lack mfa_enabled
   *  and would be forced to enrol on next login. 0 when mfaPolicy = optional. */
  staffPendingMfaEnrolment: z.number().int().nonnegative().default(0),
}).passthrough();
export type SecurityPolicyView = z.infer<typeof SecurityPolicyViewSchema>;

/** One row of platform.ip_allowlist (tenant-scoped). Mirrors IpAllowlistEntryDto.
 *  The raw CIDR is network metadata, not a secret — safe to surface. Dates are ISO
 *  strings on the wire (.NET DateTimeOffset). */
export const IpAllowlistEntrySchema = z
  .object({
    allowlistId: z.string(),
    cidrRange: z.string(),
    label: z.string().nullable().default(null),
    isActive: z.boolean().default(true),
    createdAt: z.string(),
    expiresAt: z.string().nullable().default(null),
  })
  .passthrough();
export type IpAllowlistEntry = z.infer<typeof IpAllowlistEntrySchema>;

/** `POST /security/ip-allowlist` body. Mirrors AddIpAllowlistRequest. Request input
 *  only. The server validates the CIDR/IP authoritatively (422 on a bad value). */
export interface AddIpAllowlistRequest {
  cidrRange: string;
  label?: string | null;
  expiresAt?: string | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// WORKSPACE SETTINGS (Settings screen — Phase 1). Mirrors
// mediq.SharedDataModel/Docslot/Settings/SettingsDtos.cs (camelCase wire). Surfaced
// on /settings; the facility row is bound from the JWT tenant server-side. GET gates
// tenant.settings.read (404 when the tenant has no facility row); PATCH gates
// tenant.settings.update — each supplied section REPLACES that section (send the full
// section object, not a diff). The WhatsApp access token is NEVER present on the wire —
// keep it out of this contract. NO PHI (tenant configuration only).
// ─────────────────────────────────────────────────────────────────────────────

/** One weekday's opening window. `open`/`close` are "HH:mm" (IST) or null; `closed`
 *  marks the day shut (times ignored). Absent day keys are treated as closed by the UI. */
export const BusinessHoursDaySchema = z.object({
  open: z.string().nullable(),
  close: z.string().nullable(),
  closed: z.boolean(),
});
export type BusinessHoursDay = z.infer<typeof BusinessHoursDaySchema>;

/** Weekly business hours keyed by mon..sun (a subset may be present on the wire). */
export const BusinessHoursSchema = z.record(z.string(), BusinessHoursDaySchema);
export type BusinessHours = z.infer<typeof BusinessHoursSchema>;

/** Tenant-default appointment rules (a doctor-specific override, when present, wins).
 *  Ranges are server-validated (422): slotDuration 5..120, cutoff ≥0, maxAdvance >0,
 *  reminder ≥0, grace 0..240. The schema stays permissive (ints) — the form + server
 *  enforce the bounds so a regression surfaces at the API, not the parser. */
export const AppointmentSettingsSchema = z.object({
  slotDurationMinutes: z.number().int(),
  bookingCutoffHours: z.number().int(),
  autoConfirm: z.boolean(),
  maxAdvanceDays: z.number().int(),
  allowOverbooking: z.boolean(),
  reminderHoursBefore: z.number().int(),
  noShowGraceMinutes: z.number().int(),
});
export type AppointmentSettings = z.infer<typeof AppointmentSettingsSchema>;

/** WhatsApp Cloud API connection status. The access token is NEVER serialized — this
 *  shape intentionally omits it. `verifiedAt` is an ISO string (.NET DateTimeOffset). */
export const WhatsappSettingsSchema = z.object({
  connected: z.boolean(),
  phoneNumberId: z.string().nullable(),
  verifiedAt: z.string().nullable(),
});
export type WhatsappSettings = z.infer<typeof WhatsappSettingsSchema>;

/** ABDM Health Facility Registry linkage (display-only). */
export const HfrSettingsSchema = z.object({
  id: z.string().nullable(),
  status: z.string().nullable(),
});
export type HfrSettings = z.infer<typeof HfrSettingsSchema>;

/** `GET /settings` → SettingsDto. Tolerant (passthrough) so additive server fields
 *  don't break parsing. facilityType/specialtyFocus are read-only identity. */
export const SettingsSchema = z
  .object({
    facilityType: z.string(),
    specialtyFocus: z.string().nullable(),
    businessHours: BusinessHoursSchema,
    appointmentSettings: AppointmentSettingsSchema,
    // WIRE NOTE: the live API serializes this as `whatsApp` (capital A) — .NET
    // camelCases the C# `WhatsApp` property, lowercasing only the first char. The
    // handoff contract wrote `whatsapp`; the running endpoint is authoritative, so the
    // app-facing field matches the wire (mock seeds it the same → no adapter needed).
    whatsApp: WhatsappSettingsSchema,
    hfr: HfrSettingsSchema,
  })
  .passthrough();
export type Settings = z.infer<typeof SettingsSchema>;

/** `PATCH /settings` body — at least one section; each is a FULL replace (not a diff).
 *  No Idempotency-Key (configuration write, not a money/booking mutation). */
export const UpdateSettingsRequestSchema = z.object({
  businessHours: BusinessHoursSchema.optional(),
  appointmentSettings: AppointmentSettingsSchema.optional(),
});
export type UpdateSettingsRequest = z.infer<typeof UpdateSettingsRequestSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// INVITATIONS (issue #89, epic #80 Phase C) — token-based tenant onboarding.
// Mirrors mediq.SharedDataModel/Docslot/Admin/InvitationDtos.cs (camelCase wire).
// The plaintext token is returned EXACTLY ONCE (create/resend); the list/read
// shapes NEVER carry the token or its hash. NO PHI — invited staff identities only.
// ─────────────────────────────────────────────────────────────────────────────

/** One invitation row for the console list. Mirrors InvitationDto. Never carries
 *  the token/hash. `roleName` is the LEFT-JOINed display name (null when no role
 *  was pre-attached). Status is DB-CHECK-constrained to the four values below. */
export const InvitationSchema = z.object({
  invitationId: z.string(),
  invitedEmail: z.string(),
  roleId: z.string().nullable(),
  roleName: z.string().nullable(),
  status: z.enum(['pending', 'accepted', 'revoked', 'expired']),
  expiresAt: z.string(),
  resendCount: z.number().int().nonnegative(),
  invitedByUserId: z.string().nullable(),
  acceptedUserId: z.string().nullable(),
  acceptedAt: z.string().nullable(),
  revokedAt: z.string().nullable(),
  createdAt: z.string(),
});
export type Invitation = z.infer<typeof InvitationSchema>;
export type InvitationStatus = Invitation['status'];

/** The invitation list + a count for the tab badge. Mirrors InvitationListDto. */
export const InvitationListSchema = z.object({
  items: InvitationSchema.array(),
  count: z.number().int().nonnegative(),
});
export type InvitationList = z.infer<typeof InvitationListSchema>;

/** `POST /tenants/{id}/invitations` (+ `/resend`) result. Mirrors InvitationTokenResult.
 *  `token` is the ONE-TIME plaintext — surfaced once for hand-off (send lands in #93),
 *  never persisted or re-fetchable. This response is never idempotency-cached server-side. */
export const InvitationTokenResultSchema = z.object({
  invitationId: z.string(),
  token: z.string(),
  expiresAt: z.string(),
  resendCount: z.number().int().nonnegative(),
});
export type InvitationTokenResult = z.infer<typeof InvitationTokenResultSchema>;

/** `POST /tenants/{id}/invitations/{id}/revoke` result. `alreadyInactive`=true when
 *  it was not pending (idempotent). Mirrors RevokeInvitationResult. */
export const RevokeInvitationResultSchema = z.object({
  invitationId: z.string(),
  alreadyInactive: z.boolean(),
});
export type RevokeInvitationResult = z.infer<typeof RevokeInvitationResultSchema>;

/** `POST /tenants/{id}/invitations` body. Mirrors CreateInvitationRequest. The role
 *  is OPTIONAL (the invitee gets no tenant role until accept if omitted); the actor
 *  may only pre-attach a role they may confer (R3 no-escalation, enforced at the DB). */
export const CreateInvitationRequestSchema = z.object({
  email: z.string(),
  roleId: z.string().nullable().optional(),
});
export type CreateInvitationRequest = z.infer<typeof CreateInvitationRequestSchema>;

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

/** LEGACY free-text medication line ({name,dose,frequency,duration}). Kept as a
 *  graceful fallback for older prescriptions; new writes use StructuredMedication. */
export const MedicationSchema = z.object({
  name: z.string(),
  dose: z.string(),
  frequency: z.string(),
  duration: z.string(),
});
export type Medication = z.infer<typeof MedicationSchema>;

/** Food timing for a structured medication line. Mirrors the SQL CHECK. */
export const MedTimingSchema = z.enum(['after_food', 'before_food', 'empty_stomach', 'anytime']);
export type MedTiming = z.infer<typeof MedTimingSchema>;

/** A structured medication line — the shape the composer, preview and WhatsApp
 *  reminder copy all consume. `dose` is a morning-noon-night triple (0/1/2…);
 *  `sos` (as-needed) and `weekly` are flags that override the triple for display.
 *  strength ("650 mg") / form ("tab") / durationDays / instructions are optional.
 *  Passthrough-tolerant on unknown keys so an additive backend field never drops
 *  the item. */
export const StructuredMedicationSchema = z.object({
  name: z.string(),
  strength: z.string().nullable().default(null),
  form: z.string().nullable().default(null),
  dose: z.object({
    morning: z.number().nonnegative().default(0),
    noon: z.number().nonnegative().default(0),
    night: z.number().nonnegative().default(0),
  }),
  sos: z.boolean().default(false),
  weekly: z.boolean().default(false),
  timing: MedTimingSchema.default('anytime'),
  durationDays: z.number().int().positive().nullable().default(null),
  instructions: z.string().nullable().default(null),
});
export type StructuredMedication = z.infer<typeof StructuredMedicationSchema>;

/** A medication line as it appears on the wire: the NEW structured shape, or the
 *  LEGACY free-text {name,dose,frequency,duration} fallback. zod tries structured
 *  first (its `dose` is an object); a legacy row's string `dose` fails that and
 *  falls to the legacy branch. Consumers render either via {@link formatMedicationLine}. */
export const RxMedicationSchema = z.union([StructuredMedicationSchema, MedicationSchema]);
export type RxMedication = z.infer<typeof RxMedicationSchema>;

/** True for the legacy free-text medication shape (has a `frequency` string). */
export function isLegacyMedication(med: RxMedication): med is Medication {
  return 'frequency' in med && typeof (med as Medication).frequency === 'string';
}

/** PER-ITEM TOLERANT parse of a medications payload (a medicationsJson STRING, or an
 *  already-decoded array). Every item is validated against the RxMedication union
 *  INDEPENDENTLY — a single odd/legacy/partial item is dropped, never emptying or
 *  rejecting the whole list. This is the ONLY way prescriptions/consultations decode
 *  their medications, so a structured item can never be silently lost to the legacy
 *  schema again. */
export function parseMedications(raw: unknown): RxMedication[] {
  let arr: unknown = raw;
  if (typeof raw === 'string') {
    if (!raw.trim()) return [];
    try {
      arr = JSON.parse(raw);
    } catch {
      return [];
    }
  }
  if (!Array.isArray(arr)) return [];
  const out: RxMedication[] = [];
  for (const item of arr) {
    const parsed = RxMedicationSchema.safeParse(item);
    if (parsed.success) out.push(parsed.data);
  }
  return out;
}

/** The dose token for a structured line: "1-0-1", or "SOS" / "Weekly" when those
 *  flags are set. `t` maps the flag labels through i18n so it stays bilingual. */
export function formatDose(med: StructuredMedication, t: (key: string) => string): string {
  if (med.sos) return t('consult.dose.sos');
  if (med.weekly) return t('consult.dose.weekly');
  return `${med.dose.morning}-${med.dose.noon}-${med.dose.night}`;
}

/** Human display for a medication line — powers the Rx preview and the reminder
 *  copy. e.g. "1-0-1 · After food · 5 days" / "SOS · If vomiting". `t` supplies the
 *  bilingual timing/duration labels (never baked-in English). Legacy rows render as
 *  "dose · frequency · duration". */
export function formatMedicationLine(
  med: RxMedication,
  t: (key: string, opts?: Record<string, unknown>) => string,
): string {
  if (isLegacyMedication(med)) {
    return [med.dose, med.frequency, med.duration].filter(Boolean).join(' · ');
  }
  const parts: string[] = [formatDose(med, t)];
  parts.push(t(`consult.timing.${med.timing}`));
  if (med.durationDays != null) parts.push(t('consult.durationValue', { count: med.durationDays }));
  if (med.instructions && med.instructions.trim()) parts.push(med.instructions.trim());
  return parts.filter(Boolean).join(' · ');
}

/** Decrypted detail. Mirrors PrescriptionDto (medicationsJson parsed to array via
 *  {@link parseMedications} — the structured OR legacy union, per-item tolerant). */
export const PrescriptionDetailSchema = z.object({
  prescriptionId: z.string(),
  prescriptionNumber: z.string().nullable(),
  patientId: z.string(),
  doctorId: z.string(),
  doctorName: z.string(),
  chiefComplaints: z.string().nullable(),
  examination: z.string().nullable(),
  diagnosis: z.string().nullable(),
  medications: z.array(RxMedicationSchema),
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

// ---- Consultation composer (Phase A) ────────────────────────────────────────
// The doctor prescription-writing experience. ONE consultation record per booking
// (get-or-create), autosaved as a draft, then FINALIZED (draft → finalized) — the
// doctor's legal signing act. The author is server-derived (never client-asserted).
// PHI: vitals + clinical fields are purpose-of-use gated on read (same gate as
// other clinical content); the draft is never logged / URL-encoded.

/** Vitals — standard clinical PHI, stored unencrypted like diagnosis/advice. Every
 *  field is nullable (a partial intake is valid). `bp` is a free string ("120/80"). */
export const VitalsSchema = z.object({
  bp: z.string().nullable().default(null),
  pulseBpm: z.number().nullable().default(null),
  tempF: z.number().nullable().default(null),
  spo2: z.number().nullable().default(null),
  weightKg: z.number().nullable().default(null),
});
export type Vitals = z.infer<typeof VitalsSchema>;

/** Consultation lifecycle. `draft` is editable/autosaved; `finalized` is signed. */
export const ConsultationStatusSchema = z.enum(['draft', 'finalized']);
export type ConsultationStatus = z.infer<typeof ConsultationStatusSchema>;

/** `POST /api/v1/consultations` (get-or-create per booking) result, and what
 *  finalize/autosave-load return. `medicationsJson` (a string on the wire) is
 *  parsed to the `medications` array AT THE SEAM (like PrescriptionDetail), so the
 *  UI only ever sees the structured array. patientName lets the composer header +
 *  Rx preview render without a second lookup. */
export const ConsultationDraftSchema = z.object({
  consultationId: z.string(),
  prescriptionNumber: z.string().nullable(),
  bookingId: z.string(),
  patientId: z.string(),
  patientName: z.string(),
  status: ConsultationStatusSchema,
  vitals: VitalsSchema,
  chiefComplaints: z.string().nullable(),
  examination: z.string().nullable(),
  diagnosis: z.string().nullable(),
  medications: z.array(RxMedicationSchema),
  investigations: z.array(z.string()),
  advice: z.string().nullable(),
  followUpInDays: z.number().nullable(),
  updatedAt: z.string(),
});
export type ConsultationDraft = z.infer<typeof ConsultationDraftSchema>;

/** `PATCH /api/v1/consultations/{id}` body (autosave). Every field optional — a
 *  partial save patches only what changed. `medicationsJson` is the serialised
 *  structured-medications array (parsed back at the seam). Returns 204 (no PHI
 *  echoed). */
export const SaveConsultationRequestSchema = z.object({
  vitals: VitalsSchema.optional(),
  chiefComplaints: z.string().nullable().optional(),
  examination: z.string().nullable().optional(),
  diagnosis: z.string().nullable().optional(),
  medicationsJson: z.string().optional(),
  investigations: z.array(z.string()).optional(),
  advice: z.string().nullable().optional(),
  followUpInDays: z.number().nullable().optional(),
});
export type SaveConsultationRequest = z.infer<typeof SaveConsultationRequestSchema>;

/** A drug-safety alert raised at finalize (interaction / allergy / duplicate).
 *  `severity` ∈ low/moderate/high/critical; high+critical BLOCK finalize until
 *  overridden with a reason. Mirrors the drug-alert contract. */
export const DrugAlertSeveritySchema = z.enum(['low', 'moderate', 'high', 'critical']);
export type DrugAlertSeverity = z.infer<typeof DrugAlertSeveritySchema>;

export const DrugAlertSchema = z.object({
  alertId: z.string(),
  alertType: z.string(),
  severity: DrugAlertSeveritySchema,
  medicationName: z.string(),
  description: z.string(),
  overridden: z.boolean(),
  createdAt: z.string(),
});
export type DrugAlert = z.infer<typeof DrugAlertSchema>;

/** `POST /api/v1/consultations/{id}/finalize` result. `finalized:false` means the
 *  sign was BLOCKED by unoverridden high/critical alerts — surface them inline,
 *  collect an override reason, and retry finalize with it. On success `finalized`
 *  is true and the PRX number is minted. */
export const FinalizeConsultationResultSchema = z.object({
  finalized: z.boolean(),
  prescriptionId: z.string().nullable().default(null),
  prescriptionNumber: z.string().nullable().default(null),
  alerts: z.array(DrugAlertSchema).default([]),
});
export type FinalizeConsultationResult = z.infer<typeof FinalizeConsultationResultSchema>;

// ---- Lab reports ------------------------------------------------------------
/** List row — NO clinical content. (No backend list endpoint yet — flagged.) */
export const LabReportListItemSchema = z.object({
  reportId: z.string(),
  reportNumber: z.string().nullable(),
  testName: z.string(),
  status: z.string(), // DB CHECK allows pending/processing/ready/delivered/cancelled — widened from a 2-value enum (#53)
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
  status: z.string(), // DB CHECK allows pending/processing/ready/delivered/cancelled — widened from a 2-value enum (#53)
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
/** Provenance of a medical-history row. 'clinic' = entered inside DocSlot (the
 *  default for every pre-existing row); the two external sources come from the
 *  paper-prescription intake flow. Read tolerantly: an unknown token falls back to
 *  'clinic' and an absent field defaults to 'clinic', so the pre-import API (which
 *  omits the field) still parses. The WRITE (import) side only ever sends the two
 *  external values (see {@link ImportMedicalHistorySourceSchema}). */
export const MedicalHistorySourceSchema = z.enum(['clinic', 'paper_prescription', 'patient_reported']);
export type MedicalHistorySource = z.infer<typeof MedicalHistorySourceSchema>;

/** Record-type enum for a NEW medical-history entry. Mirrors the SQL CHECK on
 *  docslot.medical_history.record_type. The READ shape keeps recordType as a free
 *  string (tolerant of server tokens); the WRITE form is constrained to this set. */
export const MedicalHistoryRecordTypeSchema = z.enum([
  'allergy',
  'chronic_condition',
  'surgery',
  'medication',
  'vaccination',
  'family_history',
  'lifestyle',
]);
export type MedicalHistoryRecordType = z.infer<typeof MedicalHistoryRecordTypeSchema>;

/** Severity enum for a medical-history entry (nullable — not every type carries one). */
export const MedicalHistorySeveritySchema = z.enum(['mild', 'moderate', 'severe', 'critical']);
export type MedicalHistorySeverity = z.infer<typeof MedicalHistorySeveritySchema>;

/** Decrypted timeline entry. Mirrors MedicalHistoryDto (non-encrypted scalars).
 *  Field order matches the C# record: historyId, recordType, title, description,
 *  severity, icd10Code, startedDate, endedDate, isActive, isCritical, addedAt.
 *  severity/icd10Code/startedDate/endedDate must round-trip on EDIT — the form
 *  edits severity but carries icd10Code/dates back from the read so a PUT (which
 *  treats missing as null) never silently wipes them. severity is read tolerantly
 *  (free-string→enum coerced in the form), the others are plain nullable strings. */
export const MedicalHistorySchema = z.object({
  historyId: z.string(),
  recordType: z.string(),
  title: z.string(),
  description: z.string().nullable(),
  severity: z.string().nullable(),
  icd10Code: z.string().nullable(),
  startedDate: z.string().nullable(),
  endedDate: z.string().nullable(),
  isActive: z.boolean(),
  isCritical: z.boolean(),
  addedAt: z.string(),
  // Paper-prescription intake fields. ALL optional with sane defaults so the
  // pre-import API (which omits every one of these) still parses cleanly.
  //  - source: provenance; 'clinic' for a normally-entered row.
  //  - verifiedAt: null ⇒ UNVERIFIED (an external row a doctor hasn't confirmed).
  //  - the rest annotate an imported external record (who wrote the paper Rx, the
  //    date on it, the batch it came in with, and its scanned attachment).
  source: MedicalHistorySourceSchema.default('clinic').catch('clinic'),
  externalDoctorName: z.string().nullable().default(null),
  recordedDate: z.string().nullable().default(null),
  verifiedAt: z.string().nullable().default(null),
  importBatchId: z.string().nullable().default(null),
  attachmentFileName: z.string().nullable().default(null),
  attachmentMimeType: z.string().nullable().default(null),
});
export type MedicalHistory = z.infer<typeof MedicalHistorySchema>;
/** INPUT shape (before defaults are applied) — the paper-Rx fields are optional
 *  here, so fixtures/seeds can omit them and rely on the schema defaults. */
export type MedicalHistoryInput = z.input<typeof MedicalHistorySchema>;

/** True when a row is an external (imported) record still awaiting a doctor's
 *  verification. A 'clinic' row is never "unverified" — it's authored in-app. */
export function isUnverifiedExternal(h: MedicalHistory): boolean {
  return h.source !== 'clinic' && h.verifiedAt === null;
}
/** True for any external (imported) row, verified or not. */
export function isExternalRecord(h: MedicalHistory): boolean {
  return h.source !== 'clinic';
}

/** Create body. Mirrors CreateMedicalHistoryRequest. title/description are PHI. */
export const CreateMedicalHistoryRequestSchema = z.object({
  recordType: MedicalHistoryRecordTypeSchema,
  title: z.string(),
  description: z.string().nullable().optional(),
  severity: MedicalHistorySeveritySchema.nullable().optional(),
  icd10Code: z.string().nullable().optional(),
  startedDate: z.string().nullable().optional(),
  endedDate: z.string().nullable().optional(),
  isCritical: z.boolean(),
});
export type CreateMedicalHistoryRequest = z.infer<typeof CreateMedicalHistoryRequestSchema>;

export const CreateMedicalHistoryResultSchema = z.object({ historyId: z.string() });
export type CreateMedicalHistoryResult = z.infer<typeof CreateMedicalHistoryResultSchema>;

/** Update body (PUT). isActive=false retires the record. Mirrors UpdateMedicalHistoryRequest. */
export const UpdateMedicalHistoryRequestSchema = z.object({
  recordType: MedicalHistoryRecordTypeSchema,
  title: z.string(),
  description: z.string().nullable().optional(),
  severity: MedicalHistorySeveritySchema.nullable().optional(),
  icd10Code: z.string().nullable().optional(),
  startedDate: z.string().nullable().optional(),
  endedDate: z.string().nullable().optional(),
  isActive: z.boolean(),
  isCritical: z.boolean(),
});
export type UpdateMedicalHistoryRequest = z.infer<typeof UpdateMedicalHistoryRequestSchema>;

// ── Paper-prescription import (front-desk intake of external history) ─────────
/** The two provenance values the IMPORT flow can send (a subset of
 *  {@link MedicalHistorySourceSchema} — 'clinic' is never imported). */
export const ImportMedicalHistorySourceSchema = z.enum(['paper_prescription', 'patient_reported']);
export type ImportMedicalHistorySource = z.infer<typeof ImportMedicalHistorySourceSchema>;

/** One transcribed line inside an import batch. title/description are PHI. */
export const ImportMedicalHistoryRecordSchema = z.object({
  recordType: MedicalHistoryRecordTypeSchema,
  title: z.string(),
  description: z.string().nullable().optional(),
  severity: MedicalHistorySeveritySchema.nullable().optional(),
  isCritical: z.boolean(),
  startedDate: z.string().nullable().optional(),
});
export type ImportMedicalHistoryRecord = z.infer<typeof ImportMedicalHistoryRecordSchema>;

/** The scanned paper Rx, sent inline as base64. The image bytes are PHI — held
 *  only in the form + the POST body, never the URL or a log. */
export const ImportMedicalHistoryAttachmentSchema = z.object({
  fileName: z.string(),
  contentType: z.string(),
  contentBase64: z.string(),
});
export type ImportMedicalHistoryAttachment = z.infer<typeof ImportMedicalHistoryAttachmentSchema>;

/** POST body for /medical-history/import. Records land UNVERIFIED (verifiedAt
 *  null) until a doctor confirms them. Mirrors ImportMedicalHistoryRequest. */
export const ImportMedicalHistoryRequestSchema = z.object({
  source: ImportMedicalHistorySourceSchema,
  externalDoctorName: z.string().nullable().optional(),
  recordedDate: z.string().nullable().optional(),
  attachment: ImportMedicalHistoryAttachmentSchema.nullable().optional(),
  records: z.array(ImportMedicalHistoryRecordSchema).min(1),
});
export type ImportMedicalHistoryRequest = z.infer<typeof ImportMedicalHistoryRequestSchema>;

/** 201 result: the batch id + the ids of the rows it created (tolerant of an
 *  absent historyIds array). */
export const ImportMedicalHistoryResultSchema = z.object({
  importBatchId: z.string(),
  historyIds: z.array(z.string()).default([]),
});
export type ImportMedicalHistoryResult = z.infer<typeof ImportMedicalHistoryResultSchema>;

// ── OCR assist: extract a paper prescription (advisory, human-in-the-loop) ───
/** Input for POST /medical-history/extract-prescription. The base64 image + the
 *  returned fields are PHI — request body only, never logged/cached; the result is
 *  a SUGGESTION the user reviews before importing (nothing auto-saves). */
export interface ExtractPrescriptionInput {
  patientId: string;
  fileName: string;
  contentType: string;
  contentBase64: string;
  purposeOfUse: string | undefined;
}

/** One AI-suggested line. recordType is a free string (coerced to the form enum);
 *  confidence is 0..1 or null. Tolerant so a lean/partial parse still loads. */
export const ExtractPrescriptionRecordSchema = z.object({
  recordType: z.string().default('medication'),
  title: z.string(),
  description: z.string().nullable().default(null),
  confidence: z.number().nullable().default(null),
});
export type ExtractPrescriptionRecord = z.infer<typeof ExtractPrescriptionRecordSchema>;

/** Result of the OCR extraction. `available:false` (or an HTTP error) means the UI
 *  falls back to manual entry — never a fabricated parse. Everything is tolerant. */
export const ExtractPrescriptionResultSchema = z.object({
  extractionId: z.string().nullable().default(null),
  overallConfidence: z.number().nullable().default(null),
  externalDoctorName: z.string().nullable().default(null),
  recordedDate: z.string().nullable().default(null),
  records: z.array(ExtractPrescriptionRecordSchema).default([]),
  rawText: z.string().nullable().default(null),
  available: z.boolean().default(true),
});
export type ExtractPrescriptionResult = z.infer<typeof ExtractPrescriptionResultSchema>;

// ── Unified patient timeline (GET /patients/{id}/timeline) ───────────────────
/** A backend-driven category chip. The server returns ONLY the categories the
 *  caller may read, with a bilingual label + count — the UI renders them verbatim
 *  (never hardcodes the category list), so a new category (e.g. 'imaging') appears
 *  automatically. `key` is a free string for forward-compat. */
export const TimelineCategorySchema = z.object({
  key: z.string(),
  labelEn: z.string(),
  labelHi: z.string(),
  count: z.number(),
});
export type TimelineCategory = z.infer<typeof TimelineCategorySchema>;

/** A card's link to its detail surface. `type` is a free string (tolerant): a
 *  known type opens the matching panel, an unknown one renders but is inert. */
export const TimelineRefSchema = z.object({
  type: z.string(),
  id: z.string(),
});
export type TimelineRef = z.infer<typeof TimelineRefSchema>;

/** One timeline card. title is required; the rest are tolerant (nullable/defaulted)
 *  so a lean backend row still parses. `category` matches a TimelineCategory.key. */
export const TimelineItemSchema = z.object({
  itemId: z.string(),
  category: z.string(),
  occurredAt: z.string(),
  title: z.string(),
  subtitle: z.string().nullable().default(null),
  summary: z.string().nullable().default(null),
  tags: z.array(z.string()).default([]),
  unverified: z.boolean().default(false),
  hasAttachment: z.boolean().default(false),
  ref: TimelineRefSchema,
});
export type TimelineItem = z.infer<typeof TimelineItemSchema>;

/** GET /patients/{id}/timeline. patientSince/visitCount feed the summary rail;
 *  categories drive the chips; items are the reverse-chronological cards. */
export const PatientTimelineSchema = z.object({
  patient: z
    .object({
      patientSince: z.string().nullable().default(null),
      visitCount: z.number().default(0),
    })
    .default({ patientSince: null, visitCount: 0 }),
  categories: z.array(TimelineCategorySchema).default([]),
  items: z.array(TimelineItemSchema).default([]),
});
export type PatientTimeline = z.infer<typeof PatientTimelineSchema>;

/** Break-glass (emergency access) request. resourceId is null for a whole-patient
 *  grant; justification is server-validated to >=10 chars. Mirrors the
 *  /security/break-glass body. Returns a grant id (Guid). */
export const BreakGlassResourceTypeSchema = z.enum(['prescription', 'lab_report', 'medical_history']);
export type BreakGlassResourceType = z.infer<typeof BreakGlassResourceTypeSchema>;

export const BreakGlassRequestSchema = z.object({
  patientId: z.string(),
  resourceType: BreakGlassResourceTypeSchema,
  resourceId: z.string().nullable(),
  justification: z.string().min(10),
});
export type BreakGlassRequest = z.infer<typeof BreakGlassRequestSchema>;

/** Result of a break-glass POST — the emergency-access grant id. */
export const BreakGlassResultSchema = z.object({ grantId: z.string() });
export type BreakGlassResult = z.infer<typeof BreakGlassResultSchema>;

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

// ---- Campaigns (admin) ------------------------------------------------------
// Mirrors CampaignDto / CreateCampaignRequest. bonusType is one of two supported
// kinds in the create form (tier_upgrade exists in the domain but is NOT offered
// here). The list shows spent-so-far against the total budget (a usage bar).
export const CampaignBonusTypeSchema = z.enum(['flat_bonus_per_booking', 'percentage_multiplier']);
export type CampaignBonusType = z.infer<typeof CampaignBonusTypeSchema>;

/** A marketing campaign. Mirrors CampaignDto (camelCase). `bonusType` is a free
 *  string on the wire (the domain has more kinds than the create form offers), so
 *  it stays a plain string here; the create form constrains input to the two
 *  supported kinds. */
export const CampaignSchema = z.object({
  campaignId: z.string(),
  campaignName: z.string(),
  bonusType: z.string(),
  bonusValue: z.number().nullable(),
  isActive: z.boolean(),
  totalBudgetInr: z.number().nullable(),
  spentSoFarInr: z.number(),
});
export type Campaign = z.infer<typeof CampaignSchema>;

export const CreateCampaignRequestSchema = z.object({
  campaignName: z.string(),
  bonusType: CampaignBonusTypeSchema,
  bonusValue: z.number().nullable(),
  startsAt: z.string(),
  endsAt: z.string(),
  totalBudgetInr: z.number().nullable(),
});
export type CreateCampaignRequest = z.infer<typeof CreateCampaignRequestSchema>;

// ---- TDS / Form 16A (section 194H) ------------------------------------------
// Mirrors Form16ACertificateDto. PHI: the DTO carries only the deductee PAN
// LAST 4 (safe to show); the legally-required FULL PAN lives ONLY on the rendered
// document at documentUrl — opened in a new tab, never logged/cached/in state.
// `status` is 'provisional' until the quarterly return is filed on TRACES.
export const Form16AStatusSchema = z.enum(['provisional', 'filed', 'revised', 'cancelled']);
export type Form16AStatus = z.infer<typeof Form16AStatusSchema>;

export const Form16ACertificateSchema = z.object({
  certificateId: z.string(),
  payoutId: z.string(),
  invoiceNumber: z.string().nullable(),
  section: z.string(),
  financialYear: z.string(),
  quarter: z.string(),
  deductorName: z.string(),
  deductorTan: z.string().nullable(),
  deducteeName: z.string(),
  /** PAN LAST 4 only — never the full PAN. */
  deducteePanLast4: z.string().nullable(),
  grossAmountInr: z.number(),
  tdsRate: z.number(),
  tdsAmountInr: z.number(),
  /** Free string on the wire ('provisional' until TRACES-filed). */
  status: z.string(),
  tracesCertificateNumber: z.string().nullable(),
  /** Where the FULL document (with full PAN) is rendered. Opened in a new tab. */
  documentUrl: z.string(),
});
export type Form16ACertificate = z.infer<typeof Form16ACertificateSchema>;

// ─────────────────────────────────────────────────────────────────────────────
// BROKER SELF-SERVICE PORTAL (Care Partner's OWN data) — base path /commission/me
// The server resolves broker_id from the JWT `broker_id` claim; there is NO id in
// any path (IDOR-safe). A Care Partner can only ever reach their own wallet/links
// and book on their own behalf.
//  - DPDP: book-on-behalf creates a BEHALF booking that triggers a patient consent
//    OTP (the patient approves via WhatsApp). The portal surfaces that.
// ─────────────────────────────────────────────────────────────────────────────

/** The Care Partner's commission wallet. Mirrors BrokerWalletDto. NO PAN. */
export const BrokerWalletSchema = z.object({
  brokerId: z.string(),
  pendingInr: z.number(),
  earnedInr: z.number(),
  readyToPayInr: z.number(),
  lifetimePaidInr: z.number(),
  currentMonthInr: z.number(),
  currentMonthAttributions: z.number(),
});
export type BrokerWallet = z.infer<typeof BrokerWalletSchema>;

/** A referral link the Care Partner shares. Mirrors ReferralLinkDto. */
export const ReferralLinkSchema = z.object({
  linkId: z.string(),
  shortCode: z.string(),
  targetUrl: z.string().nullable(),
  clickCount: z.number(),
  conversionCount: z.number(),
  isActive: z.boolean(),
  /** Not on ReferralLinkDto; carried for display when the create echoes it back. */
  campaignName: z.string().nullable(),
});
export type ReferralLink = z.infer<typeof ReferralLinkSchema>;

/** Generate a referral link. Mirrors CreateReferralLinkRequest. tenantId/doctorId
 *  are server-resolved in the portal context, so the form sends only a campaign. */
export const CreateReferralLinkRequestSchema = z.object({
  campaignName: z.string().nullable(),
});
export type CreateReferralLinkRequest = z.infer<typeof CreateReferralLinkRequestSchema>;

/** Book on behalf of a referred patient. Mirrors CreateBrokerBookingRequest. */
export const BrokerGenderSchema = z.enum(['male', 'female', 'other']);
export type BrokerGender = z.infer<typeof BrokerGenderSchema>;

export const BrokerPortalBookingRequestSchema = z.object({
  patientPhone: z.string(),
  patientName: z.string().nullable(),
  patientAge: z.number().nullable(),
  patientGender: BrokerGenderSchema.nullable(),
  slotId: z.string(),
  doctorId: z.string(),
  departmentId: z.string().nullable(),
  chiefComplaint: z.string().nullable(),
});
export type BrokerPortalBookingRequest = z.infer<typeof BrokerPortalBookingRequestSchema>;

/** Result of a broker-portal booking. Mirrors BrokerBookingResult. The status is
 *  'awaiting_patient_consent' — the patient must approve via WhatsApp OTP. */
export const BrokerBookingResultSchema = z.object({
  bookingId: z.string(),
  bookingNumber: z.string().nullable(),
  attributionId: z.string(),
  status: z.string(),
});
export type BrokerBookingResult = z.infer<typeof BrokerBookingResultSchema>;

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
  // Nullable: COALESCE(patient_name_at_booking, full_name) — both columns are nullable
  // (WhatsApp-first phone identity), so a nameless booking sends null. Adapter coalesces. (#53)
  patientDisplayName: z.string().nullable(),
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
  // Behalf / consent (Phase 1). ADDITIVE + tolerant: the tokens serialize as the
  // canonical snake_case strings; absent (older payloads) → adapter defaults to a
  // `self`/`not_required` booking. string|number kept for the int-enum regression
  // guard already applied to status/source/gender/language above.
  bookedByType: z.union([z.number(), z.string()]).nullable().optional(),
  behalfRelation: z.union([z.number(), z.string()]).nullable().optional(),
  patientConsentStatus: z.union([z.number(), z.string()]).nullable().optional(),
  // The booking's doctor — the reschedule slide-over lists this doctor's open slots.
  // Optional/nullable so older payloads (pre-Phase-1) still parse.
  doctorId: z.string().nullable().optional(),
});
export type BookingListItemDto = z.infer<typeof BookingListItemDtoSchema>;

/** `GET /api/v1/patients` row. Mirrors PatientListItemDto. PHI: maskedPhone only. */
export const PatientListItemDtoSchema = z.object({
  patientId: z.string(),
  // Nullable: docslot.patients.full_name is nullable (phone is the global identity);
  // a patient who never gave a name sends null. Adapter coalesces. (#53)
  fullName: z.string().nullable(),
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

/** `POST /api/v1/bookings/{bookingId}/reschedule` result. Mirrors
 *  RescheduleBookingResult — a reschedule supersedes the old booking with a NEW
 *  one (lineage), returning both ids + the new booking number + token. */
export const RescheduleResultDtoSchema = z.object({
  oldBookingId: z.string(),
  newBookingId: z.string(),
  newBookingNumber: z.string().nullable(),
  tokenNumber: z.number().nullable(),
});
export type RescheduleResultDto = z.infer<typeof RescheduleResultDtoSchema>;

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

// ─────────────────────────────────────────────────────────────────────────────
// IAM — Roles & permissions privilege matrix (Team & Roles, Slice 2).
// Mirrors the live .NET IAM API under /api/v1/iam (ModuleDto / PermissionDto /
// RoleMatrixDto + nested module/cell, DuplicateRole, EffectiveAccess). camelCase
// over the wire. The mock seam returns the same shapes so the flag-off app and
// the flag-on app render byte-for-byte the same grid.
// ─────────────────────────────────────────────────────────────────────────────

/** `GET /iam/modules` → a licensable resource group (a matrix section header). */
export const ModuleDtoSchema = z.object({
  resourceKey: z.string(),
  name: z.string(),
  description: z.string().nullable(),
  displayOrder: z.number(),
  licensed: z.boolean(),
});
export type ModuleDto = z.infer<typeof ModuleDtoSchema>;

/** `GET /iam/permissions?module=` → one permission in the registry. */
export const IamPermissionDtoSchema = z.object({
  permissionId: z.string(),
  permissionKey: z.string(),
  resource: z.string(),
  action: z.string(),
  scope: z.string(),
  isDangerous: z.boolean(),
  description: z.string().nullable(),
});
export type IamPermissionDto = z.infer<typeof IamPermissionDtoSchema>;

/** One action cell in a role's matrix (a checkbox in a module row). */
export const RoleMatrixCellSchema = z.object({
  permissionId: z.string(),
  permissionKey: z.string(),
  action: z.string(),
  actionName: z.string(),
  isDangerous: z.boolean(),
  granted: z.boolean(),
  moduleLicensed: z.boolean(),
});
export type RoleMatrixCell = z.infer<typeof RoleMatrixCellSchema>;

/** One module section of a role's matrix (grouped action cells + a granted tally). */
export const RoleMatrixModuleSchema = z.object({
  resourceKey: z.string(),
  name: z.string(),
  description: z.string().nullable(),
  displayOrder: z.number(),
  licensed: z.boolean(),
  grantedCount: z.number(),
  totalCount: z.number(),
  cells: z.array(RoleMatrixCellSchema),
});
export type RoleMatrixModule = z.infer<typeof RoleMatrixModuleSchema>;

/** `GET /iam/roles/{roleId}/matrix` — the heart of the Roles screen.
 *  `editable===false` (or `isSystem===true`) → the grid renders read-only and the
 *  panel surfaces a Duplicate CTA instead of live checkboxes. */
export const RoleMatrixSchema = z.object({
  roleId: z.string(),
  roleKey: z.string(),
  name: z.string(),
  description: z.string().nullable(),
  scope: z.string(),
  isSystem: z.boolean(),
  editable: z.boolean(),
  grantedCount: z.number(),
  totalCount: z.number(),
  modules: z.array(RoleMatrixModuleSchema),
});
export type RoleMatrix = z.infer<typeof RoleMatrixSchema>;

/** `POST|DELETE /iam/roles/{roleId}/permissions/{permissionId}` → the new cell state. */
export const RolePermissionToggleResultSchema = z.object({
  roleId: z.string(),
  permissionId: z.string(),
  granted: z.boolean(),
});
export type RolePermissionToggleResult = z.infer<typeof RolePermissionToggleResultSchema>;

/** `POST /iam/roles/duplicate` body. Clones a role + its grants into a new
 *  tenant-scoped (editable) role. `newRoleKey` is lower_snake_case. */
export const DuplicateRoleRequestSchema = z.object({
  sourceRoleId: z.string(),
  newRoleKey: z.string(),
  newName: z.string(),
  description: z.string().nullable().optional(),
  tenantId: z.string().nullable().optional(),
});
export type DuplicateRoleRequest = z.infer<typeof DuplicateRoleRequestSchema>;

/** `POST /iam/roles/duplicate` result — the new role's id (navigate to its matrix). */
export const DuplicateRoleResultSchema = z.object({ roleId: z.string() });
export type DuplicateRoleResult = z.infer<typeof DuplicateRoleResultSchema>;

/** `GET /iam/users/{userId}/effective-access?tenantId=` — the resolved permission
 *  key set for a user (role grants − denies + grants), tenant-scoped. */
export const EffectiveAccessSchema = z.object({
  userId: z.string(),
  tenantId: z.string(),
  permissionKeys: z.array(z.string()),
});
export type EffectiveAccess = z.infer<typeof EffectiveAccessSchema>;

// ── Catalog-plane creates (platform-governed, gated platform.permissions.manage) ──
// These define NEW catalog entries (a module / a permission). Distinct from the
// assignment plane (granting an existing permission to a role). A new permission
// is INERT until application code checks it — it becomes grantable in the matrix
// but enforces nothing on its own.

/** `POST /iam/modules` body. `resourceKey` is lower_snake (the matrix section key). */
export const CreateModuleRequestSchema = z.object({
  resourceKey: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/),
  name: z.string().trim().min(1),
  description: z.string().trim().nullable().optional(),
  displayOrder: z.number().int().optional(),
});
export type CreateModuleRequest = z.infer<typeof CreateModuleRequestSchema>;

/** `POST /iam/modules` result → the new resource type's id. */
export const CreateModuleResultSchema = z.object({ resourceTypeId: z.string() });
export type CreateModuleResult = z.infer<typeof CreateModuleResultSchema>;

/** `POST /iam/permissions` body. `permissionKey` is dotted lower_snake
 *  (e.g. "docslot.report.sign"); `resource` is the owning module's resourceKey;
 *  `action` is lower_snake; `scope` is one of platform|tenant|self. */
export const CreatePermissionRequestSchema = z.object({
  permissionKey: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$/),
  resource: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/),
  action: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/),
  scope: z.enum(['platform', 'tenant', 'self']),
  description: z.string().trim().min(1),
  isDangerous: z.boolean().optional(),
});
export type CreatePermissionRequest = z.infer<typeof CreatePermissionRequestSchema>;

/** `POST /iam/permissions` result → the new permission's id. */
export const CreatePermissionResultSchema = z.object({ permissionId: z.string() });
export type CreatePermissionResult = z.infer<typeof CreatePermissionResultSchema>;
