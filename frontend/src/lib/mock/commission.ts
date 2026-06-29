// Mock adapter for the commission / Care Partner console (Slice 07). Shapes
// mirror CommissionDtos + the commission.* SQL enums, so the mock→real swap is a
// no-op for zod.
//
// LEGAL/SAFETY invariants baked in:
//  - "Care Partner" is the customer-facing label (carePartnerLabel field). Code
//    here may say broker (matching the DTO); UI strings never do.
//  - NO full PAN is ever returned — the register result echoes none, and BrokerDto
//    carries none. Care Partner phone is MASKED for display (maskPhone).
//  - Attribution patient identity = first name + MASKED phone only (DPDP).
//  - PCPNDT: every rule's excludesPndt is true (CHECK-forced).
//  - Money: approve and execute are distinct calls; the mock advances status
//    pending→approved (approve) and approved→processing (execute), so the UI's
//    two-step gating is honoured end to end.

import { maskPhone } from '@/lib/format';
import {
  AttributionSchema,
  BrokerBookingResultSchema,
  BrokerSchema,
  BrokerWalletSchema,
  CampaignSchema,
  CommissionCreatedSchema,
  CommissionRuleSchema,
  DisputeSchema,
  Form16ACertificateSchema,
  PayoutActionResultSchema,
  PayoutSchema,
  ReferralLinkSchema,
  RegisterBrokerResultSchema,
  type Attribution,
  type Broker,
  type BrokerBookingResult,
  type BrokerPortalBookingRequest,
  type BrokerWallet,
  type Campaign,
  type CommissionCreated,
  type CommissionRule,
  type CreateCampaignRequest,
  type CreateCommissionRuleRequest,
  type CreateReferralLinkRequest,
  type Dispute,
  type Form16ACertificate,
  type Payout,
  type PayoutActionResult,
  type RaiseDisputeRequest,
  type ReferralLink,
  type RegisterBrokerRequest,
  type RegisterBrokerResult,
  type ResolveDisputeRequest,
} from './contracts';

const LATENCY = 210;
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

const iso = (daysAgo: number) => new Date(Date.now() - daysAgo * 86_400_000).toISOString();
const CARE_PARTNER = 'Care Partner';

// ── Care Partners (seed) ─────────────────────────────────────────────────────
const BROKERS: Broker[] = [
  { brokerId: 'cp-1', maskedPhone: maskPhone('+91 98200 11223'), fullName: 'Ravi Deshmukh', email: 'ravi@medrep.in', brokerType: 'medical_rep', tierLevel: 'gold', panVerified: true, gstVerified: false, isActive: true, isBlacklisted: false, carePartnerLabel: CARE_PARTNER },
  { brokerId: 'cp-2', maskedPhone: maskPhone('+91 99203 44556'), fullName: 'Sunita Corporate Wellness', email: 'hr@acme.in', brokerType: 'corporate_hr', tierLevel: 'platinum', panVerified: true, gstVerified: true, isActive: true, isBlacklisted: false, carePartnerLabel: CARE_PARTNER },
  { brokerId: 'cp-3', maskedPhone: maskPhone('+91 98765 77889'), fullName: 'Imran Panel Coordinator', email: null, brokerType: 'insurance_panel', tierLevel: 'silver', panVerified: false, gstVerified: false, isActive: true, isBlacklisted: false, carePartnerLabel: CARE_PARTNER },
  { brokerId: 'cp-4', maskedPhone: maskPhone('+91 90008 22114'), fullName: 'Local Navigator (Pune)', email: null, brokerType: 'aggregator_agent', tierLevel: 'basic', panVerified: false, gstVerified: false, isActive: false, isBlacklisted: true, carePartnerLabel: CARE_PARTNER },
];

export function listBrokers(): Promise<Broker[]> {
  return delay(BROKERS.map((b) => BrokerSchema.parse(b)));
}

