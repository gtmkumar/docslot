// Navigation feature: backend-driven menu tree + batched badge counts.
// useMenus is the SINGLE source of the sidebar nav — the tree is data, never
// hardcoded JSX, never role-branched.

import { useQuery } from '@tanstack/react-query';
import { getBadges, getMenus } from '@/lib/backend';
import type { BadgesResponse } from '@/lib/mock/contracts';

export const menusQueryKey = ['me', 'menus'] as const;
export const badgesQueryKey = ['me', 'badges'] as const;

export function useMenus() {
  return useQuery({
    queryKey: menusQueryKey,
    queryFn: getMenus,
    staleTime: Infinity, // menu shape is stable for the session
  });
}

export function useBadges() {
  return useQuery({
    queryKey: badgesQueryKey,
    queryFn: getBadges,
    refetchInterval: 60_000, // one batched poll per REACT_SKILL
    // Unwrap BadgesDto.counts so consumers see a flat { badgeSource: count } map.
    select: (dto: BadgesResponse) => dto.counts,
  });
}
