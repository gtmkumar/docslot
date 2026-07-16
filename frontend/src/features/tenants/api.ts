// Tenant onboarding (platform console). One mutation: POST /api/v1/tenants creates
// the clinic AND mints its one-time tenant_owner invitation atomically (gated
// platform.tenants.create). On success the impersonation target list is refreshed
// so the new clinic is immediately impersonatable.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createTenant, getTenant, reactivateTenant, suspendTenant, updateTenant } from '@/lib/backend';
import { tenantsQueryKey } from '@/features/impersonation/api';
import type {
  CreateTenantRequest,
  CreateTenantResult,
  TenantDetail,
  TenantListItem,
  UpdateTenantRequest,
} from '@/lib/mock/contracts';

/** Full editable detail for one tenant (GET /tenants/{id}), used by the manage/edit
 *  slide-over to pre-fill every field. Keyed per-id; only fetched while the panel is
 *  open. Short staleTime so a re-open after an edit/suspend re-syncs. */
export const tenantDetailQueryKey = (tenantId: string) => ['tenants', 'detail', tenantId] as const;

export function useTenant(tenantId: string, enabled = true) {
  return useQuery({
    queryKey: tenantDetailQueryKey(tenantId),
    queryFn: (): Promise<TenantDetail> => getTenant(tenantId),
    enabled: enabled && Boolean(tenantId),
    staleTime: 30_000,
  });
}

export function useCreateTenant() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { request: CreateTenantRequest; idempotencyKey: string }): Promise<CreateTenantResult> =>
      createTenant(vars.request, vars.idempotencyKey),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: tenantsQueryKey });
    },
  });
}

/** Mutation behind the manage-tenant slide-over's save (PUT /tenants/{id}, gated
 *  `platform.tenants.update`). Carries a stable Idempotency-Key. On success the shared
 *  ['tenants','list'] cache (also used by the impersonation picker) is stale — invalidate
 *  it so the list + count reflect the edit. */
export function useUpdateTenant() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: {
      tenantId: string;
      request: UpdateTenantRequest;
      idempotencyKey: string;
    }): Promise<TenantListItem> => updateTenant(vars.tenantId, vars.request, vars.idempotencyKey),
    onSuccess: (_data, vars) => {
      void qc.invalidateQueries({ queryKey: tenantsQueryKey });
      void qc.invalidateQueries({ queryKey: tenantDetailQueryKey(vars.tenantId) });
    },
  });
}

/** Mutation behind the DANGEROUS suspend/reactivate action (gated `platform.tenants.suspend`).
 *  Picks the route by direction: `isActive: false` → PUT /tenants/{id}/suspend (reason
 *  MANDATORY), `isActive: true` → PUT /tenants/{id}/reactivate (reason cleared). Both
 *  return the fresh TenantDetail; on success we seed the detail cache with it (instant
 *  chip + reason re-sync) and invalidate the shared list. Carries a stable Idempotency-Key. */
export function useSetTenantSuspension() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: {
      tenantId: string;
      isActive: boolean;
      reason?: string | null;
      idempotencyKey: string;
    }): Promise<TenantDetail> =>
      vars.isActive
        ? reactivateTenant(vars.tenantId, vars.reason ?? null, vars.idempotencyKey)
        : suspendTenant(vars.tenantId, (vars.reason ?? '').trim(), vars.idempotencyKey),
    onSuccess: (detail, vars) => {
      qc.setQueryData(tenantDetailQueryKey(vars.tenantId), detail);
      void qc.invalidateQueries({ queryKey: tenantsQueryKey });
    },
  });
}
