// Team & Roles feature: users, roles, the privilege matrix, permission registry,
// overrides, and the effective-access viewer. Co-located per feature-folder rule.
// Mutations take a stable Idempotency-Key generated once per action by the caller.
//
// Every data fn now has a LIVE .NET implementation and is imported from
// '@/lib/backend' (each switches live/mock by VITE_USE_REAL_API): the whole IAM
// surface (listRoles, listTenantUsers, getRoleMatrix, listModules,
// listIamPermissions, grant/revokeRolePermission, duplicateRole,
// getEffectiveAccess, createUser/assignRole/createRole, setOverride) plus the
// registry + role-grants explainer reads (getPermissionRegistry, getRolePermissions,
// listUserOverrides, getEffectivePermissions). Only the CreateRoleRequest TYPE is
// imported from '@/lib/mock' (the shared contract shape, no runtime).

import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  assignRole,
  createInvitation,
  createModule,
  exportAuditLog,
  listActiveSessions,
  listAuditLog,
  listBranches,
  listInvitations,
  resendInvitation,
  revokeAllSessions,
  revokeInvitation,
  revokeSession,
  createPermission,
  createRole,
  createUser,
  duplicateRole,
  getEffectiveAccess,
  getEffectivePermissions,
  getPermissionRegistry,
  getRoleMatrix,
  getRolePermissions,
  grantRolePermission,
  listIamPermissions,
  listModules,
  listRoles,
  listTenantOverrides,
  listTenantUsers,
  listUserOverrides,
  resetUserAccess,
  revokeRoleAssignment,
  revokeRolePermission,
  setMemberScope,
  setOverride,
  setUserActive,
  updateUser,
} from '@/lib/backend';
import { type CreateRoleRequest } from '@/lib/mock';
import type {
  ActiveSession,
  AssignRoleRequest,
  AuditLogFilter,
  CreateInvitationRequest,
  CreateModuleRequest,
  CreatePermissionRequest,
  CreateUserRequest,
  DuplicateRoleRequest,
  InvitationList,
  RoleMatrix,
  SetMemberScopeRequest,
  SetOverrideRequest,
  SetUserStatusRequest,
  UpdateUserProfileRequest,
  UserListItem,
} from '@/lib/mock/contracts';

export const usersQueryKey = ['team', 'users'] as const;
export const rolesQueryKey = ['team', 'roles'] as const;
export const tenantOverridesQueryKey = ['team', 'tenantOverrides'] as const;
export const permissionRegistryQueryKey = ['team', 'permissions'] as const;
export const modulesQueryKey = ['team', 'modules'] as const;
export const roleMatrixQueryKey = (roleId: string | undefined) => ['team', 'roleMatrix', roleId] as const;
export const effectiveAccessQueryKey = (userId: string | undefined) => ['team', 'effectiveAccess', userId] as const;

export function useTenantUsers() {
  return useQuery({ queryKey: usersQueryKey, queryFn: listTenantUsers });
}

// ── BRANCHES + MEMBERSHIP SCOPE (#90) ─────────────────────────────────────────
export const branchesQueryKey = ['team', 'branches'] as const;

/** Active branches for the People "All branches" filter, the "N branches" header
 *  stat, and the manage-panel scope picker. Gated `tenant.users.read` server-side;
 *  pass `enabled=false` when the caller lacks it so we never fire a 403. Session-stable. */
export function useBranches(enabled = true) {
  return useQuery({ queryKey: branchesQueryKey, queryFn: listBranches, enabled, staleTime: 5 * 60_000 });
}

/**
 * Set a member's org scope (branch + department) — DISPLAY only, never role. The
 * change is applied OPTIMISTICALLY to the cached users list (the row's branch/
 * department flip instantly), rolled back on error, then reconciled on settle via a
 * users-list invalidate (server state wins). `branchName` is threaded so the optimistic
 * patch shows the picked branch's label immediately without waiting for the refetch.
 */
export function useSetMemberScope() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: SetMemberScopeRequest & { userId: string; branchName: string | null; idempotencyKey: string }) =>
      setMemberScope(vars.userId, { branchId: vars.branchId, department: vars.department }, vars.idempotencyKey),
    onMutate: async (vars) => {
      await qc.cancelQueries({ queryKey: usersQueryKey });
      const previous = qc.getQueryData<UserListItem[]>(usersQueryKey);
      qc.setQueryData<UserListItem[]>(usersQueryKey, (old) =>
        old?.map((u) =>
          u.userId === vars.userId
            ? { ...u, branchId: vars.branchId, branchName: vars.branchName, department: vars.department }
            : u,
        ),
      );
      return { previous };
    },
    onError: (_e, _vars, ctx) => {
      if (ctx?.previous) qc.setQueryData(usersQueryKey, ctx.previous);
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: usersQueryKey }),
  });
}

