// Auth feature: login / refresh / logout / me. Wraps the mock seam today; the
// swap to apiFetch is one line per fn. Session persistence lives in
// stores/session; these hooks just orchestrate the calls + cache.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { forgotPassword, getMe, login, logout, resetPassword } from '@/lib/backend';
import type { ForgotPasswordRequest, LoginRequest, ResetPasswordRequest } from '@/lib/mock/contracts';
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

/** Self-service forgot-password (PUBLIC). ALWAYS resolves (anti-enumeration): the
 *  screen shows one generic confirmation regardless of whether the email exists. No
 *  session/cache interaction. */
export function useForgotPassword() {
  return useMutation({ mutationFn: (req: ForgotPasswordRequest) => forgotPassword(req) });
}

/** Self-service reset-password (PUBLIC; the token IS the authorization). On success
 *  the screen redirects to /login. Invalid/expired/used tokens reject with the generic
 *  4xx — the screen surfaces one generic inline message. No session/cache interaction. */
export function useResetPassword() {
  return useMutation({ mutationFn: (req: ResetPasswordRequest) => resetPassword(req) });
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
