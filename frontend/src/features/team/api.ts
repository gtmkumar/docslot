// Team & Roles feature: users, roles, permission registry, overrides, and the
// effective-permission explainer. Co-located per feature-folder rule. Mutations
// take a stable Idempotency-Key generated once per action by the caller.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  assignRole,
  createRole,
  createUser,
  getEffectivePermissions,
  getPermissionRegistry,
  getRolePermissions,
  listRoles,
  listTenantUsers,
  listUserOverrides,
  setOverride,
  type CreateRoleRequest,
} from '@/lib/mock';
import type {
  AssignRoleRequest,
  CreateUserRequest,
  SetOverrideRequest,
} from '@/lib/mock/contracts';

export const usersQueryKey = ['team', 'users'] as const;
export const rolesQueryKey = ['team', 'roles'] as const;
export const permissionRegistryQueryKey = ['team', 'permissions'] as const;

export function useTenantUsers() {
  return useQuery({ queryKey: usersQueryKey, queryFn: listTenantUsers });
}

export function useRoles() {
  return useQuery({ queryKey: rolesQueryKey, queryFn: listRoles });
}

export function usePermissionRegistry() {
  return useQuery({ queryKey: permissionRegistryQueryKey, queryFn: getPermissionRegistry, staleTime: Infinity });
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
