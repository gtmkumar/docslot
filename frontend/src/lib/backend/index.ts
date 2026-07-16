// Backend seam facade. Re-exports the data functions that have a LIVE .NET
// implementation, choosing real-vs-mock by the VITE_USE_REAL_API flag. Feature
// `api.ts` files import these names from '@/lib/backend' instead of '@/lib/mock';
// everything not wired to the live API yet keeps importing from '@/lib/mock'
// directly (those stay on the mock seam regardless of the flag).
//
// Default (flag off) → every export below is the mock fn, so the mock app is
// byte-for-byte unchanged.

import { USE_REAL_API } from './flag';
import * as mock from '@/lib/mock';
import * as real from './real';
import { listPatientsMock } from './patients-mock';
import {
  addDoctorMock,
  addPatientMock,
  approveRuleMock,
  checkInBookingMock,
  completeBookingMock,
  getBookingMock,
  listTenantsMock,
  noShowBookingMock,
  rescheduleBookingMock,
} from './mutations-mock';
import type { Analytics } from '@/lib/mock/contracts';

// AUTH
export const login = USE_REAL_API ? real.login : mock.login;
export const refresh = USE_REAL_API ? real.refresh : mock.refresh;
export const logout = USE_REAL_API ? real.logout : mock.logout;
export const getMe = USE_REAL_API ? real.getMe : mock.getMe;

// PERMISSIONS
export const getPermissions = USE_REAL_API ? real.getPermissions : mock.getPermissions;

// TENANTS — begin-impersonation target picker. Live: GET /tenants (super_admin,
// `platform.tenants.read`). Mock: a small seed list so the selector is usable.
export const listTenants = USE_REAL_API ? real.listTenants : listTenantsMock;

// MENUS + BADGES (backend-driven nav)
export const getMenus = USE_REAL_API ? real.getMenus : mock.getMenus;
export const getBadges = USE_REAL_API ? real.getBadges : mock.getBadges;

// READ LISTS
export const listBookings = USE_REAL_API ? real.listBookings : mock.listBookings;
export const listDoctorCards = USE_REAL_API ? real.listDoctorCards : mock.listDoctorCards;
export const getDashboardSummary = USE_REAL_API ? real.getDashboardSummary : mock.getDashboardSummary;

// DASHBOARD SIDE PANELS — live: GET /dashboard/agent-panel | /department-load |
// /floor (tenant-scoped aggregates, docslot.booking.read). Mock keeps serving the
// prototype fixtures when the flag is off.
export const getAgentPanel = USE_REAL_API ? real.getAgentPanel : mock.getAgentPanel;
export const getDepartmentLoad = USE_REAL_API ? real.getDepartmentLoad : mock.getDepartmentLoad;
export const getFloorDoctors = USE_REAL_API ? real.getFloorDoctors : mock.getFloorDoctors;

// BOOKING DETAIL — real fetches GET /bookings/{id} and adapts it to the panel's
// Booking shape; mock resolves the prototype BOOKINGS.find. Either way the
// manage/approve slide-overs open for a REAL booking id.
export const getBooking = USE_REAL_API ? real.getBooking : getBookingMock;

// CONVERSATION THREAD — real hits GET /bookings/{id}/conversation (the live read
// endpoint) and adapts ConversationMessageDto[] into the app-facing ChatMessage[]
// the WhatsApp-mirrored thread renders; mock serves the prototype thread.
export const getConversation = USE_REAL_API ? real.getConversation : mock.getConversation;

// NEW-BOOKING WIZARD — real hits GET /doctors (filtered by department) and
// GET /doctors/{id}/slots?date= (available only, carrying slotId); mock serves the
// prototype practitioners/slots. The wizard's collected fields map onto
// POST /bookings (createBooking, above) so a live create succeeds.
export const listPractitioners = USE_REAL_API ? real.listPractitioners : mock.listPractitioners;
export const listSlots = USE_REAL_API ? real.listSlots : mock.listSlots;

// PATIENTS LIST — the mock side derives a PatientRow[] from lib/data PATIENTS
// (the screen previously read PATIENTS directly); the real side hits /patients.
export const listPatients = USE_REAL_API ? real.listPatients : listPatientsMock;