export function registerBroker(req: RegisterBrokerRequest, idempotencyKey: string): Promise<RegisterBrokerResult> {
  return withIdem(idempotencyKey, () => {
    void req; // PAN (if supplied) is captured server-side; NEVER echoed back.
    return RegisterBrokerResultSchema.parse({ brokerId: crypto.randomUUID(), alreadyExisted: false });
  });
}

export function setBrokerStatus(
  brokerId: string,
  _input: { isActive: boolean; reason?: string },
  idempotencyKey: string,
): Promise<CommissionCreated> {
  return withIdem(idempotencyKey, () => CommissionCreatedSchema.parse({ id: brokerId }));
}

export function blacklistBroker(brokerId: string, _reason: string, idempotencyKey: string): Promise<CommissionCreated> {
  return withIdem(idempotencyKey, () => CommissionCreatedSchema.parse({ id: brokerId }));
}

// ── Commission rules (seed; excludesPndt always true) ────────────────────────
const RULES: CommissionRule[] = [
  { ruleId: 'r-1', ruleName: 'Standard consult — flat', ruleKey: 'std_consult_flat', calcType: 'flat', flatAmountInr: 150, percentage: null, minCommissionInr: 100, maxCommissionInr: 500, maxMonthlyPerBrokerInr: 50000, priority: 10, firstBookingOnly: false, isActive: true, excludesPndt: true },
  { ruleId: 'r-2', ruleName: 'Premium — 8% capped', ruleKey: 'premium_pct', calcType: 'percentage', flatAmountInr: null, percentage: 8, minCommissionInr: 150, maxCommissionInr: 1200, maxMonthlyPerBrokerInr: 80000, priority: 20, firstBookingOnly: true, isActive: true, excludesPndt: true },
  { ruleId: 'r-3', ruleName: 'Corporate package — tiered', ruleKey: 'corp_tiered', calcType: 'tiered_table', flatAmountInr: null, percentage: null, minCommissionInr: null, maxCommissionInr: null, maxMonthlyPerBrokerInr: 200000, priority: 5, firstBookingOnly: false, isActive: false, excludesPndt: true },
];

export function listCommissionRules(): Promise<CommissionRule[]> {
  return delay(RULES.map((r) => CommissionRuleSchema.parse(r)));
}

export function createCommissionRule(req: CreateCommissionRuleRequest, idempotencyKey: string): Promise<CommissionRule> {
  return withIdem(idempotencyKey, () =>
    CommissionRuleSchema.parse({
      ruleId: crypto.randomUUID(),
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
      excludesPndt: true, // PCPNDT — always enforced.
    }),
  );
}

// ── Attribution ledger (seed; patient = first name + masked phone) ───────────
const ATTRIBUTIONS: Attribution[] = [
  { attributionId: 'at-1', bookingRef: 'BKG-2026-06-02841', brokerId: 'cp-1', brokerName: 'Ravi Deshmukh', patientFirstName: 'Riya', patientMaskedPhone: maskPhone('+91 98203 14572'), attributionSource: 'referral_link', verificationStatus: 'auto_verified', commissionStatus: 'earned', commissionAmountInr: 150, fraudScore: 0.05, fraudFlags: [], createdAt: iso(2) },
  { attributionId: 'at-2', bookingRef: 'BKG-2026-06-02840', brokerId: 'cp-2', brokerName: 'Sunita Corporate Wellness', patientFirstName: 'Aman', patientMaskedPhone: maskPhone('+91 99205 88812'), attributionSource: 'broker_portal_booking', verificationStatus: 'patient_confirmed', commissionStatus: 'ready_to_pay', commissionAmountInr: 320, fraudScore: 0.1, fraudFlags: [], createdAt: iso(3) },
  // Flagged: fraud_score > 0.5
  { attributionId: 'at-3', bookingRef: 'BKG-2026-06-02839', brokerId: 'cp-3', brokerName: 'Imran Panel Coordinator', patientFirstName: 'Sneha', patientMaskedPhone: maskPhone('+91 90415 22034'), attributionSource: 'post_hoc_claim', verificationStatus: 'pending', commissionStatus: 'pending', commissionAmountInr: 200, fraudScore: 0.72, fraudFlags: ['repeat_phone', 'rapid_burst'], createdAt: iso(1) },
  { attributionId: 'at-4', bookingRef: 'BKG-2026-06-02835', brokerId: 'cp-1', brokerName: 'Ravi Deshmukh', patientFirstName: 'Divya', patientMaskedPhone: maskPhone('+91 87012 90034'), attributionSource: 'post_hoc_claim', verificationStatus: 'patient_denied', commissionStatus: 'pending', commissionAmountInr: null, fraudScore: 0.3, fraudFlags: ['self_referral'], createdAt: iso(4) },
];

