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
  completeBookingMock,
  getBookingMock,
  noShowBookingMock,
} from './mutations-mock';
import type { Analytics } from '@/lib/mock/contracts';

// AUTH
export const login = USE_REAL_API ? real.login : mock.login;
export const refresh = USE_REAL_API ? real.refresh : mock.refresh;
export const logout = USE_REAL_API ? real.logout : mock.logout;
export const getMe = USE_REAL_API ? real.getMe : mock.getMe;

// PERMISSIONS
export const getPermissions = USE_REAL_API ? real.getPermissions : mock.getPermissions;

// MENUS + BADGES (backend-driven nav)
export const getMenus = USE_REAL_API ? real.getMenus : mock.getMenus;
export const getBadges = USE_REAL_API ? real.getBadges : mock.getBadges;

// READ LISTS
export const listBookings = USE_REAL_API ? real.listBookings : mock.listBookings;
export const listDoctorCards = USE_REAL_API ? real.listDoctorCards : mock.listDoctorCards;
export const getDashboardSummary = USE_REAL_API ? real.getDashboardSummary : mock.getDashboardSummary;

// BOOKING DETAIL — real fetches GET /bookings/{id} and adapts it to the panel's
// Booking shape; mock resolves the prototype BOOKINGS.find. Either way the
// manage/approve slide-overs open for a REAL booking id.
export const getBooking = USE_REAL_API ? real.getBooking : getBookingMock;

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
export const createBooking = USE_REAL_API ? real.createBooking : mock.createBooking;

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

// ── CALENDAR (/calendar) — week capacity heatmap ──────────────────────────────
// Real builds the grid from GET /doctors + GET /doctors/{id}/slots?date= (rolled
// up per day×time row). Mock serves the prototype deterministic grid unchanged.
export const getCalendarGrid = USE_REAL_API ? real.getCalendarGrid : mock.getCalendarGrid;

// ── DEVELOPERS / API PLATFORM (Slice 02) — READ LISTS only ────────────────────
// Live: GET /api-clients, /api-scopes, /webhooks/event-types, and /webhooks
// (fanned out per client — no list-all endpoint). PLATFORM-ADMIN gated; a
// tenant_owner gets 403 + no nav entry, so these are only reachable by the
// platform-admin login in live mode. Mock side reuses the existing lib/mock fns
// unchanged. DANGEROUS WRITES (register/rotate/createWebhook/status/scopes/
// rate-limits) are NOT wired — they stay on mock/best-effort regardless of flag.
export const listApiClients = USE_REAL_API ? real.listApiClients : mock.listApiClients;
export const listScopes = USE_REAL_API ? real.listScopes : mock.listScopes;
export const listEventTypes = USE_REAL_API ? real.listEventTypes : mock.listEventTypes;
export const listWebhooks = USE_REAL_API ? real.listWebhooks : mock.listWebhooks;

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

export { USE_REAL_API } from './flag';
export { toUserError } from './real';
