// Dashboard feature queries. Wraps the mock adapter today; swaps to apiFetch
// later. Co-located per feature-folder rule.

import { useQuery } from '@tanstack/react-query';
import { getDashboardSummary } from '@/lib/backend';
// Agent panel / department-load / floor-doctors have no live endpoint yet
// (flagged in api-contracts) — they stay on the mock seam regardless of the flag.
import { getAgentPanel, getDepartmentLoad, getFloorDoctors } from '@/lib/mock';

export const dashboardSummaryQueryKey = ['dashboard', 'summary'] as const;
export const agentPanelQueryKey = ['dashboard', 'agent'] as const;
export const departmentLoadQueryKey = ['dashboard', 'departmentLoad'] as const;
export const floorDoctorsQueryKey = ['dashboard', 'floor'] as const;

export function useDashboardSummary() {
  return useQuery({ queryKey: dashboardSummaryQueryKey, queryFn: getDashboardSummary });
}

export function useAgentPanel() {
  return useQuery({ queryKey: agentPanelQueryKey, queryFn: getAgentPanel });
}

export function useDepartmentLoad() {
  return useQuery({ queryKey: departmentLoadQueryKey, queryFn: getDepartmentLoad });
}

export function useFloorDoctors() {
  return useQuery({ queryKey: floorDoctorsQueryKey, queryFn: getFloorDoctors });
}