export function listAttributions(): Promise<Attribution[]> {
  return delay(ATTRIBUTIONS.map((a) => AttributionSchema.parse(a)));
}

// ── Payouts (seed; approve≠execute) ──────────────────────────────────────────
function payoutMath(gross: number, gstRegistered: boolean) {
  const tdsRate = 5;
  const tds = Math.round(gross * (tdsRate / 100));
  const gstRate = gstRegistered ? 18 : null;
  const gst = gstRegistered ? Math.round(gross * 0.18) : 0;
  return { tdsRate, tds, gstRate, gst, net: gross - tds + gst };
}

interface SeedPayout {
  payoutId: string;
  brokerId: string;
  brokerName: string;
  attributionCount: number;
  gross: number;
  gstRegistered: boolean;
  status: Payout['status'];
  paymentReference: string | null;
  daysAgo: number;
}

const PAYOUTS: SeedPayout[] = [
  { payoutId: 'po-1', brokerId: 'cp-1', brokerName: 'Ravi Deshmukh', attributionCount: 14, gross: 4200, gstRegistered: false, status: 'pending', paymentReference: null, daysAgo: 1 },
  // Approved but NOT executed — the "awaiting execution" state.
  { payoutId: 'po-2', brokerId: 'cp-2', brokerName: 'Sunita Corporate Wellness', attributionCount: 38, gross: 18600, gstRegistered: true, status: 'approved', paymentReference: null, daysAgo: 2 },
  { payoutId: 'po-3', brokerId: 'cp-1', brokerName: 'Ravi Deshmukh', attributionCount: 9, gross: 2700, gstRegistered: false, status: 'paid', paymentReference: 'UTR-8841-2026-05', daysAgo: 33 },
];

function toPayout(p: SeedPayout): Payout {
  const m = payoutMath(p.gross, p.gstRegistered);
  return PayoutSchema.parse({
    payoutId: p.payoutId,
    brokerId: p.brokerId,
    brokerName: p.brokerName,
    periodStart: iso(p.daysAgo + 30),
    periodEnd: iso(p.daysAgo),
    attributionCount: p.attributionCount,
    grossAmountInr: p.gross,
    tdsRate: m.tdsRate,
    tdsAmountInr: m.tds,
    gstRate: m.gstRate,
    gstAmountInr: m.gst,
    netAmountInr: m.net,
    status: p.status,
    paymentReference: p.paymentReference,
  });
}

export function listPayouts(): Promise<Payout[]> {
  return delay(PAYOUTS.map(toPayout));
}

/** APPROVE — distinct from execute (commission.payouts.approve). pending→approved. */
export function approvePayout(payoutId: string, idempotencyKey: string): Promise<PayoutActionResult> {
  return withIdem(idempotencyKey, () =>
    PayoutActionResultSchema.parse({ payoutId, status: 'approved', paymentReference: null }),
  );
}

/** EXECUTE — distinct from approve (commission.payouts.execute). approved→processing. */
export function executePayout(payoutId: string, idempotencyKey: string): Promise<PayoutActionResult> {
  return withIdem(idempotencyKey, () =>
    PayoutActionResultSchema.parse({ payoutId, status: 'processing', paymentReference: `UTR-${Date.now().toString().slice(-8)}` }),
  );
}

