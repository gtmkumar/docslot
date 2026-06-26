// Team & Roles feature: users, roles, the privilege matrix, permission registry,
// overrides, and the effective-access viewer. Co-located per feature-folder rule.
// Mutations take a stable Idempotency-Key generated once per action by the caller.
//
// Data fns that have a LIVE .NET implementation are imported from '@/lib/backend'
// (they switch live/mock by VITE_USE_REAL_API): listRoles, listTenantUsers,
// setOverride, and the whole IAM matrix surface (getRoleMatrix, listModules,
// listIamPermissions, grant/revokeRolePermission, duplicateRole,
// getEffectiveAccess). Fns not yet wired live (createUser/assignRole/createRole,
// the registry + role-grants explainer reads) keep importing '@/lib/mock'.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  duplicateRole,
  getEffectiveAccess,
  getRoleMatrix,
  grantRolePermission,
  listIamPermissions,
  listModules,
  listRoles,
  listTenantUsers,
  revokeRolePermission,
  setOverride,
} from '@/lib/backend';
import {
  assignRole,
  createRole,
  createUser,
  getEffectivePermissions,
  getPermissionRegistry,
  getRolePermissions,
  listUserOverrides,
  type CreateRoleRequest,
} from '@/lib/mock';
import type {
  AssignRoleRequest,
  CreateUserRequest,
  DuplicateRoleRequest,
  RoleMatrix,
  SetOverrideRequest,
} from '@/lib/mock/contracts';

export const usersQueryKey = ['team', 'users'] as const;
export const rolesQueryKey = ['team', 'roles'] as const;
export const permissionRegistryQueryKey = ['team', 'permissions'] as const;
export const modulesQueryKey = ['team', 'modules'] as const;
export const roleMatrixQueryKey = (roleId: string | undefined) => ['team', 'roleMatrix', roleId] as const;
export const effectiveAccessQueryKey = (userId: string | undefined) => ['team', 'effectiveAccess', userId] as const;

export function useTenantUsers() {
  return useQuery({ queryKey: usersQueryKey, queryFn: listTenantUsers });
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

export type { RoleMatrix };