// ANALYTICS — real hits /analytics?period=; the mock ignores the period (its
// aggregates are static). Uniform (period) signature so the feature hook is mode-
// agnostic.
export const getAnalytics: (period?: 'month' | 'quarter' | 'year') => Promise<Analytics> = USE_REAL_API
  ? real.getAnalytics
  : () => mock.getAnalytics();

// BOOKING MUTATIONS — real POSTs to /bookings/{id}/<action> with an
// Idempotency-Key header; mock returns the de-duped result shape. approve/cancel/
// create reuse the existing lib/mock fns; complete/no-show use the new mock stubs.
export const approveBooking = USE_REAL_API ? real.approveBooking : mock.approveBooking;
export const cancelBooking = USE_REAL_API ? real.cancelBooking : mock.cancelBooking;
export const completeBooking = USE_REAL_API ? real.completeBooking : completeBookingMock;
export const noShowBooking = USE_REAL_API ? real.noShowBooking : noShowBookingMock;
// CHECK-IN (confirmed → checked_in) — real POSTs to /bookings/{id}/check-in with
// an Idempotency-Key; mock returns the de-duped result shape.
export const checkInBooking = USE_REAL_API ? real.checkInBooking : checkInBookingMock;
export const createBooking = USE_REAL_API ? real.createBooking : mock.createBooking;
// RESCHEDULE — real POSTs to /bookings/{id}/reschedule (Idempotency-Key) with the
// chosen new slot (+ optional doctor/reason); mock returns the same result shape.
export const rescheduleBooking = USE_REAL_API ? real.rescheduleBooking : rescheduleBookingMock;

// ADD PATIENT — real POSTs to /patients; the mock is a no-op returning a synthetic
// id (the prior mock panel just toasted + closed). Idempotency-Key on the POST.
export const addPatient = USE_REAL_API ? real.addPatient : addPatientMock;

// ADD DOCTOR — real POSTs to /doctors (CreateDoctorRequest, 201); the mock is a
// no-op returning a synthetic id (the prior mock panel just toasted + closed).
// Idempotency-Key on the POST.
export const addDoctor = USE_REAL_API ? real.addDoctor : addDoctorMock;

// ── COMMISSION / CARE PARTNERS (Slice 07) ─────────────────────────────────────
// READS are thin pass-throughs (the zod contracts mirror the DTOs 1:1). WRITES
// carry an Idempotency-Key; APPROVE and EXECUTE stay distinct. Mock side reuses
// the existing lib/mock/commission fns unchanged.
export const listBrokers = USE_REAL_API ? real.listBrokers : mock.listBrokers;
export const listAttributions = USE_REAL_API ? real.listAttributions : mock.listAttributions;
export const listCommissionRules = USE_REAL_API ? real.listCommissionRules : mock.listCommissionRules;
export const listPayouts = USE_REAL_API ? real.listPayouts : mock.listPayouts;
export const listDisputes = USE_REAL_API ? real.listDisputes : mock.listDisputes;

export const registerBroker = USE_REAL_API ? real.registerBroker : mock.registerBroker;
export const setBrokerStatus = USE_REAL_API ? real.setBrokerStatus : mock.setBrokerStatus;
export const blacklistBroker = USE_REAL_API ? real.blacklistBroker : mock.blacklistBroker;
export const createCommissionRule = USE_REAL_API ? real.createCommissionRule : mock.createCommissionRule;
export const approvePayout = USE_REAL_API ? real.approvePayout : mock.approvePayout;
export const executePayout = USE_REAL_API ? real.executePayout : mock.executePayout;
export const raiseDispute = USE_REAL_API ? real.raiseDispute : mock.raiseDispute;
export const resolveDispute = USE_REAL_API ? real.resolveDispute : mock.resolveDispute;
// approveRule has no mock equivalent yet; live-only (mock falls back to a no-op
// resolving the rule id, matching the other 204 mock shapes) — wired if the UI
// exposes it.
export const approveRule = USE_REAL_API ? real.approveRule : approveRuleMock;