// ── Disputes ─────────────────────────────────────────────────────────────────
const DISPUTES: Dispute[] = [
  { disputeId: 'd-1', attributionId: 'at-3', bookingRef: 'BKG-2026-06-02839', brokerName: 'Imran Panel Coordinator', raisedBy: 'tenant_staff', disputeReason: 'suspected_fraud', status: 'investigating', raisedAt: iso(1) },
  { disputeId: 'd-2', attributionId: 'at-4', bookingRef: 'BKG-2026-06-02835', brokerName: 'Ravi Deshmukh', raisedBy: 'patient', disputeReason: 'incorrect_attribution', status: 'open', raisedAt: iso(4) },
];

export function listDisputes(): Promise<Dispute[]> {
  return delay(DISPUTES.map((d) => DisputeSchema.parse(d)));
}

export function raiseDispute(req: RaiseDisputeRequest, idempotencyKey: string): Promise<CommissionCreated> {
  return withIdem(idempotencyKey, () => {
    void req;
    return CommissionCreatedSchema.parse({ id: crypto.randomUUID() });
  });
}

export function resolveDispute(req: ResolveDisputeRequest, idempotencyKey: string): Promise<CommissionCreated> {
  return withIdem(idempotencyKey, () => CommissionCreatedSchema.parse({ id: req.disputeId }));
}

// ── Campaigns (admin; spent_so_far vs total_budget shown as a usage bar) ──────
const CAMPAIGNS: Campaign[] = [
  { campaignId: 'cmp-1', campaignName: 'Monsoon health drive', bonusType: 'flat_bonus_per_booking', bonusValue: 50, isActive: true, totalBudgetInr: 50000, spentSoFarInr: 18400 },
  { campaignId: 'cmp-2', campaignName: 'Corporate Q2 push', bonusType: 'percentage_multiplier', bonusValue: 1.5, isActive: true, totalBudgetInr: 120000, spentSoFarInr: 96250 },
  { campaignId: 'cmp-3', campaignName: 'Diwali wellness (ended)', bonusType: 'flat_bonus_per_booking', bonusValue: 75, isActive: false, totalBudgetInr: 30000, spentSoFarInr: 30000 },
];

export function listCampaigns(): Promise<Campaign[]> {
  return delay(CAMPAIGNS.map((c) => CampaignSchema.parse(c)));
}

export function createCampaign(req: CreateCampaignRequest, idempotencyKey: string): Promise<CommissionCreated> {
  return withIdem(idempotencyKey, () => {
    void req; // A real create persists server-side; the list refetches after invalidation.
    return CommissionCreatedSchema.parse({ id: crypto.randomUUID() });
  });
}

// ── Form 16A (TDS 194H certificate for a PAID payout) ─────────────────────────
// PHI: only PAN LAST 4 is returned here (the full PAN lives solely on the rendered
// document at documentUrl). Status is 'provisional' until filed on TRACES.
export function issueForm16A(payoutId: string, idempotencyKey: string): Promise<Form16ACertificate> {
  return withIdem(idempotencyKey, () =>
    Form16ACertificateSchema.parse({
      certificateId: crypto.randomUUID(),
      payoutId,
      invoiceNumber: `INV-2026-${payoutId.slice(-4).toUpperCase()}`,
      section: '194H',
      financialYear: '2026-27',
      quarter: 'Q1',
      deductorName: 'Apollo Care Pvt Ltd',
      deductorTan: 'BLRA12345C',
      deducteeName: 'Care Partner',
      deducteePanLast4: '234F',
      grossAmountInr: 2700,
      tdsRate: 5,
      tdsAmountInr: 135,
      status: 'provisional',
      tracesCertificateNumber: null,
      // A clearly-synthetic same-origin URL; in mock mode this 404s if opened, but
      // the live document endpoint serves text/html. Kept as a placeholder so the
      // "View certificate" action is wired identically in both modes.
      documentUrl: `/api/v1/commission/payouts/${payoutId}/form-16a/document`,
    }),
  );
}

