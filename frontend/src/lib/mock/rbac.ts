// Mock adapter for auth + Team & Roles (RBAC) — Slice 01 platform_core.
// Shapes mirror the real .NET DTOs (mediq.SharedDataModel/Docslot/Auth + Admin)
// and the canonical permission keys in database/01_platform_core.sql /
// 08_rbac_navigation.sql, so the mock→real swap is a no-op for zod.
//
// Everything returns a Promise + zod-parses its payload. POST-equivalent
// mutations take an idempotencyKey (generated once per action by the caller),
// de-duped via the shared idemCache pattern.

import { maskPhone } from '@/lib/format';
import {
  AssignRoleResultSchema,
  BranchSchema,
  BulkImportResultSchema,
  CreateModuleResultSchema,
  CreatePermissionResultSchema,
  CreateUserResultSchema,
  SetMemberScopeResultSchema,
  DuplicateRoleResultSchema,
  EffectiveAccessSchema,
  EffectivePermissionSchema,
  IamPermissionDtoSchema,
  MeSchema,
  ModuleDtoSchema,
  PermissionDefSchema,
  RoleMatrixSchema,
  RolePermissionToggleResultSchema,
  RoleSchema,
  RevokeRoleResultSchema,
  SetOverrideResultSchema,
  SetUserStatusResultSchema,
  UpdateUserProfileResultSchema,
  ResetAccessResultSchema,
  TokenResponseSchema,
  TenantOverridesListSchema,
  UserListItemSchema,
  UserOverrideSchema,
  type AssignRoleRequest,
  type AssignRoleResult,
  type Branch,
  type BulkImportResult,
  type BulkImportUsersRequest,
  type UserCsvResult,
  type RevokeRoleResult,
  type SetMemberScopeRequest,
  type SetMemberScopeResult,
  type CreateModuleRequest,
  type CreateModuleResult,
  type CreatePermissionRequest,
  type CreatePermissionResult,
  type CreateUserRequest,
  type CreateUserResult,
  type DuplicateRoleRequest,
  type DuplicateRoleResult,
  type EffectiveAccess,
  type EffectivePermission,
  type IamPermissionDto,
  type LoginRequest,
  type Me,
  type ModuleDto,
  type PermissionDef,
  type Role,
  type RoleMatrix,
  type RolePermissionToggleResult,
  type SetOverrideRequest,
  type SetOverrideResult,
  type TenantOverridesList,
  type TokenResponse,
  type UserListItem,
  type UserOverride,
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

// ── Error shape the login flow throws on bad credentials / lockout ───────────
export class MockApiError extends Error {
  constructor(
    readonly status: number,
    /** i18n key the UI shows (e.g. 'auth.error.invalid' | 'auth.error.locked'). */
    readonly messageKey: string,
  ) {
    super(messageKey);
    this.name = 'MockApiError';
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// AUTH
// ─────────────────────────────────────────────────────────────────────────────

const TENANT_ID = '00000000-0000-0000-0000-00000000ap01';
const ADMIN_USER_ID = '00000000-0000-0000-0000-0000000admin';

// Demo credentials for the mock. The real API verifies bcrypt/argon2 server-side.
const DEMO_EMAIL = 'priyanka@apollocare.in';
const DEMO_PASSWORD = 'reception';

// Lockout simulation: 5 consecutive bad passwords for a known email → locked.
const failedAttempts = new Map<string, number>();
const LOCKOUT_THRESHOLD = 5;

export function login(req: LoginRequest): Promise<TokenResponse> {
  const email = req.email.trim().toLowerCase();
  const fails = failedAttempts.get(email) ?? 0;
  if (fails >= LOCKOUT_THRESHOLD) {
    return Promise.reject(new MockApiError(423, 'auth.error.locked'));
  }
  if (email !== DEMO_EMAIL || req.password !== DEMO_PASSWORD) {
    failedAttempts.set(email, fails + 1);
    const nowFailed = fails + 1;
    if (nowFailed >= LOCKOUT_THRESHOLD) {
      return Promise.reject(new MockApiError(423, 'auth.error.locked'));
    }
    // Uniform message (no user enumeration) — same for unknown email + bad pwd.
    return Promise.reject(new MockApiError(401, 'auth.error.invalid'));
  }
  failedAttempts.delete(email);
  return delay(
    TokenResponseSchema.parse({
      accessToken: `mock.jwt.${crypto.randomUUID()}`,
      refreshToken: crypto.randomUUID(),
      expiresInSeconds: 900,
      userId: ADMIN_USER_ID,
      activeTenantId: req.tenantId ?? TENANT_ID,
      mfaRequired: false,
    }),
  );
}

export function refresh(refreshToken: string): Promise<TokenResponse> {
  if (!refreshToken) return Promise.reject(new MockApiError(401, 'auth.error.session'));
  return delay(
    TokenResponseSchema.parse({
      accessToken: `mock.jwt.${crypto.randomUUID()}`,
      refreshToken: crypto.randomUUID(),
      expiresInSeconds: 900,
      userId: ADMIN_USER_ID,
      activeTenantId: TENANT_ID,
      mfaRequired: false,
    }),
  );
}

export function logout(_refreshToken?: string): Promise<void> {
  return delay(undefined);
}

export function getMe(): Promise<Me> {
  return delay(
    MeSchema.parse({
      userId: ADMIN_USER_ID,
      email: DEMO_EMAIL,
      fullName: 'Priyanka R',
      preferredLanguage: 'en',
      timezone: 'Asia/Kolkata',
      mfaEnabled: false,
      activeTenantId: TENANT_ID,
      tenants: [
        {
          tenantId: TENANT_ID,
          tenantCode: 'APOLLO-AND',
          displayName: 'Apollo Care · Andheri West',
          tenantType: 'hospital',
          isPrimary: true,
        },
      ],
    }),
  );
}

/** Demo creds surfaced on the login screen so reviewers can sign in. */
export const DEMO_LOGIN = { email: DEMO_EMAIL, password: DEMO_PASSWORD };

// ─────────────────────────────────────────────────────────────────────────────
// ROLES + PERMISSION REGISTRY (seed mirrors platform.roles / platform.permissions)
// ─────────────────────────────────────────────────────────────────────────────

// memberCount is a computed value (listRoles fills it), never stored on the seed.
type SeedRole = Omit<Role, 'memberCount'>;
const ROLES: SeedRole[] = [
  { roleId: 'r-owner', roleKey: 'tenant_owner', name: 'Tenant Owner', scope: 'tenant', isSystem: true, tenantId: null },
  { roleId: 'r-admin', roleKey: 'tenant_admin', name: 'Tenant Admin', scope: 'tenant', isSystem: true, tenantId: null },
  { roleId: 'r-staff', roleKey: 'tenant_staff', name: 'Reception Staff', scope: 'tenant', isSystem: true, tenantId: null },
  { roleId: 'r-viewer', roleKey: 'tenant_viewer', name: 'Viewer', scope: 'tenant', isSystem: true, tenantId: null },
  // A custom, tenant-scoped role (tenantId set, not system → editable).
  { roleId: 'r-billing', roleKey: 'billing_desk', name: 'Billing Desk', scope: 'tenant', isSystem: false, tenantId: TENANT_ID },
];

// Subset of platform.permissions relevant to the Team & Roles surface. Keys are
// the canonical permission_key values from the SQL seed.
const PERMISSION_REGISTRY: PermissionDef[] = [
  { permissionKey: 'tenant.users.read', resource: 'users', action: 'read', scope: 'tenant', description: 'View users in the tenant', isDangerous: false },
  { permissionKey: 'tenant.users.create', resource: 'users', action: 'create', scope: 'tenant', description: 'Invite / create users', isDangerous: false },
  { permissionKey: 'tenant.users.update', resource: 'users', action: 'update', scope: 'tenant', description: 'Edit users', isDangerous: false },
  { permissionKey: 'tenant.users.remove', resource: 'users', action: 'remove', scope: 'tenant', description: 'Remove users from the tenant', isDangerous: true },
  { permissionKey: 'tenant.roles.assign', resource: 'roles', action: 'assign', scope: 'tenant', description: 'Assign / revoke roles', isDangerous: false },
  { permissionKey: 'tenant.settings.read', resource: 'settings', action: 'read', scope: 'tenant', description: 'View tenant settings', isDangerous: false },
  { permissionKey: 'tenant.settings.update', resource: 'settings', action: 'update', scope: 'tenant', description: 'Update tenant settings', isDangerous: false },
  { permissionKey: 'tenant.billing.read', resource: 'billing', action: 'read', scope: 'tenant', description: 'View billing', isDangerous: false },
  { permissionKey: 'tenant.audit.read', resource: 'audit', action: 'read', scope: 'tenant', description: 'Read the audit log', isDangerous: false },
  { permissionKey: 'platform.overrides.grant', resource: 'overrides', action: 'grant', scope: 'platform', description: 'Grant / deny per-user permission overrides', isDangerous: true },
  { permissionKey: 'platform.overrides.read', resource: 'overrides', action: 'read', scope: 'platform', description: 'Read per-user overrides', isDangerous: false },
  { permissionKey: 'docslot.booking.read', resource: 'booking', action: 'read', scope: 'tenant', description: 'View bookings', isDangerous: false },
  { permissionKey: 'docslot.booking.create', resource: 'booking', action: 'create', scope: 'tenant', description: 'Create bookings', isDangerous: false },
  { permissionKey: 'docslot.booking.approve', resource: 'booking', action: 'approve', scope: 'tenant', description: 'Approve bookings', isDangerous: false },
  { permissionKey: 'docslot.booking.cancel', resource: 'booking', action: 'cancel', scope: 'tenant', description: 'Cancel bookings', isDangerous: true },
  { permissionKey: 'docslot.patient.read', resource: 'patient', action: 'read', scope: 'tenant', description: 'View patients', isDangerous: false },
  { permissionKey: 'docslot.patient.update', resource: 'patient', action: 'update', scope: 'tenant', description: 'Edit / register patients', isDangerous: false },
  { permissionKey: 'docslot.doctor.read', resource: 'doctor', action: 'read', scope: 'tenant', description: 'View doctors', isDangerous: false },
  { permissionKey: 'docslot.analytics.read', resource: 'analytics', action: 'read', scope: 'tenant', description: 'View analytics', isDangerous: false },
];

// Role → granted permission keys (mock; the real set comes from role_permissions).
const ROLE_GRANTS: Record<string, string[]> = {
  'r-owner': PERMISSION_REGISTRY.map((p) => p.permissionKey),
  'r-admin': [
    'tenant.users.read', 'tenant.users.create', 'tenant.users.update', 'tenant.roles.assign',
    'tenant.settings.read', 'tenant.audit.read', 'platform.overrides.read',
    'docslot.booking.read', 'docslot.booking.create', 'docslot.booking.approve', 'docslot.patient.read', 'docslot.doctor.read', 'docslot.analytics.read',
  ],
  'r-staff': [
    'docslot.booking.read', 'docslot.booking.create', 'docslot.booking.approve', 'docslot.booking.cancel',
    'docslot.patient.read', 'docslot.patient.update', 'docslot.doctor.read',
  ],
  'r-viewer': ['docslot.booking.read', 'docslot.patient.read', 'docslot.doctor.read', 'docslot.analytics.read'],
  'r-billing': ['tenant.billing.read', 'docslot.booking.read', 'docslot.patient.read'],
};

// ─────────────────────────────────────────────────────────────────────────────
// USERS (seed)
// ─────────────────────────────────────────────────────────────────────────────

interface SeedUser {
  userId: string;
  email: string;
  fullName: string;
  phone: string | null;
  isActive: boolean;
  mfaEnabled: boolean;
  lastLoginAt: string | null;
  /** #87 — most-recent active session activity; drives the People "Online" dot.
   *  Relative to import time so the mock shows a couple of "online" teammates. */
  lastActivityAt: string | null;
  /** #90 — org SCOPE (display-only). Null branch = "All branches"; null/blank
   *  department = "All departments". MUTABLE so the setMemberScope mock persists a
   *  scope change flag-off (the optimistic UI reconciles against a real change). */
  branchId: string | null;
  department: string | null;
  roleIds: { userTenantRoleId: string; roleId: string; isPrimary: boolean; expiresAt: string | null }[];
  overrides: UserOverride[];
}

// ── BRANCHES (seed, #90) ──────────────────────────────────────────────────────
// A tenant's physical locations. MUTABLE so createBranch could extend it flag-off;
// heads the People "All branches" filter + the "N branches" header stat and the
// manage-panel scope picker. Display-only — a branch never confers permissions.
interface SeedBranch {
  branchId: string;
  name: string;
  code: string | null;
  isActive: boolean;
}

const BRANCHES: SeedBranch[] = [
  { branchId: 'br-1', name: 'Jubilee Hills (Main)', code: 'JH', isActive: true },
  { branchId: 'br-2', name: 'Gachibowli', code: 'GB', isActive: true },
  { branchId: 'br-3', name: 'Secunderabad', code: 'SC', isActive: true },
];

function branchNameById(id: string | null): string | null {
  if (!id) return null;
  return BRANCHES.find((b) => b.branchId === id)?.name ?? null;
}

/** Minutes-ago ISO for relative presence seeds (evaluated once at import). */
const minsAgoIso = (m: number): string => new Date(Date.now() - m * 60_000).toISOString();

const USERS: SeedUser[] = [
  {
    userId: ADMIN_USER_ID, email: DEMO_EMAIL, fullName: 'Priyanka R', phone: '+91 98200 11223',
    isActive: true, mfaEnabled: false, lastLoginAt: '2026-06-14T09:12:00+05:30', lastActivityAt: minsAgoIso(1),
    branchId: 'br-1', department: 'Front desk',
    roleIds: [{ userTenantRoleId: 'utr-1', roleId: 'r-admin', isPrimary: true, expiresAt: null }],
    overrides: [],
  },
  {
    userId: 'u-2', email: 'arjun.sharma@apollocare.in', fullName: 'Dr. Arjun Sharma', phone: '+91 99203 44556',
    isActive: true, mfaEnabled: true, lastLoginAt: '2026-06-13T18:40:00+05:30', lastActivityAt: minsAgoIso(3),
    branchId: 'br-2', department: 'Cardiology OPD',
    roleIds: [{ userTenantRoleId: 'utr-2', roleId: 'r-staff', isPrimary: true, expiresAt: null }],
    overrides: [
      { overrideId: 'ov-1', permissionKey: 'docslot.booking.cancel', isAllowed: false, reason: 'Under review after a wrong-cancel incident (2026-05)', expiresAt: '2026-09-01T00:00:00+05:30' },
    ],
  },
  {
    userId: 'u-3', email: 'meena.r@apollocare.in', fullName: 'Meena R', phone: '+91 98765 77889',
    isActive: true, mfaEnabled: false, lastLoginAt: '2026-06-14T08:02:00+05:30', lastActivityAt: minsAgoIso(42),
    branchId: null, department: null,
    roleIds: [{ userTenantRoleId: 'utr-3', roleId: 'r-viewer', isPrimary: true, expiresAt: '2026-12-31T00:00:00+05:30' }],
    overrides: [],
  },
  {
    userId: 'u-4', email: 'rohit.billing@apollocare.in', fullName: 'Rohit Billing', phone: null,
    isActive: false, mfaEnabled: false, lastLoginAt: null, lastActivityAt: null,
    branchId: 'br-1', department: null,
    roleIds: [{ userTenantRoleId: 'utr-4', roleId: 'r-billing', isPrimary: true, expiresAt: null }],
    overrides: [
      { overrideId: 'ov-2', permissionKey: 'docslot.analytics.read', isAllowed: true, reason: 'Temporary access for the Q2 revenue report', expiresAt: '2026-07-15T00:00:00+05:30' },
    ],
  },
];

function roleById(id: string): SeedRole | undefined {
  return ROLES.find((r) => r.roleId === id);
}

/** #84 — distinct ACTIVE members holding a role: the user must be active AND the
 *  assignment must not be expired (mirrors the server's revoked/expired exclusion).
 *  Revoked assignments aren't modelled in the seed, so active + unexpired is enough. */
function activeMemberCount(roleId: string): number {
  const now = Date.now();
  return USERS.filter(
    (u) =>
      u.isActive &&
      u.roleIds.some(
        (a) => a.roleId === roleId && (a.expiresAt === null || new Date(a.expiresAt).getTime() > now),
      ),
  ).length;
}

/** #85 — effectiveFrom per seeded override (the UserOverride seed shape carries no
 *  start date; the tenant-wide list DTO does). Keyed by overrideId. */
const OVERRIDE_EFFECTIVE_FROM: Record<string, string> = {
  'ov-1': '2026-05-20T00:00:00+05:30',
  'ov-2': '2026-06-25T00:00:00+05:30',
};

function toUserListItem(u: SeedUser): UserListItem {
  return UserListItemSchema.parse({
    userId: u.userId,
    email: u.email,
    fullName: u.fullName,
    maskedPhone: u.phone ? maskPhone(u.phone) : null,
    isActive: u.isActive,
    mfaEnabled: u.mfaEnabled,
    lastLoginAt: u.lastLoginAt,
    lastActivityAt: u.lastActivityAt,
    branchId: u.branchId,
    branchName: branchNameById(u.branchId),
    department: u.department,
    roles: u.roleIds.map((a) => {
      const role = roleById(a.roleId);
      return {
        userTenantRoleId: a.userTenantRoleId,
        roleId: a.roleId,
        roleKey: role?.roleKey ?? 'unknown',
        name: role?.name ?? 'Unknown role',
        isPrimary: a.isPrimary,
        expiresAt: a.expiresAt,
      };
    }),
  });
}

// ── Queries ──────────────────────────────────────────────────────────────────

export function listTenantUsers(): Promise<UserListItem[]> {
  return delay(USERS.map(toUserListItem));
}

/** #90 — active branches for the People "All branches" filter + the "N branches"
 *  header stat + the manage-panel scope picker. */
export function listBranches(): Promise<Branch[]> {
  return delay(BRANCHES.filter((b) => b.isActive).map((b) => BranchSchema.parse(b)));
}

/** #90 — set a member's org scope (DISPLAY-only). Unlike the other user mutations
 *  (no-ops), this MUTATES the seed so the change persists flag-off and the optimistic
 *  UI reconciles against a real value after invalidate. Never touches roles. */
export function setMemberScope(
  userId: string,
  req: SetMemberScopeRequest,
  idempotencyKey: string,
): Promise<SetMemberScopeResult> {
  return withIdem(idempotencyKey, () => {
    const dept = req.department?.trim() ? req.department.trim() : null;
    const u = USERS.find((x) => x.userId === userId);
    if (u) {
      u.branchId = req.branchId ?? null;
      u.department = dept;
    }
    return SetMemberScopeResultSchema.parse({
      userTenantRoleId: u?.roleIds[0]?.userTenantRoleId ?? crypto.randomUUID(),
      branchId: req.branchId ?? null,
      department: dept,
    });
  });
}

export function listRoles(): Promise<Role[]> {
  return delay(ROLES.map((r) => RoleSchema.parse({ ...r, memberCount: activeMemberCount(r.roleId) })));
}

export function getPermissionRegistry(): Promise<PermissionDef[]> {
  return delay(PERMISSION_REGISTRY.map((p) => PermissionDefSchema.parse(p)));
}

export function getRolePermissions(roleId: string): Promise<string[]> {
  return delay(ROLE_GRANTS[roleId] ?? []);
}

export function listUserOverrides(userId: string): Promise<UserOverride[]> {
  const u = USERS.find((x) => x.userId === userId);
  return delay((u?.overrides ?? []).map((o) => UserOverrideSchema.parse(o)));
}

/** #85 — GET /iam/overrides: every per-user override across the tenant, with the
 *  target user's identity inlined (so the list needs no per-row user lookup) and a
 *  server-style `count`. Deny-wins overrides (isAllowed=false) and grants both show;
 *  `active` reflects whether the override applies right now (started, not expired). */
export function listTenantOverrides(): Promise<TenantOverridesList> {
  const now = Date.now();
  const overrides = USERS.flatMap((u) =>
    u.overrides.map((o) => ({
      overrideId: o.overrideId,
      userId: u.userId,
      userDisplayName: u.fullName,
      userEmail: u.email,
      permissionKey: o.permissionKey,
      isAllowed: o.isAllowed,
      reason: o.reason,
      effectiveFrom: OVERRIDE_EFFECTIVE_FROM[o.overrideId] ?? '2026-06-01T00:00:00+05:30',
      expiresAt: o.expiresAt,
      active: o.expiresAt === null || new Date(o.expiresAt).getTime() > now,
    })),
  );
  return delay(TenantOverridesListSchema.parse({ count: overrides.length, overrides }));
}

/** "Why does user X have permission Y" — effective set with its source.
 *  Mirrors platform.v_user_effective_permissions: role grants MINUS deny-overrides
 *  PLUS grant-overrides (deny wins). */
export function getEffectivePermissions(userId: string): Promise<EffectivePermission[]> {
  const u = USERS.find((x) => x.userId === userId);
  if (!u) return delay([]);

  const denied = new Set(u.overrides.filter((o) => !o.isAllowed).map((o) => o.permissionKey));
  const out = new Map<string, EffectivePermission>();

  // Role grants not denied by an override.
  for (const assignment of u.roleIds) {
    const role = roleById(assignment.roleId);
    for (const key of ROLE_GRANTS[assignment.roleId] ?? []) {
      if (denied.has(key)) continue;
      if (!out.has(key)) out.set(key, { permissionKey: key, source: 'role', via: role?.name ?? null });
    }
  }
  // Grant overrides (always win unless also denied — deny wins, but a key can't be
  // both allow and deny in this seed).
  for (const o of u.overrides) {
    if (o.isAllowed) out.set(o.permissionKey, { permissionKey: o.permissionKey, source: 'override_grant', via: null });
  }

  return delay([...out.values()].map((e) => EffectivePermissionSchema.parse(e)));
}

// ── Mutations (Idempotency-Key per logical action) ───────────────────────────

export function createUser(req: CreateUserRequest, idempotencyKey: string): Promise<CreateUserResult> {
  return withIdem(idempotencyKey, () => {
    void req;
    return CreateUserResultSchema.parse({ userId: crypto.randomUUID() });
  });
}

// ── Export + bulk import (#95) ────────────────────────────────────────────────
// Mock parity for GET /users/export + POST /users/bulk-import. The export builds a
// text/csv from the USERS seed with the documented header, CSV-injection-safe
// (RFC-4180 quoting + a leading =,+,-,@,tab,CR neutralised) exactly like the server.
// Bulk import simulates the single-user provisioning path per row: an email matching
// an existing member LINKS, a duplicate WITHIN the batch SKIPS, a malformed row or an
// unknown role ERRORS, everything else is CREATED (and pushed into USERS so the People
// list refresh shows it flag-off).

const IMPORT_EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

/** CSV-injection-safe cell: neutralise a leading formula trigger (=,+,-,@,tab,CR)
 *  with a leading apostrophe, then RFC-4180 quote if it contains a comma, quote,
 *  CR or LF. Mirrors the server's export encoding. */
function csvUserCell(value: string | null | undefined): string {
  let s = value ?? '';
  if (/^[=+\-@\t\r]/.test(s)) s = `'${s}`;
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

function roleByKey(key: string): SeedRole | undefined {
  return ROLES.find((r) => r.roleKey === key);
}

export function exportTenantUsers(): Promise<UserCsvResult> {
  const header = ['full_name', 'email', 'roles', 'branch', 'department', 'status', 'two_factor', 'last_active'];
  const lines = USERS.map((u) => {
    const roles = u.roleIds.map((a) => roleById(a.roleId)?.name ?? a.roleId).join('; ');
    return [
      u.fullName,
      u.email,
      roles,
      branchNameById(u.branchId) ?? '',
      u.department ?? '',
      u.isActive ? 'active' : 'inactive',
      u.mfaEnabled ? 'on' : 'off',
      u.lastActivityAt ?? u.lastLoginAt ?? '',
    ]
      .map(csvUserCell)
      .join(',');
  });
  const content = [header.join(','), ...lines].join('\r\n');
  const stamp = new Date().toISOString().slice(0, 10);
  return delay({ fileName: `team-members-${stamp}.csv`, content });
}

export function bulkImportUsers(
  req: BulkImportUsersRequest,
  idempotencyKey: string,
): Promise<BulkImportResult> {
  return withIdem(idempotencyKey, () => {
    let created = 0;
    let linked = 0;
    let skipped = 0;
    let errored = 0;
    const seenInBatch = new Set<string>();
    const rows = req.rows.map((r, i) => {
      const row = i + 1;
      const emailRaw = r.email.trim();
      const email = emailRaw.toLowerCase();
      const mk = (status: string, message: string) => ({ row, email: emailRaw, status, message });
      if (!IMPORT_EMAIL_RE.test(email) || !r.fullName.trim()) {
        errored += 1;
        return mk('errored', 'Missing or invalid email / name');
      }
      if (seenInBatch.has(email)) {
        skipped += 1;
        return mk('skipped', 'Duplicate row in this file');
      }
      seenInBatch.add(email);
      let roleId: string | null = null;
      if (r.roleKey) {
        const role = roleByKey(r.roleKey);
        if (!role) {
          errored += 1;
          return mk('errored', `Unknown role: ${r.roleKey}`);
        }
        roleId = role.roleId;
      }
      const existing = USERS.find((u) => u.email.toLowerCase() === email);
      if (existing) {
        linked += 1;
        return mk('linked', 'Linked to existing account');
      }
      // Provision a new member so the People list refresh shows it flag-off.
      USERS.push({
        userId: crypto.randomUUID(),
        email: emailRaw,
        fullName: r.fullName.trim(),
        phone: null,
        isActive: true,
        mfaEnabled: false,
        lastLoginAt: null,
        lastActivityAt: null,
        branchId: null,
        department: null,
        roleIds: roleId
          ? [{ userTenantRoleId: crypto.randomUUID(), roleId, isPrimary: true, expiresAt: null }]
          : [],
        overrides: [],
      });
      created += 1;
      return mk('created', 'Account created');
    });
    return BulkImportResultSchema.parse({ total: req.rows.length, created, linked, skipped, errored, rows });
  });
}

export function assignRole(req: AssignRoleRequest, idempotencyKey: string): Promise<AssignRoleResult> {
  return withIdem(idempotencyKey, () => {
    void req;
    return AssignRoleResultSchema.parse({ userTenantRoleId: crypto.randomUUID() });
  });
}

export function revokeRoleAssignment(
  userTenantRoleId: string,
  reason: string,
  idempotencyKey: string,
): Promise<RevokeRoleResult> {
  return withIdem(idempotencyKey, () => {
    void reason;
    return RevokeRoleResultSchema.parse({ userTenantRoleId, alreadyRevoked: false });
  });
}

export function setOverride(req: SetOverrideRequest, idempotencyKey: string): Promise<SetOverrideResult> {
  return withIdem(idempotencyKey, () => {
    void req;
    return SetOverrideResultSchema.parse({ overrideId: crypto.randomUUID() });
  });
}

// User-lifecycle mock stubs — no-op (return the synthetic result shape) like the other
// mock mutations above; flag-off keeps the static user list, so the screens just re-render.
export function setUserActive(
  userId: string,
  req: { isActive: boolean; reason: string },
  idempotencyKey: string,
): Promise<{ userId: string; isActive: boolean }> {
  return withIdem(idempotencyKey, () =>
    SetUserStatusResultSchema.parse({ userId, isActive: req.isActive }),
  );
}

export function updateUser(
  userId: string,
  req: { fullName: string; phone?: string | null; preferredLanguage: 'en' | 'hi' },
  idempotencyKey: string,
): Promise<{ userId: string }> {
  return withIdem(idempotencyKey, () => {
    void req;
    return UpdateUserProfileResultSchema.parse({ userId });
  });
}

export function resetUserAccess(
  userId: string,
  reason: string,
  idempotencyKey: string,
): Promise<{ userId: string }> {
  return withIdem(idempotencyKey, () => {
    void reason;
    return ResetAccessResultSchema.parse({ userId });
  });
}

export interface CreateRoleRequest {
  name: string;
  roleKey: string;
  permissionKeys: string[];
}

export function createRole(req: CreateRoleRequest, idempotencyKey: string): Promise<Role> {
  return withIdem(idempotencyKey, () =>
    RoleSchema.parse({
      roleId: crypto.randomUUID(),
      roleKey: req.roleKey,
      name: req.name,
      scope: 'tenant',
      isSystem: false,
      tenantId: TENANT_ID,
    }),
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// IAM — privilege matrix (Slice 2). The mock derives modules + cells from the
// existing PERMISSION_REGISTRY / ROLE_GRANTS seed so the flag-off grid stays
// internally consistent with the (already-seeded) role list. Grants made through
// the mock toggle endpoints mutate an in-memory overlay (MOCK_ROLE_GRANTS) so
// the optimistic UI reconciles against a real change without a backend.
// ─────────────────────────────────────────────────────────────────────────────

// Module metadata: resource → display name + bilingual-neutral description +
// order. Resources without an entry fall back to a title-cased resource name.
// MUTABLE so the catalog-create mocks (createModule / createPermission) can add a
// new module/permission and have listModules() + getRoleMatrix() reflect it
// flag-off (a new permission surfaces as a matrix cell under its module).
const MODULE_META: Record<string, { name: string; description: string; order: number }> = {
  booking: { name: 'Bookings', description: 'Appointment booking and lifecycle', order: 10 },
  patient: { name: 'Patients', description: 'Patient directory and registration', order: 20 },
  doctor: { name: 'Doctors', description: 'Practitioner directory', order: 30 },
  analytics: { name: 'Analytics', description: 'Dashboards and reports', order: 40 },
  users: { name: 'Users', description: 'Team members in this tenant', order: 50 },
  roles: { name: 'Roles', description: 'Role assignment', order: 60 },
  overrides: { name: 'Overrides', description: 'Per-user permission overrides', order: 70 },
  settings: { name: 'Settings', description: 'Tenant configuration', order: 80 },
  billing: { name: 'Billing', description: 'Invoices and subscription', order: 90 },
  audit: { name: 'Audit', description: 'Audit trail access', order: 100 },
};

// Human action labels for cells.
const ACTION_NAME: Record<string, string> = {
  read: 'View',
  create: 'Create',
  update: 'Edit',
  remove: 'Delete',
  delete: 'Delete',
  approve: 'Approve',
  cancel: 'Cancel',
  assign: 'Assign',
  grant: 'Grant',
  export: 'Export',
};

// Stable synthetic permissionId per key (the registry has no ids; the matrix
// toggle endpoints address cells by id, so we need a deterministic one).
function permIdFor(key: string): string {
  return `perm-${key}`;
}

function moduleMetaFor(resource: string): { name: string; description: string; order: number } {
  return (
    MODULE_META[resource] ?? {
      name: resource.charAt(0).toUpperCase() + resource.slice(1),
      description: `${resource} permissions`,
      order: 999,
    }
  );
}

// The set of registered module resourceKeys. Seeded from the permission registry's
// resources, plus any standalone module created via createModule (a module can
// exist before it has any permissions). Recomputed on each read so catalog-create
// mocks take effect flag-off.
const EXTRA_MODULES = new Set<string>();

function resourcesInOrder(): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const p of PERMISSION_REGISTRY) {
    if (!seen.has(p.resource)) {
      seen.add(p.resource);
      out.push(p.resource);
    }
  }
  for (const r of EXTRA_MODULES) {
    if (!seen.has(r)) {
      seen.add(r);
      out.push(r);
    }
  }
  return out.sort((a, b) => moduleMetaFor(a).order - moduleMetaFor(b).order);
}

// Mutable grant overlay so mock toggles take effect (seeded from ROLE_GRANTS).
const MOCK_ROLE_GRANTS: Record<string, Set<string>> = (() => {
  const out: Record<string, Set<string>> = {};
  for (const [roleId, keys] of Object.entries(ROLE_GRANTS)) out[roleId] = new Set(keys);
  return out;
})();

export function listModules(): Promise<ModuleDto[]> {
  const mods = resourcesInOrder().map((resource) => {
    const meta = moduleMetaFor(resource);
    return ModuleDtoSchema.parse({
      resourceKey: resource,
      name: meta.name,
      description: meta.description,
      displayOrder: meta.order,
      licensed: true,
    });
  });
  return delay(mods);
}

export function listIamPermissions(module?: string): Promise<IamPermissionDto[]> {
  const defs = module ? PERMISSION_REGISTRY.filter((p) => p.resource === module) : PERMISSION_REGISTRY;
  return delay(
    defs.map((p) =>
      IamPermissionDtoSchema.parse({
        permissionId: permIdFor(p.permissionKey),
        permissionKey: p.permissionKey,
        resource: p.resource,
        action: p.action,
        scope: p.scope,
        isDangerous: p.isDangerous,
        description: p.description,
      }),
    ),
  );
}

export function getRoleMatrix(roleId: string): Promise<RoleMatrix> {
  const role = roleById(roleId);
  // 404 parity with the live API. The messageKey is never shown (the panel renders
  // its generic error state on isError); it just mirrors the not-found status.
  if (!role) return Promise.reject(new MockApiError(404, 'error.genericTitle'));

  const granted = MOCK_ROLE_GRANTS[roleId] ?? new Set<string>();
  let grantedTotal = 0;
  let total = 0;

  const modules = resourcesInOrder().map((resource) => {
    const meta = moduleMetaFor(resource);
    const defs = PERMISSION_REGISTRY.filter((p) => p.resource === resource);
    let modGranted = 0;
    const cells = defs.map((p) => {
      const isGranted = granted.has(p.permissionKey);
      if (isGranted) modGranted += 1;
      return {
        permissionId: permIdFor(p.permissionKey),
        permissionKey: p.permissionKey,
        action: p.action,
        actionName: ACTION_NAME[p.action] ?? p.action,
        isDangerous: p.isDangerous,
        granted: isGranted,
        moduleLicensed: true,
      };
    });
    grantedTotal += modGranted;
    total += defs.length;
    return {
      resourceKey: resource,
      name: meta.name,
      description: meta.description,
      displayOrder: meta.order,
      licensed: true,
      grantedCount: modGranted,
      totalCount: defs.length,
      cells,
    };
  });

  return delay(
    RoleMatrixSchema.parse({
      roleId: role.roleId,
      roleKey: role.roleKey,
      name: role.name,
      description: null,
      scope: role.scope,
      isSystem: role.isSystem,
      // System roles are read-only; custom (tenant-scoped) roles are editable.
      editable: !role.isSystem,
      grantedCount: grantedTotal,
      totalCount: total,
      modules,
    }),
  );
}

// permissionId is `perm-<key>` in the mock — recover the key to mutate the overlay.
function keyForPermId(permissionId: string): string | undefined {
  return PERMISSION_REGISTRY.find((p) => permIdFor(p.permissionKey) === permissionId)?.permissionKey;
}

export function grantRolePermission(
  roleId: string,
  permissionId: string,
  idempotencyKey: string,
): Promise<RolePermissionToggleResult> {
  return withIdem(idempotencyKey, () => {
    const role = roleById(roleId);
    if (role && !role.isSystem) {
      const key = keyForPermId(permissionId);
      if (key) (MOCK_ROLE_GRANTS[roleId] ??= new Set<string>()).add(key);
    }
    return RolePermissionToggleResultSchema.parse({ roleId, permissionId, granted: true });
  });
}

export function revokeRolePermission(
  roleId: string,
  permissionId: string,
  idempotencyKey: string,
): Promise<RolePermissionToggleResult> {
  return withIdem(idempotencyKey, () => {
    const role = roleById(roleId);
    if (role && !role.isSystem) {
      const key = keyForPermId(permissionId);
      if (key) MOCK_ROLE_GRANTS[roleId]?.delete(key);
    }
    return RolePermissionToggleResultSchema.parse({ roleId, permissionId, granted: false });
  });
}

export function duplicateRole(req: DuplicateRoleRequest, idempotencyKey: string): Promise<DuplicateRoleResult> {
  return withIdem(idempotencyKey, () => {
    const newId = `r-${crypto.randomUUID().slice(0, 8)}`;
    // Append a new editable role cloning the source's grants so a subsequent
    // getRoleMatrix(newId) renders the cloned, now-editable matrix.
    const source = roleById(req.sourceRoleId);
    ROLES.push({
      roleId: newId,
      roleKey: req.newRoleKey,
      name: req.newName,
      scope: source?.scope ?? 'tenant',
      isSystem: false,
      tenantId: TENANT_ID,
    });
    MOCK_ROLE_GRANTS[newId] = new Set(MOCK_ROLE_GRANTS[req.sourceRoleId] ?? []);
    return DuplicateRoleResultSchema.parse({ roleId: newId });
  });
}

export function getEffectiveAccess(userId: string, _tenantId?: string | null): Promise<EffectiveAccess> {
  void _tenantId;
  const u = USERS.find((x) => x.userId === userId);
  const denied = new Set((u?.overrides ?? []).filter((o) => !o.isAllowed).map((o) => o.permissionKey));
  const keys = new Set<string>();
  for (const assignment of u?.roleIds ?? []) {
    for (const key of MOCK_ROLE_GRANTS[assignment.roleId] ?? []) {
      if (!denied.has(key)) keys.add(key);
    }
  }
  for (const o of u?.overrides ?? []) {
    if (o.isAllowed && !denied.has(o.permissionKey)) keys.add(o.permissionKey);
  }
  return delay(
    EffectiveAccessSchema.parse({
      userId,
      tenantId: TENANT_ID,
      permissionKeys: [...keys].sort(),
    }),
  );
}

// ── Catalog-plane creates (platform.permissions.manage) ──────────────────────
// Mutate the in-memory catalog so the flag-off app reflects the new module /
// permission: a new module appears in listModules() (and as an empty matrix
// section once it has permissions); a new permission appears as a matrix cell
// under its module. Idempotency-Key de-dupes a retry.

export function createModule(req: CreateModuleRequest, idempotencyKey: string): Promise<CreateModuleResult> {
  return withIdem(idempotencyKey, () => {
    // Register the module's display metadata + mark the resourceKey known so
    // listModules() emits it even before it has any permissions.
    MODULE_META[req.resourceKey] = {
      name: req.name,
      description: req.description?.trim() ? req.description.trim() : `${req.name} permissions`,
      order: req.displayOrder ?? 500,
    };
    EXTRA_MODULES.add(req.resourceKey);
    return CreateModuleResultSchema.parse({ resourceTypeId: `rt-${req.resourceKey}` });
  });
}

export function createPermission(
  req: CreatePermissionRequest,
  idempotencyKey: string,
): Promise<CreatePermissionResult> {
  return withIdem(idempotencyKey, () => {
    // Append to the registry so getRoleMatrix() renders it as a (revocable) cell
    // under its module, and listIamPermissions(module) returns it. The module is
    // implicitly registered too (in case the permission names a brand-new module).
    EXTRA_MODULES.add(req.resource);
    if (!PERMISSION_REGISTRY.some((p) => p.permissionKey === req.permissionKey)) {
      PERMISSION_REGISTRY.push({
        permissionKey: req.permissionKey,
        resource: req.resource,
        action: req.action,
        scope: req.scope,
        description: req.description,
        isDangerous: req.isDangerous ?? false,
      });
    }
    return CreatePermissionResultSchema.parse({ permissionId: permIdFor(req.permissionKey) });
  });
}