// Campaigns (admin, commission.campaign.manage). List + create; create returns an
// { id } (the list refetches after invalidation). Mock parity in lib/mock/commission.
export const listCampaigns = USE_REAL_API ? real.listCampaigns : mock.listCampaigns;
export const createCampaign = USE_REAL_API ? real.createCampaign : mock.createCampaign;

// Form 16A (TDS 194H, commission.tds.issue). Issue returns the cert DTO (PAN last-4
// only); getForm16ADocumentUrl returns the same-origin doc path (full PAN, text/html)
// that the UI opens in a new tab WITH auth headers — never logged/cached/in state.
export const issueForm16A = USE_REAL_API ? real.issueForm16A : mock.issueForm16A;
export const getForm16ADocumentUrl = USE_REAL_API ? real.getForm16ADocumentUrl : mock.getForm16ADocumentUrl;
export const openForm16ADocument = USE_REAL_API ? real.openForm16ADocument : mock.openForm16ADocument;

// ── BROKER SELF-SERVICE PORTAL (/commission/me) ───────────────────────────────
// The Care Partner's OWN data; the server resolves broker_id from the JWT (no id
// in any path). Reads are pass-throughs; book-on-behalf carries an Idempotency-Key
// and its result status is 'awaiting_patient_consent' (patient WhatsApp OTP, DPDP).
export const getBrokerWallet = USE_REAL_API ? real.getBrokerWallet : mock.getBrokerWallet;
export const listReferralLinks = USE_REAL_API ? real.listReferralLinks : mock.listReferralLinks;
export const createReferralLink = USE_REAL_API ? real.createReferralLink : mock.createReferralLink;
export const createPortalBooking = USE_REAL_API ? real.createPortalBooking : mock.createPortalBooking;

// ── CALENDAR (/calendar) — week capacity heatmap ──────────────────────────────
// Real builds the grid from GET /doctors + GET /doctors/{id}/slots?date= (rolled
// up per day×time row). Mock serves the prototype deterministic grid unchanged.
export const getCalendarGrid = USE_REAL_API ? real.getCalendarGrid : mock.getCalendarGrid;

// ── DEVELOPERS / API PLATFORM (Slice 02 + 12) ─────────────────────────────────
// Live: GET /api-clients, /api-scopes, /webhooks/event-types, and /webhooks
// (fanned out per client — no list-all endpoint). PLATFORM-ADMIN gated; a
// tenant_owner gets 403 + no nav entry, so these are only reachable by the
// platform-admin login in live mode. Mock side reuses the existing lib/mock fns
// unchanged. WRITES (register/rotate/status/rate-limits/scopes + createWebhook/
// updateWebhook/retryDelivery) carry the caller's Idempotency-Key; the plaintext
// client secret / webhook signing secret is returned ONCE on register/rotate/
// createWebhook and never cached. createWebhook OMITS tenantId from the body —
// the server binds the tenant from the JWT. Forensic reads (deliveries, request
// logs) are metadata-only (no payload/secret/PHI).
export const listApiClients = USE_REAL_API ? real.listApiClients : mock.listApiClients;
export const listScopes = USE_REAL_API ? real.listScopes : mock.listScopes;
export const listEventTypes = USE_REAL_API ? real.listEventTypes : mock.listEventTypes;
export const listWebhooks = USE_REAL_API ? real.listWebhooks : mock.listWebhooks;
export const listWebhookDeliveries = USE_REAL_API ? real.listWebhookDeliveries : mock.listWebhookDeliveries;
export const listApiRequestLogs = USE_REAL_API ? real.listApiRequestLogs : mock.listApiRequestLogs;
export const registerApiClient = USE_REAL_API ? real.registerApiClient : mock.registerApiClient;
export const rotateClientSecret = USE_REAL_API ? real.rotateClientSecret : mock.rotateClientSecret;
export const setClientStatus = USE_REAL_API ? real.setClientStatus : mock.setClientStatus;
export const setClientRateLimits = USE_REAL_API ? real.setClientRateLimits : mock.setClientRateLimits;
export const setClientScopes = USE_REAL_API ? real.setClientScopes : mock.setClientScopes;
export const createWebhook = USE_REAL_API ? real.createWebhook : mock.createWebhook;
export const updateWebhook = USE_REAL_API ? real.updateWebhook : mock.updateWebhook;
export const retryWebhookDelivery = USE_REAL_API ? real.retryWebhookDelivery : mock.retryWebhookDelivery;