export function useRoles() {
  return useQuery({ queryKey: rolesQueryKey, queryFn: listRoles });
}

export function usePermissionRegistry() {
  return useQuery({ queryKey: permissionRegistryQueryKey, queryFn: getPermissionRegistry, staleTime: Infinity });
}

/** The licensable module list (matrix section metadata). Session-stable. */
export function useModules() {
  return useQuery({ queryKey: modulesQueryKey, queryFn: () => listModules(), staleTime: Infinity });
}

/** The permission registry for one module (drill-down / future module detail).
 *  Omitting `module` returns the full registry. Session-stable. */
export function useIamPermissions(module?: string) {
  return useQuery({
    queryKey: ['team', 'iamPermissions', module ?? 'all'] as const,
    queryFn: () => listIamPermissions(module),
    staleTime: Infinity,
  });
}

/** The privilege matrix for a role — the heart of the Roles screen. */
export function useRoleMatrix(roleId: string | undefined) {
  return useQuery({
    queryKey: roleMatrixQueryKey(roleId),
    queryFn: () => getRoleMatrix(roleId ?? ''),
    enabled: Boolean(roleId),
  });
}

export function useRolePermissions(roleId: string | undefined) {
  return useQuery({
    queryKey: ['team', 'rolePermissions', roleId] as const,
    queryFn: () => getRolePermissions(roleId ?? ''),
    enabled: Boolean(roleId),
  });
}

export function useUserOverrides(userId: string | undefined) {
  return useQuery({
    queryKey: ['team', 'overrides', userId] as const,
    queryFn: () => listUserOverrides(userId ?? ''),
    enabled: Boolean(userId),
  });
}

/** Tenant-wide per-user overrides (#85) — feeds the Roles & permissions "Per-user
 *  overrides" sub-tab list AND its badge count. Gated on platform.overrides.read;
 *  pass `enabled=false` when the caller lacks it so we never fire a 403. */
export function useTenantOverrides(enabled = true) {
  return useQuery({ queryKey: tenantOverridesQueryKey, queryFn: listTenantOverrides, enabled });
}

export function useEffectivePermissions(userId: string | undefined) {
  return useQuery({
    queryKey: ['team', 'effective', userId] as const,
    queryFn: () => getEffectivePermissions(userId ?? ''),
    enabled: Boolean(userId),
  });
}

/** Resolved effective permission-key set (role grants − denies + grants). */
export function useEffectiveAccess(userId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: effectiveAccessQueryKey(userId),
    queryFn: () => getEffectiveAccess(userId ?? ''),
    enabled: Boolean(userId) && enabled,
  });
}

export interface CreateUserInput extends CreateUserRequest {
  idempotencyKey: string;
}
export function useCreateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateUserInput) => createUser(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: usersQueryKey }),
  });
}

// ── User lifecycle: deactivate/reactivate, edit profile, reset access ─────────
/** Deactivate (revoke memberships in this tenant) or reactivate a user. Invalidates the
 *  users list + that user's effective access (status changes what they can do). */
export function useSetUserStatus() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: SetUserStatusRequest & { userId: string; idempotencyKey: string }) =>
      setUserActive(vars.userId, { isActive: vars.isActive, reason: vars.reason }, vars.idempotencyKey),
    onSuccess: (_r, vars) => {
      void qc.invalidateQueries({ queryKey: usersQueryKey });
      void qc.invalidateQueries({ queryKey: ['team', 'effective', vars.userId] });
      void qc.invalidateQueries({ queryKey: effectiveAccessQueryKey(vars.userId) });
    },
  });
}

/** Edit a user's profile (name / phone / language). Invalidates the users list. */
export function useUpdateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: UpdateUserProfileRequest & { userId: string; idempotencyKey: string }) =>
      updateUser(
        vars.userId,
        { fullName: vars.fullName, phone: vars.phone ?? null, preferredLanguage: vars.preferredLanguage },
        vars.idempotencyKey,
      ),
    onSuccess: () => void qc.invalidateQueries({ queryKey: usersQueryKey }),
  });
}

/** Reset/unlock a user's access (force password change + clear lockout). Invalidates the
 *  users list so the locked/pending-reset chips refresh. */
export function useResetUserAccess() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { userId: string; reason: string; idempotencyKey: string }) =>
      resetUserAccess(vars.userId, vars.reason, vars.idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: usersQueryKey }),
  });
}

export interface AssignRoleInput extends AssignRoleRequest {
  idempotencyKey: string;
}
export function useAssignRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: AssignRoleInput) => assignRole(req, idempotencyKey),
    onSuccess: (_r, vars) => {
      void qc.invalidateQueries({ queryKey: usersQueryKey });
      void qc.invalidateQueries({ queryKey: ['team', 'effective', vars.userId] });
      void qc.invalidateQueries({ queryKey: effectiveAccessQueryKey(vars.userId) });
    },
  });
}

