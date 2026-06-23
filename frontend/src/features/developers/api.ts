// Developer / API Platform portal feature: API clients, scopes, webhooks +
// deliveries, and request logs. Co-located per feature-folder rule. Mutations
// take a stable Idempotency-Key generated once per action by the caller.
//
// SECRETS: register/rotate/createWebhook return the plaintext secret on their
// result. The calling component hands that result straight to the one-time
// secret panel and never writes it into any query cache. We deliberately do NOT
// store these results in React Query.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
// READ LISTS that have a LIVE endpoint route through the backend seam (real in
// live mode, mock by default). Everything else (request logs, deliveries, and all
// mutations) has no wired live endpoint and stays on the mock seam directly.
import { listApiClients, listEventTypes, listScopes, listWebhooks } from '@/lib/backend';
import {
  createWebhook,
  listApiRequestLogs,
  listWebhookDeliveries,
  registerApiClient,
  retryWebhookDelivery,
  rotateClientSecret,
  setClientRateLimits,
  setClientScopes,
  setClientStatus,
  updateWebhook,
  type ApiRequestLogFilter,
} from '@/lib/mock';
import type {
  CreateWebhookRequest,
  RegisterApiClientRequest,
  SetClientRateLimitsRequest,
  UpdateWebhookRequest,
} from '@/lib/mock/contracts';

export const apiClientsQueryKey = ['developers', 'clients'] as const;
export const scopesQueryKey = ['developers', 'scopes'] as const;
export const eventTypesQueryKey = ['developers', 'eventTypes'] as const;
export const webhooksQueryKey = ['developers', 'webhooks'] as const;

export function useApiClients() {
  return useQuery({ queryKey: apiClientsQueryKey, queryFn: listApiClients });
}

export function useScopes() {
  return useQuery({ queryKey: scopesQueryKey, queryFn: listScopes, staleTime: Infinity });
}

export function useEventTypes() {
  return useQuery({ queryKey: eventTypesQueryKey, queryFn: listEventTypes, staleTime: Infinity });
}

export function useWebhooks() {
  return useQuery({ queryKey: webhooksQueryKey, queryFn: listWebhooks });
}

export function useWebhookDeliveries(webhookId: string | undefined) {
  return useQuery({
    queryKey: ['developers', 'deliveries', webhookId] as const,
    queryFn: () => listWebhookDeliveries(webhookId ?? ''),
    enabled: Boolean(webhookId),
  });
}

export function useApiRequestLogs(filter: ApiRequestLogFilter) {
  return useQuery({
    queryKey: ['developers', 'logs', filter.clientId ?? 'all', filter.page ?? 1] as const,
    queryFn: () => listApiRequestLogs(filter),
    // Keep the previous page visible while the next loads (smoother paging).
    placeholderData: (prev) => prev,
  });
}

// ── Mutations ────────────────────────────────────────────────────────────────

export interface RegisterClientInput extends RegisterApiClientRequest {
  idempotencyKey: string;
}
export function useRegisterClient() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: RegisterClientInput) => registerApiClient(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: apiClientsQueryKey }),
  });
}

export function useRotateSecret() {
  return useMutation({
    mutationFn: ({ clientId, idempotencyKey }: { clientId: string; idempotencyKey: string }) =>
      rotateClientSecret(clientId, idempotencyKey),
  });
}

export function useSetClientStatus() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      clientId,
      isActive,
      isVerified,
      idempotencyKey,
    }: {
      clientId: string;
      isActive: boolean;
      isVerified: boolean;
      idempotencyKey: string;
    }) => setClientStatus(clientId, { isActive, isVerified }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: apiClientsQueryKey }),
  });
}

export function useSetClientRateLimits() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      clientId,
      limits,
      idempotencyKey,
    }: {
      clientId: string;
      limits: SetClientRateLimitsRequest;
      idempotencyKey: string;
    }) => setClientRateLimits(clientId, limits, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: apiClientsQueryKey }),
  });
}

export function useSetClientScopes() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      clientId,
      scopeKeys,
      idempotencyKey,
    }: {
      clientId: string;
      scopeKeys: string[];
      idempotencyKey: string;
    }) => setClientScopes(clientId, scopeKeys, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: apiClientsQueryKey }),
  });
}

export interface CreateWebhookInput extends CreateWebhookRequest {
  idempotencyKey: string;
}
export function useCreateWebhook() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateWebhookInput) => createWebhook(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: webhooksQueryKey }),
  });
}

export function useUpdateWebhook() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      webhookId,
      req,
      idempotencyKey,
    }: {
      webhookId: string;
      req: UpdateWebhookRequest;
      idempotencyKey: string;
    }) => updateWebhook(webhookId, req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: webhooksQueryKey }),
  });
}

export function useRetryDelivery() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ deliveryId, idempotencyKey }: { deliveryId: string; idempotencyKey: string }) =>
      retryWebhookDelivery(deliveryId, idempotencyKey),
    onSuccess: (_r, vars) => {
      void vars;
      void qc.invalidateQueries({ queryKey: ['developers', 'deliveries'] });
    },
  });
}