// ── SECURITY & COMPLIANCE (Slice 05) — READ LISTS only ────────────────────────
// Live: GET /security/{audit-chain/verify, audit-chain/anchors, dpdp/requests,
// breaches, review-queue, keys}. PLATFORM-ADMIN gated (distinct platform.* perms).
// Several lists are legitimately empty → the tab renders its empty state. NO PHI /
// NO key material. DANGEROUS WRITES (break-glass/breach report/DPDP export+erase/
// deletion-cert gen/anchor) are NOT wired — they stay on mock/best-effort.
export const verifyAuditChain = USE_REAL_API ? real.verifyAuditChain : mock.verifyAuditChain;
export const listAnchors = USE_REAL_API ? real.listAnchors : mock.listAnchors;
export const listDpdpRequests = USE_REAL_API ? real.listDpdpRequests : mock.listDpdpRequests;
export const listBreaches = USE_REAL_API ? real.listBreaches : mock.listBreaches;
export const listReviewQueue = USE_REAL_API ? real.listReviewQueue : mock.listReviewQueue;
export const listKeyStatus = USE_REAL_API ? real.listKeyStatus : mock.listKeyStatus;

// ── AUDIT LOG (#86) + ACTIVE SESSIONS (#87) — Team console surfaces ────────────
// Live: GET /security/audit/logs (+ /export → text/csv), GET /security/sessions,
// POST /security/sessions/{id}/revoke + /users/{id}/revoke-all. Audit gated
// tenant.audit.read; sessions gated tenant.users.update. NO PHI (staff identities;
// raw ip only). The CSV export returns {fileName, content} for the caller to
// trigger a download — never cached. Revokes carry an Idempotency-Key.
export const listAuditLog = USE_REAL_API ? real.listAuditLog : mock.listAuditLog;
export const exportAuditLog = USE_REAL_API ? real.exportAuditLog : mock.exportAuditLog;
export const listActiveSessions = USE_REAL_API ? real.listActiveSessions : mock.listActiveSessions;
export const revokeSession = USE_REAL_API ? real.revokeSession : mock.revokeSession;
export const revokeAllSessions = USE_REAL_API ? real.revokeAllSessions : mock.revokeAllSessions;

// ── SECURITY POLICY (#91) — Team console "Security" tab ────────────────────────
// Live: GET/PUT /security/policy (gated tenant.settings.read / .update) +
// GET/POST/DELETE /security/ip-allowlist (gated platform.ip_allowlist.manage). The
// policy lives in tenants.settings->'security' JSONB; every field is really enforced
// at login / password-set / patient-read. Writes carry an Idempotency-Key. Mock side
// seeds a configured tenant so the warning + editor states render flag-off. NO PHI.
export const getSecurityPolicy = USE_REAL_API ? real.getSecurityPolicy : mock.getSecurityPolicy;
export const updateSecurityPolicy = USE_REAL_API ? real.updateSecurityPolicy : mock.updateSecurityPolicy;
export const listIpAllowlist = USE_REAL_API ? real.listIpAllowlist : mock.listIpAllowlist;
export const addIpAllowlist = USE_REAL_API ? real.addIpAllowlist : mock.addIpAllowlist;
export const removeIpAllowlist = USE_REAL_API ? real.removeIpAllowlist : mock.removeIpAllowlist;

// ── WORKSPACE SETTINGS (Settings screen — Phase 1) — /settings ─────────────────
// Live: GET/PATCH /settings (gated tenant.settings.read / .update; facility row bound
// from the JWT tenant). GET 404s when the tenant has no facility row → the screen shows
// a distinct "not set up" state. PATCH replaces each supplied section wholesale and
// carries NO Idempotency-Key (configuration write). Mock side seeds a configured tenant
// so the demo exercises every section + state flag-off. NO PHI.
export const getSettings = USE_REAL_API ? real.getSettings : mock.getSettings;
export const updateSettings = USE_REAL_API ? real.updateSettings : mock.updateSettings;

