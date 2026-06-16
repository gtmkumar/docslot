// Calendar feature: queries. Co-located per feature-folder rule. Wraps the mock
// adapter today; swaps to apiFetch when the slot-availability endpoint exists.

import { useQuery } from '@tanstack/react-query';
import { getCalendarGrid } from '@/lib/backend';

export const calendarGridQueryKey = ['calendar', 'week'] as const;

export function useCalendarGrid() {
  return useQuery({ queryKey: calendarGridQueryKey, queryFn: getCalendarGrid });
}
