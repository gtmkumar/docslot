// Mock adapter for the Team console Invites tab (#89) — token-based invitations.
// Shapes mirror mediq.SharedDataModel/Docslot/Admin/InvitationDtos.cs so the
// mock→real swap is a no-op for zod.
//
// INVARIANTS baked in:
//  - The plaintext token is returned ONCE (create/resend) and is NEVER stored on a
//    row — the list/read shapes carry neither the token nor its hash.
//  - State is mutable: create adds a pending row; resend rotates the token +
//    extends expiry + bumps resend_count; revoke flips status to 'revoked' — so a
//    refetch (after invalidation) reflects the change, exactly as the real
//    endpoints do.
//  - Every state-changing POST takes a caller-generated Idempotency-Key (de-duped).
//  - NO PHI: invited emails + the inviter are staff identities only. `invitedByUserId`
//    references seeded People-tab users so the console can resolve a display name.

import {
  AcceptInvitationResultSchema,
  CreateTenantResultSchema,
  PincodeLookupSchema,
  type PincodeLookup,
  InvitationSchema,
  InvitationListSchema,
  InvitationTokenResultSchema,
  RevokeInvitationResultSchema,
  type AcceptInvitationRequest,
  type AcceptInvitationResult,
  type CreateInvitationRequest,
  type CreateTenantRequest,
  type CreateTenantResult,
  type Invitation,
  type InvitationList,
  type InvitationTokenResult,
  type RevokeInvitationResult,
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

const DAY = 86_400_000;
const HOUR = 3_600_000;
const TTL_DAYS = 7; // mirrors InvitationPolicy.Ttl on the server
const at = (msFromNow: number) => new Date(Date.now() + msFromNow).toISOString();

// Mirrors the People-tab seed ids/names (lib/mock/rbac.ts) so the console resolves
// an "invited by" name for these rows.
const ADMIN_USER_ID = '00000000-0000-0000-0000-0000000admin';

// Static role-name lookup mirroring the mock roles seed — the LEFT JOIN the real
// list query does. Unknown ids fall back to null (shown as "no role").
const ROLE_NAMES: Record<string, string> = {
  'r-owner': 'Tenant Owner',
  'r-admin': 'Tenant Admin',
  'r-staff': 'Reception Staff',
  'r-viewer': 'Viewer',
  'r-billing': 'Billing Desk',
};
const roleNameOf = (roleId: string | null | undefined): string | null =>
  roleId ? (ROLE_NAMES[roleId] ?? null) : null;

/** An opaque, URL-safe one-time token like InvitationTokenFactory emits — the mock
 *  never keeps it (only this transient return value carries the plaintext). */
function newToken(): string {
  const bytes = new Uint8Array(24);
  crypto.getRandomValues(bytes);
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

let INVITATIONS: Invitation[] = [
  InvitationSchema.parse({
    invitationId: 'inv-1', invitedEmail: 'kavya.nurse@apollocare.in',
    roleId: 'r-staff', roleName: 'Reception Staff', status: 'pending',
    expiresAt: at(5 * DAY), resendCount: 0, invitedByUserId: ADMIN_USER_ID,
    acceptedUserId: null, acceptedAt: null, revokedAt: null, createdAt: at(-2 * DAY),
  }),
  InvitationSchema.parse({
    invitationId: 'inv-2', invitedEmail: 'dr.reddy@apollocare.in',
    roleId: null, roleName: null, status: 'pending',
    expiresAt: at(6 * DAY), resendCount: 1, invitedByUserId: ADMIN_USER_ID,
    acceptedUserId: null, acceptedAt: null, revokedAt: null, createdAt: at(-1 * DAY),
  }),
  // Expiring soon — exercises the countdown warning tone in the row.
  InvitationSchema.parse({
    invitationId: 'inv-3', invitedEmail: 'frontdesk2@apollocare.in',
    roleId: 'r-billing', roleName: 'Billing Desk', status: 'pending',
    expiresAt: at(6 * HOUR), resendCount: 2, invitedByUserId: 'u-2',
    acceptedUserId: null, acceptedAt: null, revokedAt: null, createdAt: at(-6.5 * DAY),
  }),
];

/** GET /tenants/{id}/invitations?status= — newest first. Omitting status returns all. */
export function listInvitations(status?: string): Promise<InvitationList> {
  const items = INVITATIONS
    .filter((i) => !status || i.status === status)
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
    .map((i) => InvitationSchema.parse(i));
  return delay(InvitationListSchema.parse({ items, count: items.length }));
}

/** POST /tenants/{id}/invitations — mint a pending invite; return the one-time token. */
export function createInvitation(
  req: CreateInvitationRequest,
  idempotencyKey: string,
): Promise<InvitationTokenResult> {
  return withIdem(idempotencyKey, () => {
    const expiresAt = at(TTL_DAYS * DAY);
    const row = InvitationSchema.parse({
      invitationId: `inv-${crypto.randomUUID().slice(0, 8)}`,
      invitedEmail: req.email,
      roleId: req.roleId ?? null,
      roleName: roleNameOf(req.roleId),
      status: 'pending',
      expiresAt,
      resendCount: 0,
      invitedByUserId: ADMIN_USER_ID,
      acceptedUserId: null,
      acceptedAt: null,
      revokedAt: null,
      createdAt: new Date().toISOString(),
    });
    INVITATIONS = [row, ...INVITATIONS];
    return InvitationTokenResultSchema.parse({
      invitationId: row.invitationId, token: newToken(), expiresAt, resendCount: 0,
    });
  });
}

/** POST /tenants/{id}/invitations/{id}/resend — rotate the token + extend expiry,
 *  bumping resend_count. Pending-only; returns a NEW one-time token. */
export function resendInvitation(
  invitationId: string,
  idempotencyKey: string,
): Promise<InvitationTokenResult> {
  return withIdem(idempotencyKey, () => {
    const cur = INVITATIONS.find((i) => i.invitationId === invitationId);
    if (!cur || cur.status !== 'pending') {
      // The real endpoint 422s when it is not pending; a thrown Error surfaces via
      // toUserError as the generic toast (the console only resends pending rows).
      throw new Error('invitation is not pending');
    }
    const expiresAt = at(TTL_DAYS * DAY);
    const resendCount = cur.resendCount + 1;
    INVITATIONS = INVITATIONS.map((i) =>
      i.invitationId === invitationId ? InvitationSchema.parse({ ...i, expiresAt, resendCount }) : i,
    );
    return InvitationTokenResultSchema.parse({ invitationId, token: newToken(), expiresAt, resendCount });
  });
}

/** POST /tenants — mock tenant onboarding: fabricates a tenant id + a one-time owner
 *  invitation token (mirrors CreateTenantCommand: tenant + tenant_owner invite atomically). */
export function createTenant(req: CreateTenantRequest, idempotencyKey: string): Promise<CreateTenantResult> {
  return withIdem(idempotencyKey, () =>
    CreateTenantResultSchema.parse({
      tenantId: crypto.randomUUID(),
      tenantCode: req.tenantCode,
      displayName: req.displayName,
      invitationId: `inv-${crypto.randomUUID().slice(0, 8)}`,
      inviteToken: newToken(),
      inviteExpiresAt: at(TTL_DAYS * DAY),
      adminEmail: req.adminEmail,
    }),
  );
}

/** GET /geo/pincode/{pin} — mock lookup: a handful of canned codes so the onboarding
 *  form's auto-fill is demoable offline; anything else rejects like the real 404. */
const MOCK_PINCODES: Record<string, Omit<PincodeLookup, 'pinCode'>> = {
  '400001': { state: 'Maharashtra', district: 'Mumbai', areas: ['Fort', 'M.P.T.', 'Town Hall'], latitude: 18.938771, longitude: 72.835335 },
  '110001': { state: 'Delhi', district: 'New Delhi', areas: ['Connaught Place', 'Janpath', 'Sansad Marg'], latitude: 28.632735, longitude: 77.219696 },
  '854301': { state: 'Bihar', district: 'Purnia', areas: ['Purnea City', 'Line Bazar', 'Bhatta Bazar'], latitude: 25.777268, longitude: 87.475556 },
  '560001': { state: 'Karnataka', district: 'Bengaluru', areas: ['Bangalore G.P.O.', 'Vidhana Soudha', 'HighCourt'], latitude: 12.972442, longitude: 77.580643 },
};

export function lookupPincode(pinCode: string): Promise<PincodeLookup> {
  const hit = MOCK_PINCODES[pinCode];
  if (!hit) return Promise.reject(new Error(`PIN code ${pinCode} was not found in the postal directory.`));
  return delay(PincodeLookupSchema.parse({ pinCode, ...hit }));
}

/** POST /invitations/accept — mock redemption. Any non-empty token "succeeds" so the
 *  public accept page is demoable offline; the real endpoint 422s bad/expired tokens. */
export function acceptInvitation(req: AcceptInvitationRequest): Promise<AcceptInvitationResult> {
  if (!req.token.trim()) {
    return Promise.reject(new Error('This invitation is invalid, expired, or has already been used.'));
  }
  return delay(
    AcceptInvitationResultSchema.parse({
      userId: crypto.randomUUID(),
      tenantId: crypto.randomUUID(),
      alreadyExisted: false,
    }),
  );
}

/** POST /tenants/{id}/invitations/{id}/revoke — flip a pending invite to revoked.
 *  Idempotent: `alreadyInactive`=true when it was not pending. */
export function revokeInvitation(
  invitationId: string,
  idempotencyKey: string,
): Promise<RevokeInvitationResult> {
  return withIdem(idempotencyKey, () => {
    const cur = INVITATIONS.find((i) => i.invitationId === invitationId);
    if (!cur || cur.status !== 'pending') {
      return RevokeInvitationResultSchema.parse({ invitationId, alreadyInactive: true });
    }
    INVITATIONS = INVITATIONS.map((i) =>
      i.invitationId === invitationId
        ? InvitationSchema.parse({ ...i, status: 'revoked' as const, revokedAt: new Date().toISOString() })
        : i,
    );
    return RevokeInvitationResultSchema.parse({ invitationId, alreadyInactive: false });
  });
}
