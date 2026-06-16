// Analytics feature: queries. Co-located per feature-folder rule. Reads the
// backend seam — live mode hits GET /analytics?period=, mock mode serves static
// aggregates (period ignored). Gated by docslot.analytics.read at the screen level.

import { useQuery } from '@tanstack/react-query';
import { getAnalytics } from '@/lib/backend';

export type AnalyticsPeriod = 'month' | 'quarter' | 'year';

export const analyticsQueryKey = (period: AnalyticsPeriod) => ['analytics', 'summary', period] as const;

export function useAnalytics(period: AnalyticsPeriod = 'month') {
  return useQuery({
    queryKey: analyticsQueryKey(period),
    queryFn: () => getAnalytics(period),
  });
}