// TEAM — token-based Invitations (#89)
export const listInvitations = USE_REAL_API ? real.listInvitations : mock.listInvitations;
export const createInvitation = USE_REAL_API ? real.createInvitation : mock.createInvitation;
export const resendInvitation = USE_REAL_API ? real.resendInvitation : mock.resendInvitation;
export const revokeInvitation = USE_REAL_API ? real.revokeInvitation : mock.revokeInvitation;

// ── IAM / ROLES & PERMISSIONS (Slice 2) ───────────────────────────────────────
// Privilege-matrix grid + duplicate + effective-access viewer. READS pass through
// (zod mirrors the IAM DTOs 1:1); WRITES (cell toggle, duplicate) carry an
// Idempotency-Key. Mock side derives the matrix from the existing RBAC seed so
// flag-off renders byte-for-byte. The DB re-checks editability for built-in roles.
// Roles/Users tabs + overrides via existing live endpoints (GET /roles,
// GET /tenants/{id}/users, POST /permission-overrides). Mock side keeps the
// existing RBAC seed so flag-off is unchanged.
export const listRoles = USE_REAL_API ? real.listRoles : mock.listRoles;
export const listTenantUsers = USE_REAL_API ? real.listTenantUsers : mock.listTenantUsers;
// Role assignment writes — live POST /role-assignments (+ /revoke). Assigning or revoking
// a role updates the user's row (the users query invalidates on success).
export const assignRole = USE_REAL_API ? real.assignRole : mock.assignRole;
export const revokeRoleAssignment = USE_REAL_API ? real.revokeRoleAssignment : mock.revokeRoleAssignment;
export const setOverride = USE_REAL_API ? real.setOverride : mock.setOverride;

// ── USER MANAGEMENT (lifecycle) ───────────────────────────────────────────────
// createUser is the invite write — live POST /tenants/{id}/users (escalation-safe;
// server seeds a temp credential + must-change-password). setUserActive (deactivate/
// reactivate), updateUser (edit profile), resetUserAccess (force change + unlock) hit
// the new gated lifecycle endpoints. All carry an Idempotency-Key; the actor is bound
// server-side. Mock side returns the synthetic result shape so flag-off stays functional.
export const createUser = USE_REAL_API ? real.createUser : mock.createUser;
export const setUserActive = USE_REAL_API ? real.setUserActive : mock.setUserActive;
export const updateUser = USE_REAL_API ? real.updateUser : mock.updateUser;
export const resetUserAccess = USE_REAL_API ? real.resetUserAccess : mock.resetUserAccess;

// ── EXPORT + BULK IMPORT (#95) — People-tab toolbar ───────────────────────────
// Live: GET /tenants/{id}/users/export (text/csv, gated tenant.users.read; tenant
// bound from the JWT) + POST /tenants/{id}/users/bulk-import (gated tenant.users.create;
// per-row atomic; role via the R3 no-escalation guard; batch cap 500 → 422). The export
// returns {fileName, content} for the caller to download — never cached. The import
// carries an Idempotency-Key. Mock side builds the CSV from + provisions into the RBAC
// seed so flag-off renders + refreshes.
export const exportTenantUsers = USE_REAL_API ? real.exportTenantUsers : mock.exportTenantUsers;
export const bulkImportUsers = USE_REAL_API ? real.bulkImportUsers : mock.bulkImportUsers;

// ── BRANCH / MEMBERSHIP SCOPE (#90) ───────────────────────────────────────────
// listBranches (GET /tenants/{id}/branches, gated tenant.users.read) heads the
// People "All branches" filter + the "N branches" stat + the manage-panel picker.
// setMemberScope (PUT /tenants/{id}/users/{userId}/scope, gated tenant.users.update)
// writes DISPLAY-only branch/department via platform.set_membership_scope — it can
// never change effective access. Idempotency-Key on the write. Mock persists the
// change in-memory so flag-off is functional + the optimistic UI reconciles.
export const listBranches = USE_REAL_API ? real.listBranches : mock.listBranches;
export const setMemberScope = USE_REAL_API ? real.setMemberScope : mock.setMemberScope;

