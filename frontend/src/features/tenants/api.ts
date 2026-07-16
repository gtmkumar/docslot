// Tenant onboarding (platform console). One mutation: POST /api/v1/tenants creates
// the clinic AND mints its one-time tenant_owner invitation atomically (gated
// platform.tenants.create). On success the impersonation target list is refreshed
// so the new clinic is immediately impersonatable.

import { useMutation, useQueryClient } from '@tanstack/react-query';
import { createTenant } from '@/lib/backend';
import { tenantsQueryKey } from '@/features/impersonation/api';
import type { CreateTenantRequest, CreateTenantResult } from '@/lib/mock/contracts';

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
