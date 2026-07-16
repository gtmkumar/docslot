// Dashboard feature queries. Every widget goes through the backend seam
// (real-vs-mock by VITE_USE_REAL_API). Co-located per feature-folder rule.

import { useQuery } from '@tanstack/react-query';
import {
  getAgentPanel,
  getDashboardSummary,
  getDepartmentLoad,
  getFloorDoctors,
} from '@/lib/backend';

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
