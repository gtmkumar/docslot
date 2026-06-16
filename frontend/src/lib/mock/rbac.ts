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
  CreateUserResultSchema,
  EffectivePermissionSchema,
  MeSchema,
  PermissionDefSchema,
  RoleSchema,
  SetOverrideResultSchema,
  TokenResponseSchema,
  UserListItemSchema,
  UserOverrideSchema,
  type AssignRoleRequest,
  type AssignRoleResult,
  type CreateUserRequest,
  type CreateUserResult,
  type EffectivePermission,
  type LoginRequest,
  type Me,
  type PermissionDef,
  type Role,
  type SetOverrideRequest,
  type SetOverrideResult,
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

const ROLES: Role[] = [
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
  roleIds: { userTenantRoleId: string; roleId: string; isPrimary: boolean; expiresAt: string | null }[];
  overrides: UserOverride[];
}

const USERS: SeedUser[] = [
  {
    userId: ADMIN_USER_ID, email: DEMO_EMAIL, fullName: 'Priyanka R', phone: '+91 98200 11223',
    isActive: true, mfaEnabled: false, lastLoginAt: '2026-06-14T09:12:00+05:30',
    roleIds: [{ userTenantRoleId: 'utr-1', roleId: 'r-admin', isPrimary: true, expiresAt: null }],
    overrides: [],
  },
  {
    userId: 'u-2', email: 'arjun.sharma@apollocare.in', fullName: 'Dr. Arjun Sharma', phone: '+91 99203 44556',
    isActive: true, mfaEnabled: true, lastLoginAt: '2026-06-13T18:40:00+05:30',
    roleIds: [{ userTenantRoleId: 'utr-2', roleId: 'r-staff', isPrimary: true, expiresAt: null }],
    overrides: [
      { overrideId: 'ov-1', permissionKey: 'docslot.booking.cancel', isAllowed: false, reason: 'Under review after a wrong-cancel incident (2026-05)', expiresAt: '2026-09-01T00:00:00+05:30' },
    ],
  },
  {
    userId: 'u-3', email: 'meena.r@apollocare.in', fullName: 'Meena R', phone: '+91 98765 77889',
    isActive: true, mfaEnabled: false, lastLoginAt: '2026-06-14T08:02:00+05:30',
    roleIds: [{ userTenantRoleId: 'utr-3', roleId: 'r-viewer', isPrimary: true, expiresAt: '2026-12-31T00:00:00+05:30' }],
    overrides: [],
  },
  {
    userId: 'u-4', email: 'rohit.billing@apollocare.in', fullName: 'Rohit Billing', phone: null,
    isActive: false, mfaEnabled: false, lastLoginAt: null,
    roleIds: [{ userTenantRoleId: 'utr-4', roleId: 'r-billing', isPrimary: true, expiresAt: null }],
    overrides: [
      { overrideId: 'ov-2', permissionKey: 'docslot.analytics.read', isAllowed: true, reason: 'Temporary access for the Q2 revenue report', expiresAt: '2026-07-15T00:00:00+05:30' },
    ],
  },
];

function roleById(id: string): Role | undefined {
  return ROLES.find((r) => r.roleId === id);
}

function toUserListItem(u: SeedUser): UserListItem {
  return UserListItemSchema.parse({
    userId: u.userId,
    email: u.email,
    fullName: u.fullName,
    maskedPhone: u.phone ? maskPhone(u.phone) : null,
    isActive: u.isActive,
    mfaEnabled: u.mfaEnabled,
    lastLoginAt: u.lastLoginAt,
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

export function listRoles(): Promise<Role[]> {
  return delay(ROLES.map((r) => RoleSchema.parse(r)));
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

export function assignRole(req: AssignRoleRequest, idempotencyKey: string): Promise<AssignRoleResult> {
  return withIdem(idempotencyKey, () => {
    void req;
    return AssignRoleResultSchema.parse({ userTenantRoleId: crypto.randomUUID() });
  });
}

export function setOverride(req: SetOverrideRequest, idempotencyKey: string): Promise<SetOverrideResult> {
  return withIdem(idempotencyKey, () => {
    void req;
    return SetOverrideResultSchema.parse({ overrideId: crypto.randomUUID() });
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
