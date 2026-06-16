// Commission / Care Partners feature (Slice 07). Queries + mutations. Money
// actions carry a stable Idempotency-Key. APPROVE and EXECUTE are separate
// mutations (separate permissions) — the UI never collapses them.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  approvePayout,
  blacklistBroker,
  createCommissionRule,
  executePayout,
  listAttributions,
  listBrokers,
  listCommissionRules,
  listDisputes,
  listPayouts,
  raiseDispute,
  registerBroker,
  resolveDispute,
  setBrokerStatus,
} from '@/lib/backend';
import type {
  CreateCommissionRuleRequest,
  RaiseDisputeRequest,
  RegisterBrokerRequest,
  ResolveDisputeRequest,
} from '@/lib/mock/contracts';

export const brokersQueryKey = ['commission', 'brokers'] as const;
export const attributionsQueryKey = ['commission', 'attributions'] as const;
export const rulesQueryKey = ['commission', 'rules'] as const;
export const payoutsQueryKey = ['commission', 'payouts'] as const;
export const disputesQueryKey = ['commission', 'disputes'] as const;

export function useBrokers() {
  return useQuery({ queryKey: brokersQueryKey, queryFn: listBrokers });
}
export function useAttributions() {
  return useQuery({ queryKey: attributionsQueryKey, queryFn: listAttributions });
}
export function useCommissionRules() {
  return useQuery({ queryKey: rulesQueryKey, queryFn: listCommissionRules });
}
export function usePayouts() {
  return useQuery({ queryKey: payoutsQueryKey, queryFn: listPayouts });
}
export function useDisputes() {
  return useQuery({ queryKey: disputesQueryKey, queryFn: listDisputes });
}

export function useRegisterBroker() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: RegisterBrokerRequest & { idempotencyKey: string }) =>
      registerBroker(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: brokersQueryKey }),
  });
}

export function useSetBrokerStatus() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ brokerId, isActive, reason, idempotencyKey }: { brokerId: string; isActive: boolean; reason?: string; idempotencyKey: string }) =>
      setBrokerStatus(brokerId, { isActive, reason }, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: brokersQueryKey }),
  });
}

export function useBlacklistBroker() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ brokerId, reason, idempotencyKey }: { brokerId: string; reason: string; idempotencyKey: string }) =>
      blacklistBroker(brokerId, reason, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: brokersQueryKey }),
  });
}

export function useCreateCommissionRule() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: CreateCommissionRuleRequest & { idempotencyKey: string }) =>
      createCommissionRule(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: rulesQueryKey }),
  });
}

/** APPROVE — distinct from execute (commission.payouts.approve). */
export function useApprovePayout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ payoutId, idempotencyKey }: { payoutId: string; idempotencyKey: string }) =>
      approvePayout(payoutId, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: payoutsQueryKey }),
  });
}

/** EXECUTE — distinct from approve (commission.payouts.execute). */
export function useExecutePayout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ payoutId, idempotencyKey }: { payoutId: string; idempotencyKey: string }) =>
      executePayout(payoutId, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: payoutsQueryKey }),
  });
}

export function useRaiseDispute() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: RaiseDisputeRequest & { idempotencyKey: string }) =>
      raiseDispute(req, idempotencyKey),
    onSuccess: () => void qc.invalidateQueries({ queryKey: disputesQueryKey }),
  });
}

export function useResolveDispute() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, ...req }: ResolveDisputeRequest & { idempotencyKey: string }) =>
      resolveDispute(req, idempotencyKey),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: disputesQueryKey });
      void qc.invalidateQueries({ queryKey: attributionsQueryKey });
    },
  });
}