/** Soft-revoke a role assignment. The user's row updates (users query invalidates);
 *  `userId` is threaded through only to invalidate that user's effective-access. */
export function useRevokeRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { userTenantRoleId: string; reason: string; userId: string; idempotencyKey: string }) =>
      revokeRoleAssignment(vars.userTenantRoleId, vars.reason, vars.idempotencyKey),
    onSuccess: (_r, vars) => {
      void qc.invalidateQueries({ queryKey: usersQueryKey });
      void qc.invalidateQueries({ queryKey: ['team', 'effective', vars.userId] });
      void qc.invalidateQueries({ queryKey: effectiveAccessQueryKey(vars.userId) });
    },
  });
}

export interface SetOverrideInput extends SetOverrideRequest {
  idempotencyKey: string;
}
export function useSetOverride() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: SetOverrideInput) => setOverride(req, idempotencyKey),
    onSuccess: (_r, vars) => {
      void qc.invalidateQueries({ queryKey: ['team', 'overrides', vars.userId] });
      void qc.invalidateQueries({ queryKey: ['team', 'effective', vars.userId] });
      void qc.invalidateQueries({ queryKey: effectiveAccessQueryKey(vars.userId) });
    },
  });
}

export interface CreateRoleInput extends CreateRoleRequest {
  idempotencyKey: string;
}
export function useCreateRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateRoleInput) => createRole(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: rolesQueryKey }),
  });
}

// ── Privilege matrix mutations ───────────────────────────────────────────────

export interface ToggleCellInput {
  roleId: string;
  permissionId: string;
  /** next desired state: true → grant (POST), false → revoke (DELETE). */
  granted: boolean;
  idempotencyKey: string;
}

/**
 * Toggle a single matrix cell. ON → POST grant, OFF → DELETE revoke. The caller
 * applies the optimistic flip (useOptimistic in the panel); this hook invalidates
 * the matrix on settle so it reconciles with the server (and rolls back on 403).
 */
export function useToggleRolePermission() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ roleId, permissionId, granted, idempotencyKey }: ToggleCellInput) =>
      granted
        ? grantRolePermission(roleId, permissionId, idempotencyKey)
        : revokeRolePermission(roleId, permissionId, idempotencyKey),
    onSettled: (_r, _e, vars) => {
      void qc.invalidateQueries({ queryKey: roleMatrixQueryKey(vars.roleId) });
    },
  });
}

// ── Catalog-plane creates (platform.permissions.manage) ──────────────────────
// "+ Module" / "+ Permission" define NEW catalog entries (platform-governed),
// distinct from the assignment-plane grants above. On success both invalidate the
// modules list and every role matrix (a new permission surfaces as a matrix cell
// under its module; a new module appears as a section), plus the permission
// registry / iam-permission reads.

/** Invalidate every catalog-derived query so a freshly-created module/permission
 *  shows up immediately (the role matrix is keyed per-role → invalidate by prefix). */
function invalidateCatalog(qc: ReturnType<typeof useQueryClient>) {
  void qc.invalidateQueries({ queryKey: modulesQueryKey });
  void qc.invalidateQueries({ queryKey: permissionRegistryQueryKey });
  void qc.invalidateQueries({ queryKey: ['team', 'iamPermissions'] });
  void qc.invalidateQueries({ queryKey: ['team', 'roleMatrix'] });
}

export interface CreateModuleInput extends CreateModuleRequest {
  idempotencyKey: string;
}
export function useCreateModule() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateModuleInput) => createModule(req, idempotencyKey),
    onSuccess: () => invalidateCatalog(qc),
  });
}

export interface CreatePermissionInput extends CreatePermissionRequest {
  idempotencyKey: string;
}
export function useCreatePermission() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreatePermissionInput) => createPermission(req, idempotencyKey),
    onSuccess: () => invalidateCatalog(qc),
  });
}

export interface DuplicateRoleInput extends DuplicateRoleRequest {
  idempotencyKey: string;
}
export function useDuplicateRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: DuplicateRoleInput) => duplicateRole(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: rolesQueryKey }),
  });
}

// ── AUDIT LOG (#86) ──────────────────────────────────────────────────────────
// The timeline is a paged, faceted read. keepPreviousData keeps the current page
// on-screen while the next page / a changed filter loads (no skeleton flash on
// paginate). The CSV export is a MUTATION (a side-effecting fetch that returns
// {fileName, content}); the caller triggers the download and discards the payload
// — it is never written into any query cache.

export const sessionsQueryKey = ['team', 'sessions'] as const;
export const auditQueryKey = (filter: AuditLogFilter) => ['team', 'audit', filter] as const;

