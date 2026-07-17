// Developer / API Platform portal feature: API clients, scopes, webhooks +
// deliveries, and request logs. Co-located per feature-folder rule. Mutations
// take a stable Idempotency-Key generated once per action by the caller.
//
// SECRETS: register/rotate/createWebhook return the plaintext secret on their
// result. The calling component hands that result straight to the one-time
// secret panel and never writes it into any query cache. We deliberately do NOT
// store these results in React Query.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
// The developer-portal data fns route through the backend seam (real in live mode,
// mock by default): the READ LISTS, the forensic reads (request logs, deliveries),
// AND the WRITES (register/rotate/status/rate-limits/scopes + webhook create/update/
// retry). Each WRITE carries the caller's Idempotency-Key; the one-time plaintext
// secret on register/rotate/createWebhook flows straight to the reveal panel and is
// never cached. `ApiRequestLogFilter` is a type-only import (mock-defined shape).
import {
  createWebhook,
  listApiClients,
  listApiRequestLogs,
  listEventTypes,
  listScopes,
  listWebhookDeliveries,
  listWebhooks,
  registerApiClient,
  retryWebhookDelivery,
  rotateClientSecret,
  setClientRateLimits,
  setClientScopes,
  setClientStatus,
  updateWebhook,
} from '@/lib/backend';
import type { ApiRequestLogFilter } from '@/lib/mock';
import type {
  ApiClient,
  CreateWebhookRequest,
  RegisterApiClientRequest,
  SetClientRateLimitsRequest,
  UpdateWebhookRequest,
  WebhookSubscription,
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

/** Approve / suspend / reactivate a client — OPTIMISTIC: the cached row's
 *  isActive/isVerified (and the derived status chip) flip instantly, roll back on
 *  error (the caller toasts the revert), then reconcile on settle via invalidate
 *  (server state wins). */
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
    onMutate: async (vars) => {
      await qc.cancelQueries({ queryKey: apiClientsQueryKey });
      const previous = qc.getQueryData<ApiClient[]>(apiClientsQueryKey);
      qc.setQueryData<ApiClient[]>(apiClientsQueryKey, (old) =>
        old?.map((c) =>
          c.clientId === vars.clientId
            ? {
                ...c,
                isActive: vars.isActive,
                isVerified: vars.isVerified,
                // Same derivation the server uses: inactive → suspended,
                // verified+active → approved, unverified → pending.
                status: !vars.isActive ? 'suspended' : vars.isVerified ? 'approved' : 'pending',
              }
            : c,
        ),
      );
      return { previous };
    },
    onError: (_e, _vars, ctx) => {
      if (ctx?.previous) qc.setQueryData(apiClientsQueryKey, ctx.previous);
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: apiClientsQueryKey }),
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

/** Edit a webhook (name / URL / events / active) — OPTIMISTIC: the cached row
 *  reflects the edit instantly (the panel closes without waiting), rolls back on
 *  error (the caller toasts the revert), then reconciles on settle via invalidate. */
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
    onMutate: async (vars) => {
      await qc.cancelQueries({ queryKey: webhooksQueryKey });
      const previous = qc.getQueryData<WebhookSubscription[]>(webhooksQueryKey);
      qc.setQueryData<WebhookSubscription[]>(webhooksQueryKey, (old) =>
        old?.map((w) =>
          w.webhookId === vars.webhookId
            ? {
                ...w,
                // PATCH semantics: only the supplied (non-null) fields change.
                ...(vars.req.name != null ? { name: vars.req.name } : null),
                ...(vars.req.url != null ? { url: vars.req.url } : null),
                ...(vars.req.eventTypes != null ? { eventTypes: vars.req.eventTypes } : null),
                ...(vars.req.isActive != null ? { isActive: vars.req.isActive } : null),
              }
            : w,
        ),
      );
      return { previous };
    },
    onError: (_e, _vars, ctx) => {
      if (ctx?.previous) qc.setQueryData(webhooksQueryKey, ctx.previous);
    },
    onSettled: () => void qc.invalidateQueries({ queryKey: webhooksQueryKey }),
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
