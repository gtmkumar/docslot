// In-memory permission gate. Per REACT_SKILL + RBAC_NAVIGATION, the effective
// permission set is fetched ONCE per session (via TanStack Query), held in
// memory, and every component-level check is a Set lookup — NEVER a per-check
// network call, and NEVER a role === 'x' branch in JSX.

import { useQuery } from '@tanstack/react-query';
import { getPermissions } from '@/lib/backend';

export const permissionsQueryKey = ['me', 'permissions'] as const;

/**
 * Returns a `can(key)` predicate backed by the once-fetched effective set, plus
 * the loading flag. While loading, `can` returns false (fail-closed). The query
 * is cached for the whole session (staleTime: Infinity).
 */
export function usePermissions(): { can: (key: string) => boolean; isLoading: boolean } {
  const { data, isLoading } = useQuery({
    queryKey: permissionsQueryKey,
    queryFn: getPermissions,
    staleTime: Infinity,
    gcTime: Infinity,
  });

  const set = data ? new Set(data.permissionKeys) : null;
  const can = (key: string): boolean => (set ? set.has(key) : false);

  return { can, isLoading };
}