export function useAuditLog(filter: AuditLogFilter) {
  return useQuery({
    queryKey: auditQueryKey(filter),
    queryFn: () => listAuditLog(filter),
    placeholderData: keepPreviousData,
  });
}

/** Export the current filter to CSV. Returns {fileName, content}; the caller
 *  triggers the browser download. Page/pageSize are ignored server-side (export is
 *  the whole filtered set). */
export function useExportAuditLog() {
  return useMutation({ mutationFn: (filter: AuditLogFilter) => exportAuditLog(filter) });
}

// ── ACTIVE SESSIONS (#87) ────────────────────────────────────────────────────
export function useActiveSessions() {
  return useQuery({ queryKey: sessionsQueryKey, queryFn: () => listActiveSessions() });
}

/** Revoke a single session. The panel shows an INSTANT drop via useOptimistic; on
 *  success we surgically remove the row from the cache (no invalidate → no refetch
 *  flash, so the optimistic overlay and the base state agree). On error the
 *  optimistic revert restores the row and the caller toasts. */
export function useRevokeSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { sessionId: string; idempotencyKey: string }) =>
      revokeSession(vars.sessionId, vars.idempotencyKey),
    onSuccess: (_r, vars) => {
      qc.setQueryData<ActiveSession[]>(sessionsQueryKey, (old) =>
        old ? old.filter((s) => s.sessionId !== vars.sessionId) : old,
      );
    },
  });
}

/** Sign a user out of every active session. Surgically drops that user's rows from
 *  the sessions cache and invalidates the users list (the People "Online" dot reads
 *  the same activity signal). */
export function useRevokeAllSessions() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { userId: string; idempotencyKey: string }) =>
      revokeAllSessions(vars.userId, vars.idempotencyKey),
    onSuccess: (_r, vars) => {
      qc.setQueryData<ActiveSession[]>(sessionsQueryKey, (old) =>
        old ? old.filter((s) => s.userId !== vars.userId) : old,
      );
      void qc.invalidateQueries({ queryKey: usersQueryKey });
    },
  });
}

// ── INVITATIONS (#89) ────────────────────────────────────────────────────────
// Token-based tenant onboarding, ALONGSIDE the direct-add invite. The list is
// gated on tenant.users.read; create/resend/revoke on tenant.users.create (the
// tab/panel do the in-memory can() gate). create/resend return a ONE-TIME token
// the caller reveals — it is NEVER written into any query cache. revoke/resend
// surgically patch the pending list (no refetch flash: the optimistic overlay in
// the tab and the base cache agree).

export const invitationsQueryKey = (status?: string) =>
  ['team', 'invitations', status ?? 'all'] as const;

/** Pending invitations for the Invites tab + its badge count. Pass `enabled=false`
 *  when the caller lacks tenant.users.read so we never fire a 403. */
export function useInvitations(status = 'pending', enabled = true) {
  return useQuery({
    queryKey: invitationsQueryKey(status),
    queryFn: () => listInvitations(status),
    enabled,
  });
}

export interface CreateInvitationInput extends CreateInvitationRequest {
  idempotencyKey: string;
}
/** Mint an invitation. Returns the one-time token to the caller (revealed in the
 *  panel, never cached). Invalidates the invitations list so the new pending row
 *  appears with its real server fields. */
export function useCreateInvitation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateInvitationInput) =>
      createInvitation({ email: req.email, roleId: req.roleId ?? null }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['team', 'invitations'] }),
  });
}

/** Resend — rotate the token + extend expiry, bumping resend_count. Returns a NEW
 *  one-time token (revealed by the caller). Surgically patches the pending row so
 *  its expiry/resend count refresh without a refetch flash. */
export function useResendInvitation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { invitationId: string; idempotencyKey: string }) =>
      resendInvitation(vars.invitationId, vars.idempotencyKey),
    onSuccess: (res) => {
      qc.setQueryData<InvitationList>(invitationsQueryKey('pending'), (old) =>
        old
          ? {
              ...old,
              items: old.items.map((i) =>
                i.invitationId === res.invitationId
                  ? { ...i, resendCount: res.resendCount, expiresAt: res.expiresAt }
                  : i,
              ),
            }
          : old,
      );
    },
  });
}

/** Revoke a pending invitation. Surgically drops the now-non-pending row from the
 *  pending list + decrements the count (no refetch → optimistic overlay + base agree). */
export function useRevokeInvitation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { invitationId: string; idempotencyKey: string }) =>
      revokeInvitation(vars.invitationId, vars.idempotencyKey),
    onSuccess: (res) => {
      qc.setQueryData<InvitationList>(invitationsQueryKey('pending'), (old) => {
        if (!old) return old;
        const items = old.items.filter((i) => i.invitationId !== res.invitationId);
        return { items, count: items.length };
      });
    },
  });
}

export type { RoleMatrix };
