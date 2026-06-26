// Session state (Zustand): the signed-in user (MeDto), active tenant, and tokens.
// Persisted to localStorage so a reload keeps the session. The access token is a
// short-lived (15 min) JWT; api-client transparently renews it on a 401 via a
// single-flight /auth/refresh and replays the request (see lib/api-client.ts).
//
// The api-client cannot call React hooks, so it reads the CURRENT token + active
// tenant through `getSessionSnapshot()` (a plain getter over the store), and the
// store keeps that snapshot in sync on every change.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { isJwtExpired } from '@/lib/jwt';
import type { Me, MeTenant } from '@/lib/mock/contracts';

interface SessionState {
  accessToken: string | null;
  refreshToken: string | null;
  /** Active tenant id → X-Tenant-Id header. */
  tenantId: string | null;
  user: Me | null;

  // ── Support impersonation (issue #3) ───────────────────────────────────────
  // When a support actor is impersonating a tenant, the access token carries an
  // `impersonated_tenant` JWT claim (the banner reads it directly). We ALSO keep
  // the server-issued `impersonationId` + target tenant here because the
  // /impersonation/end call needs the id, and the begin UI (when it ships) is the
  // only place that learns it. Both are cleared on end + on logout (clear()).
  impersonationId: string | null;
  impersonatedTenantId: string | null;

  isAuthenticated: () => boolean;
  activeTenant: () => MeTenant | null;
  setSession: (input: { accessToken: string; refreshToken: string; tenantId: string | null }) => void;
  setUser: (user: Me) => void;
  setTenant: (tenantId: string) => void;
  /** Record an in-progress impersonation (called when a /begin response lands). */
  setImpersonation: (input: { impersonationId: string; targetTenantId: string }) => void;
  /** Forget the impersonation linkage (called after /end succeeds). */
  clearImpersonation: () => void;
  clear: () => void;
}

export const useSession = create<SessionState>()(
  persist(
    (set, get) => ({
      accessToken: null,
      refreshToken: null,
      tenantId: null,
      user: null,
      impersonationId: null,
      impersonatedTenantId: null,

      // Presence is NOT validity. A persisted (localStorage) session restores
      // whatever access token was saved — including an EXPIRED 15-min JWT. Such a
      // token only counts as authenticated if a refresh token can still renew it
      // (api-client refreshes transparently on the first 401); with the access
      // token expired AND no refresh token, the session is dead → treat as logged
      // out so the route guard sends the user to /login instead of mounting a
      // doomed shell that 401-storms /me, /me/permissions, and /me/badges.
      isAuthenticated: () => {
        const { accessToken, refreshToken } = get();
        if (!accessToken) return false;
        if (isJwtExpired(accessToken)) return Boolean(refreshToken);
        return true;
      },
      activeTenant: () => {
        const { user, tenantId } = get();
        return user?.tenants.find((t) => t.tenantId === tenantId) ?? null;
      },
      setSession: ({ accessToken, refreshToken, tenantId }) =>
        set({ accessToken, refreshToken, tenantId }),
      setUser: (user) =>
        set((s) => ({ user, tenantId: s.tenantId ?? user.activeTenantId })),
      setTenant: (tenantId) => set({ tenantId }),
      setImpersonation: ({ impersonationId, targetTenantId }) =>
        set({ impersonationId, impersonatedTenantId: targetTenantId }),
      clearImpersonation: () => set({ impersonationId: null, impersonatedTenantId: null }),
      clear: () =>
        set({
          accessToken: null,
          refreshToken: null,
          tenantId: null,
          user: null,
          impersonationId: null,
          impersonatedTenantId: null,
        }),
    }),
    { name: 'docslot.session' },
  ),
);

/**
 * Non-React snapshot of the auth headers for the api-client. Reading
 * `useSession.getState()` outside React is supported by Zustand and always
 * returns the latest value.
 */
export function getSessionSnapshot(): { accessToken: string | null; tenantId: string | null } {
  const { accessToken, tenantId } = useSession.getState();
  return { accessToken, tenantId };
}