export const listModules = USE_REAL_API ? real.listModules : mock.listModules;
export const listIamPermissions = USE_REAL_API ? real.listIamPermissions : mock.listIamPermissions;
// CATALOG-PLANE CREATES (platform.permissions.manage) — real POSTs to /iam/modules
// + /iam/permissions (Idempotency-Key); mock mutates the in-memory RBAC catalog so
// flag-off reflects the new module/permission (a new permission appears as a matrix
// cell under its module).
export const createModule = USE_REAL_API ? real.createModule : mock.createModule;
export const createPermission = USE_REAL_API ? real.createPermission : mock.createPermission;
export const getRoleMatrix = USE_REAL_API ? real.getRoleMatrix : mock.getRoleMatrix;
export const grantRolePermission = USE_REAL_API ? real.grantRolePermission : mock.grantRolePermission;
export const revokeRolePermission = USE_REAL_API ? real.revokeRolePermission : mock.revokeRolePermission;
export const duplicateRole = USE_REAL_API ? real.duplicateRole : mock.duplicateRole;
export const getEffectiveAccess = USE_REAL_API ? real.getEffectiveAccess : mock.getEffectiveAccess;

// Team & Roles seam (the last mock-only IAM fns, now live). createRole creates an
// EMPTY role (the create DTO has no permissionKeys) then attaches the picked
// permissions via the per-grant GUARDED grant endpoint — never a bulk grant.
// getPermissionRegistry/getRolePermissions are DERIVED from the wired permission
// catalog / role matrix (no new endpoint). listUserOverrides + getEffectivePermissions
// hit the new GET /iam/users/{id}/{overrides,effective-permissions} endpoints
// (overrides gated on platform.overrides.read, SoD-distinct from .grant). Mock side
// keeps the existing RBAC seed so flag-off renders byte-for-byte the same.
export const createRole = USE_REAL_API ? real.createRole : mock.createRole;
export const getPermissionRegistry = USE_REAL_API ? real.getPermissionRegistry : mock.getPermissionRegistry;
export const getRolePermissions = USE_REAL_API ? real.getRolePermissions : mock.getRolePermissions;
export const listUserOverrides = USE_REAL_API ? real.listUserOverrides : mock.listUserOverrides;
// #85 — tenant-wide overrides list (GET /iam/overrides, gated platform.overrides.read).
export const listTenantOverrides = USE_REAL_API ? real.listTenantOverrides : mock.listTenantOverrides;
export const getEffectivePermissions = USE_REAL_API ? real.getEffectivePermissions : mock.getEffectivePermissions;

