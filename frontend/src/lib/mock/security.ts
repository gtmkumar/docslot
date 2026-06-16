// Mock adapter for the Security & Compliance console (Slice 05
// security_hardening). Shapes mirror the SecurityController result records in
// mediq.Application/Features/Security and the SQL views/tables, so the mock→real
// swap is a no-op for zod.
//
// SENSITIVE SURFACE — invariants baked into this mock:
//  - NO PHI is ever returned. Subject identity is a MASKED phone (maskPhone),
//    never a name/email. No medical content.
//  - NO key material. The key-status view carries metadata + rotation status only.
//  - The deletion certificate's signature/hashes are returned ONCE on the erase
//    result and are NEVER seeded or re-fetchable (mirrors the once-shown secret
//    pattern from the developer portal).
//  - Every state-changing POST takes a caller-generated Idempotency-Key (de-duped).

import { maskPhone } from '@/lib/format';
import {
  AuditAnchorResultSchema,
  AuditAnchorSchema,
  AuditChainVerifySchema,
  BreachSchema,
  DataExportResultSchema,
  DpdpRequestSchema,
  ErasureResultSchema,
  KeyStatusSchema,
  ReviewQueueItemSchema,
  SecurityCreatedSchema,
  type AuditAnchor,
  type AuditAnchorResult,
  type AuditChainVerify,
  type Breach,
  type DataExportResult,
  type DpdpRequest,
  type ErasureResult,
  type KeyStatus,
  type ReviewQueueItem,
  type SecurityCreated,
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

function hex(len: number): string {
  let s = '';
  while (s.length < len) s += crypto.randomUUID().replace(/-/g, '');
  return s.slice(0, len);
}

const iso = (msAgo: number) => new Date(Date.now() - msAgo).toISOString();
const HOUR = 3_600_000;
const DAY = 86_400_000;

// ── Audit chain ──────────────────────────────────────────────────────────────
// The chain is seeded BROKEN at one link to surface the known concurrency
// finding (the verify view shows a break; "Verify now" re-runs the same mock).
let chainIntact = false;
const SEEDED_BREAK = {
  sequence: 184_512,
  auditId: crypto.randomUUID(),
  expectedHash: hex(64),
  actualHash: hex(64),
};

export function verifyAuditChain(): Promise<AuditChainVerify> {
  return delay(
    AuditChainVerifySchema.parse({
      intact: chainIntact,
      breaks: chainIntact ? [] : [SEEDED_BREAK],
      lastVerifiedAt: iso(2 * 60_000),
    }),
  );
}

export function anchorAuditChain(
  input: { anchorType: string; anchorReference: string },
  idempotencyKey: string,
): Promise<AuditAnchorResult> {
  return withIdem(idempotencyKey, () => {
    void input;
    return AuditAnchorResultSchema.parse({
      anchorId: crypto.randomUUID(),
      headSequence: 184_998,
      headHash: hex(64),
    });
  });
}

const ANCHORS: AuditAnchor[] = [
  { anchorId: crypto.randomUUID(), chainHeadSequence: 184_010, chainHeadHash: hex(64), anchorType: 'transparency_log', anchorReference: 'https://transparency.docslot.in/log/8a13', anchoredAt: iso(7 * DAY) },
  { anchorId: crypto.randomUUID(), chainHeadSequence: 183_550, chainHeadHash: hex(64), anchorType: 'notary_api', anchorReference: 'notary:txn:0xc91f…', anchoredAt: iso(14 * DAY) },
];

export function listAnchors(): Promise<AuditAnchor[]> {
  return delay(ANCHORS.map((a) => AuditAnchorSchema.parse(a)));
}

// ── DPDP rights ──────────────────────────────────────────────────────────────
const DPDP_REQUESTS: DpdpRequest[] = [
  { requestId: 'dr-1', kind: 'export', subjectMaskedPhone: maskPhone('+91 98203 14572'), status: 'completed', scope: 'all', reason: 'Subject access request', gracePeriodEndsAt: null, createdAt: iso(3 * DAY) },
  { requestId: 'dr-2', kind: 'erasure', subjectMaskedPhone: maskPhone('+91 90120 56781'), status: 'pending', scope: 'all', reason: 'Right to be forgotten', gracePeriodEndsAt: iso(-27 * DAY), createdAt: iso(3 * DAY) },
  { requestId: 'dr-3', kind: 'correction', subjectMaskedPhone: maskPhone('+91 99205 88812'), status: 'processing', scope: 'profile', reason: 'Wrong DOB on record', gracePeriodEndsAt: null, createdAt: iso(1 * DAY) },
];

export function listDpdpRequests(): Promise<DpdpRequest[]> {
  return delay(DPDP_REQUESTS.map((r) => DpdpRequestSchema.parse(r)));
}

export function exportSubjectData(
  input: { subjectPhone: string },
  idempotencyKey: string,
): Promise<DataExportResult> {
  return withIdem(idempotencyKey, () => {
    void input;
    return DataExportResultSchema.parse({
      requestId: crypto.randomUUID(),
      format: 'FHIR-R4',
      recordCount: 47,
      checksum: hex(64),
      downloadToken: crypto.randomUUID(),
    });
  });
}

/** Cryptographic erasure — IRREVERSIBLE. Returns the deletion certificate ONCE. */
export function eraseSubjectData(
  input: { deletionRequestId: string; subjectPhone: string },
  idempotencyKey: string,
): Promise<ErasureResult> {
  return withIdem(idempotencyKey, () => {
    void input;
    return ErasureResultSchema.parse({
      certificateId: crypto.randomUUID(),
      destroyedKeyIds: [crypto.randomUUID(), crypto.randomUUID()],
      preHash: hex(64),
      postHash: hex(64),
      signatureAlgorithm: 'ECDSA_P256_SHA256',
      digitalSignature: hex(128),
      certifiedAt: new Date().toISOString(),
      deletedRecordCounts: { bookings: 12, prescriptions: 5, reports: 3, messages: 88 },
    });
  });
}

// ── Breach register ──────────────────────────────────────────────────────────
const BREACHES: Breach[] = [
  // Reported within 72h.
  { breachId: 'b-1', breachType: 'unauthorized_access', severity: 'high', description: 'Partner API key used from an unrecognised IP range', affectedRecordCount: 320, detectedAt: iso(2 * DAY), reportedToDpbAt: iso(2 * DAY - 6 * HOUR), resolvedAt: null },
  // OVERDUE — detected >72h ago, not yet reported to DPB.
  { breachId: 'b-2', breachType: 'misconfiguration', severity: 'critical', description: 'Report bucket briefly public due to a deploy misconfig', affectedRecordCount: 1450, detectedAt: iso(4 * DAY), reportedToDpbAt: null, resolvedAt: null },
  { breachId: 'b-3', breachType: 'phishing', severity: 'medium', description: 'Staff credential phishing attempt (blocked)', affectedRecordCount: 0, detectedAt: iso(10 * DAY), reportedToDpbAt: iso(10 * DAY - 12 * HOUR), resolvedAt: iso(9 * DAY) },
];

export function listBreaches(): Promise<Breach[]> {
  return delay(BREACHES.map((b) => BreachSchema.parse(b)));
}

export function reportBreach(
  input: { breachType: string; severity: string; description: string; affectedRecordCount: number },
  idempotencyKey: string,
): Promise<SecurityCreated> {
  return withIdem(idempotencyKey, () => {
    void input;
    return SecurityCreatedSchema.parse({ id: crypto.randomUUID() });
  });
}

// ── Break-glass & review queue ───────────────────────────────────────────────
const REVIEW_QUEUE: ReviewQueueItem[] = [
  { source: 'break_glass', itemId: 'rq-1', severity: 'high', occurredAt: iso(40 * 60_000), description: 'Break-glass medical record access: suspected cardiac emergency, patient unconscious', actorLabel: 'Dr. A.S.', subjectMaskedPhone: maskPhone('+91 98203 14572') },
  { source: 'anomaly', itemId: 'rq-2', severity: 'medium', occurredAt: iso(3 * HOUR), description: 'Unusual bulk export volume from a single session', actorLabel: 'R.M.', subjectMaskedPhone: null },
  { source: 'consent_revocation', itemId: 'rq-3', severity: 'medium', occurredAt: iso(5 * HOUR), description: 'Consent revoked, downstream not yet notified', actorLabel: null, subjectMaskedPhone: maskPhone('+91 90120 56781') },
];

export function listReviewQueue(): Promise<ReviewQueueItem[]> {
  return delay(REVIEW_QUEUE.map((r) => ReviewQueueItemSchema.parse(r)));
}

export function recordBreakGlass(
  input: { resourceType: string; resourceId: string; justification: string },
  idempotencyKey: string,
): Promise<SecurityCreated> {
  return withIdem(idempotencyKey, () => {
    void input;
    return SecurityCreatedSchema.parse({ id: crypto.randomUUID() });
  });
}

// ── Encryption keys (read; NO key material) ──────────────────────────────────
const KEYS: KeyStatus[] = [
  { keyId: 'k-1', tenantName: 'Apollo Care · Andheri West', dataClass: 'phi_medical', activatedAt: iso(80 * DAY), nextRotationDueAt: iso(-3 * DAY), rotationStatus: 'overdue', daysUntilRotation: -3, usageCount: 184_512 },
  { keyId: 'k-2', tenantName: 'Apollo Care · Andheri West', dataClass: 'pii_contact', activatedAt: iso(60 * DAY), nextRotationDueAt: iso(-4 * DAY), rotationStatus: 'due_soon', daysUntilRotation: 4, usageCount: 92_004 },
  { keyId: 'k-3', tenantName: 'Dr. Mehta · Cardiology', dataClass: 'phi_medical', activatedAt: iso(20 * DAY), nextRotationDueAt: iso(-70 * DAY), rotationStatus: 'ok', daysUntilRotation: 70, usageCount: 14_220 },
  { keyId: 'k-4', tenantName: null, dataClass: 'platform_secrets', activatedAt: iso(10 * DAY), nextRotationDueAt: iso(-80 * DAY), rotationStatus: 'ok', daysUntilRotation: 80, usageCount: 4_001 },
];

export function listKeyStatus(): Promise<KeyStatus[]> {
  return delay(KEYS.map((k) => KeyStatusSchema.parse(k)));
}
