// Auth feature: login / refresh / logout / me. Wraps the mock seam today; the
// swap to apiFetch is one line per fn. Session persistence lives in
// stores/session; these hooks just orchestrate the calls + cache.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getMe, login, logout } from '@/lib/backend';
import type { LoginRequest } from '@/lib/mock/contracts';
import { useSession } from '@/stores/session';

export const meQueryKey = ['me', 'profile'] as const;

/** Bootstraps the signed-in profile once a token exists. Drives the route guard. */
export function useMe() {
  const isAuthed = useSession((s) => Boolean(s.accessToken));
  const setUser = useSession((s) => s.setUser);
  return useQuery({
    queryKey: meQueryKey,
    queryFn: async () => {
      const me = await getMe();
      setUser(me);
      return me;
    },
    enabled: isAuthed,
    staleTime: Infinity,
  });
}

export function useLogin() {
  const setSession = useSession((s) => s.setSession);
  return useMutation({
    mutationFn: (req: LoginRequest) => login(req),
    onSuccess: (token) =>
      setSession({
        accessToken: token.accessToken,
        refreshToken: token.refreshToken,
        tenantId: token.activeTenantId,
      }),
  });
}

export function useLogout() {
  const qc = useQueryClient();
  const session = useSession();
  return useMutation({
    mutationFn: () => logout(session.refreshToken ?? undefined),
    onSettled: () => {
      session.clear();
      qc.clear(); // drop all cached server state (permissions/menus/users…)
    },
  });
}