/** The document URL for a payout's certificate (live: text/html; opened in a new tab). */
export function getForm16ADocumentUrl(payoutId: string): string {
  return `/api/v1/commission/payouts/${payoutId}/form-16a/document`;
}

/** Mock parity for opening the certificate document in a new tab. There is no live
 *  HTML offline, so we render a clearly-synthetic placeholder (NO real PAN) into a
 *  transient blob and open it — never stored in app state. Mirrors the live helper. */
export function openForm16ADocument(payoutId: string): Promise<void> {
  const html =
    `<!doctype html><html lang="en"><head><meta charset="utf-8">` +
    `<title>Form 16A (sample) · ${payoutId}</title></head>` +
    `<body style="font-family:system-ui;padding:2rem;max-width:42rem;margin:auto">` +
    `<h1>Form 16A — TDS Certificate (SAMPLE)</h1>` +
    `<p><strong>Section:</strong> 194H &middot; <strong>Status:</strong> PROVISIONAL` +
    ` (until filed on TRACES)</p>` +
    `<p>This is a clearly-synthetic placeholder rendered in mock mode. The live` +
    ` endpoint serves the real certificate (full PAN) as text/html.</p>` +
    `<p>PAN: <code>XXXXX234F</code> (sample &mdash; last 4 only)</p>` +
    `</body></html>`;
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
  window.open(url, '_blank', 'noopener,noreferrer');
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
  return Promise.resolve();
}

// ─────────────────────────────────────────────────────────────────────────────
// BROKER SELF-SERVICE PORTAL (the Care Partner's OWN data). No id in any path —
// the server resolves broker_id from the JWT. Seed a single "logged-in partner".
// ─────────────────────────────────────────────────────────────────────────────
const SELF_BROKER_ID = 'cp-self';

const SELF_WALLET: BrokerWallet = {
  brokerId: SELF_BROKER_ID,
  pendingInr: 1850,
  earnedInr: 6420,
  readyToPayInr: 3200,
  lifetimePaidInr: 41250,
  currentMonthInr: 2740,
  currentMonthAttributions: 11,
};

export function getBrokerWallet(): Promise<BrokerWallet> {
  return delay(BrokerWalletSchema.parse(SELF_WALLET));
}

const SELF_LINKS: ReferralLink[] = [
  { linkId: 'rl-1', shortCode: 'RAVI-CARD', targetUrl: 'https://book.docslot.in/r/RAVI-CARD', clickCount: 142, conversionCount: 23, isActive: true, campaignName: 'Monsoon health drive' },
  { linkId: 'rl-2', shortCode: 'RAVI-GEN', targetUrl: 'https://book.docslot.in/r/RAVI-GEN', clickCount: 58, conversionCount: 6, isActive: true, campaignName: null },
];

export function listReferralLinks(): Promise<ReferralLink[]> {
  return delay(SELF_LINKS.map((l) => ReferralLinkSchema.parse(l)));
}

export function createReferralLink(req: CreateReferralLinkRequest, idempotencyKey: string): Promise<ReferralLink> {
  return withIdem(idempotencyKey, () => {
    const code = `RAVI-${Date.now().toString(36).slice(-4).toUpperCase()}`;
    return ReferralLinkSchema.parse({
      linkId: crypto.randomUUID(),
      shortCode: code,
      targetUrl: `https://book.docslot.in/r/${code}`,
      clickCount: 0,
      conversionCount: 0,
      isActive: true,
      campaignName: req.campaignName ?? null,
    });
  });
}

export function createPortalBooking(req: BrokerPortalBookingRequest, idempotencyKey: string): Promise<BrokerBookingResult> {
  return withIdem(idempotencyKey, () => {
    void req; // The patient must approve via WhatsApp OTP — status reflects that.
    return BrokerBookingResultSchema.parse({
      bookingId: crypto.randomUUID(),
      bookingNumber: `BKG-2026-06-${Math.floor(10000 + Math.random() * 89999)}`,
      attributionId: crypto.randomUUID(),
      status: 'awaiting_patient_consent',
    });
  });
}