// ── CLINICAL PHI + ABDM + CONSENT (Phase-3 slice 4) ───────────────────────────
// The most PHI-sensitive surface, now on the live seam. Clinical READS send the
// declared X-Purpose-Of-Use header (the feature query stays DISABLED until the
// purpose gate declares one); the CONSENT read is the only clinical GET without a
// purpose. WRITES carry an Idempotency-Key. A consent-denied read 403s; the UI
// posts /security/break-glass then re-fetches. Mock side is byte-for-byte parity.
export const getPatientConsent = USE_REAL_API ? real.getPatientConsent : mock.getPatientConsent;
export const listPrescriptions = USE_REAL_API ? real.listPrescriptions : mock.listPrescriptions;
export const getPrescription = USE_REAL_API ? real.getPrescription : mock.getPrescription;
export const issuePrescription = USE_REAL_API ? real.issuePrescription : mock.issuePrescription;
// CONSULTATION COMPOSER (Phase A) — get-or-create draft (POST, purpose + Idempotency-Key),
// autosave (PATCH → 204, no PHI echoed), finalize (POST, Idempotency-Key; finalized:false
// means blocked by unoverridden high/critical drug alerts). Mock keeps stateful drafts
// keyed by bookingId so the full flow works flag-off.
export const getOrCreateConsultation = USE_REAL_API ? real.getOrCreateConsultation : mock.getOrCreateConsultation;
export const saveConsultation = USE_REAL_API ? real.saveConsultation : mock.saveConsultation;
export const finalizeConsultation = USE_REAL_API ? real.finalizeConsultation : mock.finalizeConsultation;
export const listLabReports = USE_REAL_API ? real.listLabReports : mock.listLabReports;
export const getLabReport = USE_REAL_API ? real.getLabReport : mock.getLabReport;
export const uploadLabReport = USE_REAL_API ? real.uploadLabReport : mock.uploadLabReport;
export const deliverLabReport = USE_REAL_API ? real.deliverLabReport : mock.deliverLabReport;
export const listMedicalHistory = USE_REAL_API ? real.listMedicalHistory : mock.listMedicalHistory;
// NEW (this slice) — medical-history create/update + the contextual break-glass.
export const createMedicalHistory = USE_REAL_API ? real.createMedicalHistory : mock.createMedicalHistory;
export const updateMedicalHistory = USE_REAL_API ? real.updateMedicalHistory : mock.updateMedicalHistory;
// Paper-prescription intake — import a batch of UNVERIFIED external records + an
// optional scan, verify an external record, and fetch a record's attachment image.
export const importMedicalHistory = USE_REAL_API ? real.importMedicalHistory : mock.importMedicalHistory;
export const verifyMedicalHistory = USE_REAL_API ? real.verifyMedicalHistory : mock.verifyMedicalHistory;
export const fetchMedicalHistoryAttachment = USE_REAL_API ? real.fetchMedicalHistoryAttachment : mock.fetchMedicalHistoryAttachment;
// Unified patient timeline (prescriptions + reports + history in one purpose-gated read).
export const getPatientTimeline = USE_REAL_API ? real.getPatientTimeline : mock.getPatientTimeline;
// OCR assist: extract a paper prescription into review-first suggested lines (advisory).
export const extractPrescription = USE_REAL_API ? real.extractPrescription : mock.extractPrescription;
export const listAbdmRecords = USE_REAL_API ? real.listAbdmRecords : mock.listAbdmRecords;
export const getAbdmRecord = USE_REAL_API ? real.getAbdmRecord : mock.getAbdmRecord;
export const pushAbdmRecord = USE_REAL_API ? real.pushAbdmRecord : mock.pushAbdmRecord;
export const breakGlass = USE_REAL_API ? real.breakGlass : mock.breakGlass;

// ── AI ASSIST (no-show risk + triage) ─────────────────────────────────────────
// Two already-shipped backend capabilities surfaced read-only at the reception
// desk. getNoShowRisk: GET /bookings/{id}/no-show-risk (docslot.booking.read, NO
// PHI). submitTriage: POST /triage (docslot.booking.create); the X-Purpose-Of-Use
// header is forwarded only when the call is patient/booking-bound (server 422
// gate). Mock side is deterministic + clearly labelled (source 'mock-ui') so the
// app works flag-off without ever implying a real model ran.
export const getNoShowRisk = USE_REAL_API ? real.getNoShowRisk : mock.getNoShowRisk;
export const submitTriage = USE_REAL_API ? real.submitTriage : mock.submitTriage;

// ── AI DOCUMENT SURFACES (OCR extract + RAG ask + ops reads) — Slice 11 + 14 ──
// PHI POSTs: extractLabReport (PERSISTED → Idempotency-Key) + askPatientRag
// (advisory → none); both forward X-Purpose-Of-Use (patient-bound). Their results
// (analyte values / RAG answer) AND the RAG question are PHI → consumed via
// mutations, never cached in a query key. The ops reads (extractions list + RAG
// status) are non-PHI summaries → cacheable queries. Mock side is deterministic +
// clearly labelled (source 'mock-ui') so flag-off works without implying a real
// model ran.
export const extractLabReport = USE_REAL_API ? real.extractLabReport : mock.extractLabReport;
export const askPatientRag = USE_REAL_API ? real.askPatientRag : mock.askPatientRag;
export const listAiExtractions = USE_REAL_API ? real.listAiExtractions : mock.listAiExtractions;
export const getRagStatus = USE_REAL_API ? real.getRagStatus : mock.getRagStatus;

export { USE_REAL_API } from './flag';
export { toUserError } from './real';
