// Mock adapter for the Team console's Security policy (#91) + IP allow-list.
// Shapes mirror mediq.SharedDataModel/Docslot/Security/SecurityPolicyDtos.cs, so
// the mock→real swap is a no-op for zod.
//
// INVARIANTS baked in:
//  - NO PHI. The policy is tenant configuration; the allow-list holds CIDRs only.
//  - Absent keys merge over code defaults server-side; the mock seeds a realistically
//    CONFIGURED Apollo Care policy so the demo exercises the warning + editor states.
//  - `staffPendingMfaEnrolment` is DERIVED from the staff directory the People tab
//    already uses (listTenantUsers), so the "N of M have 2FA" line and the pending
//    warning stay consistent across the console.
//  - Every state-changing write takes a caller-generated Idempotency-Key (de-duped).
//  - The policy + allow-list are MUTABLE in-memory so a refetch after invalidation
//    reflects the change — same as the real endpoints persisting to JSONB / the table.

import { listTenantUsers } from './rbac';
import {
  IpAllowlistEntrySchema,
  SecurityPolicyViewSchema,
  type AddIpAllowlistRequest,
  type IpAllowlistEntry,
  type SecurityPolicyInput,
  type SecurityPolicyView,
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

// ── Policy seed (a realistically CONFIGURED tenant, not the all-off default) ───
// mfaPolicy=owners_admins with an admin (Priyanka) who lacks 2FA → the pending
// warning shows a count of 1 in the demo. masking ON mirrors the code default.
let POLICY: SecurityPolicyInput = {
  mfaPolicy: 'owners_admins',
  minPasswordLength: 10,
  idleTimeoutMinutes: 30,
  requireNewDeviceVerification: false,
  restrictLoginHours: false,
  loginHoursStart: '08:00',
  loginHoursEnd: '20:00',
  doctorsExemptFromHours: true,
  ipAllowlistEnabled: false,
  maskSensitiveForReceptionist: true,
};

/** Server-side derivation of the pending-enrolment count: active staff subject to a
 *  REQUIRED-2FA tier who still lack mfa_enabled. owners_admins narrows to staff
 *  holding an owner/admin role. A dashboard estimate (role-derived; overrides are
 *  ignored) — mirrors the backend's intent. */
async function derivePending(policy: SecurityPolicyInput): Promise<number> {
  if (policy.mfaPolicy === 'optional') return 0;
  const users = await listTenantUsers();
  const withoutMfa = users.filter((u) => u.isActive && !u.mfaEnabled);
  if (policy.mfaPolicy === 'all') return withoutMfa.length;
  return withoutMfa.filter((u) => u.roles.some((r) => /owner|admin/i.test(r.roleKey))).length;
}

async function toView(policy: SecurityPolicyInput): Promise<SecurityPolicyView> {
  return SecurityPolicyViewSchema.parse({ ...policy, staffPendingMfaEnrolment: await derivePending(policy) });
}

export async function getSecurityPolicy(): Promise<SecurityPolicyView> {
  return delay(await toView(POLICY));
}

export async function updateSecurityPolicy(
  policy: SecurityPolicyInput,
  idempotencyKey: string,
): Promise<SecurityPolicyView> {
  // Persist the edit, then re-read the effective policy with a fresh pending count
  // (the real handler does exactly this). Idempotent per key so a retry is a no-op.
  const view = await withIdem(idempotencyKey, () => {
    POLICY = { ...policy };
    return POLICY;
  });
  return toView(view);
}

// ── IP allow-list seed (mutable — add appends, remove drops the row) ───────────
let ALLOWLIST: IpAllowlistEntry[] = [
  IpAllowlistEntrySchema.parse({
    allowlistId: 'ip-1',
    cidrRange: '103.21.244.0/24',
    label: 'Jubilee Hills clinic LAN',
    isActive: true,
    createdAt: new Date(Date.now() - 40 * 86_400_000).toISOString(),
    expiresAt: null,
  }),
  IpAllowlistEntrySchema.parse({
    allowlistId: 'ip-2',
    cidrRange: '49.36.12.88/32',
    label: 'Billing desk static IP',
    isActive: true,
    createdAt: new Date(Date.now() - 12 * 86_400_000).toISOString(),
    expiresAt: null,
  }),
];

export function listIpAllowlist(): Promise<IpAllowlistEntry[]> {
  return delay(ALLOWLIST.map((e) => IpAllowlistEntrySchema.parse(e)));
}

export function addIpAllowlist(req: AddIpAllowlistRequest, idempotencyKey: string): Promise<IpAllowlistEntry> {
  return withIdem(idempotencyKey, () => {
    const entry = IpAllowlistEntrySchema.parse({
      allowlistId: `ip-${Date.now()}`,
      cidrRange: req.cidrRange.trim(),
      label: req.label?.trim() || null,
      isActive: true,
      createdAt: new Date().toISOString(),
      expiresAt: req.expiresAt ?? null,
    });
    ALLOWLIST = [...ALLOWLIST, entry];
    return entry;
  });
}

export function removeIpAllowlist(allowlistId: string, idempotencyKey: string): Promise<boolean> {
  return withIdem(idempotencyKey, () => {
    const before = ALLOWLIST.length;
    ALLOWLIST = ALLOWLIST.filter((e) => e.allowlistId !== allowlistId);
    return ALLOWLIST.length < before;
  });
}
