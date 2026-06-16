// Session state (Zustand): the signed-in user (MeDto), active tenant, and tokens.
// Persisted to localStorage so a refresh keeps the session (the access token is a
// short-lived JWT; a real deployment would refresh it on bootstrap — wired via
// refresh() in the auth feature).
//
// The api-client cannot call React hooks, so it reads the CURRENT token + active
// tenant through `getSessionSnapshot()` (a plain getter over the store), and the
// store keeps that snapshot in sync on every change.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { Me, MeTenant } from '@/lib/mock/contracts';

interface SessionState {
  accessToken: string | null;
  refreshToken: string | null;
  /** Active tenant id → X-Tenant-Id header. */
  tenantId: string | null;
  user: Me | null;

  isAuthenticated: () => boolean;
  activeTenant: () => MeTenant | null;
  setSession: (input: { accessToken: string; refreshToken: string; tenantId: string | null }) => void;
  setUser: (user: Me) => void;
  setTenant: (tenantId: string) => void;
  clear: () => void;
}

export const useSession = create<SessionState>()(
  persist(
    (set, get) => ({
      accessToken: null,
      refreshToken: null,
      tenantId: null,
      user: null,

      isAuthenticated: () => Boolean(get().accessToken),
      activeTenant: () => {
        const { user, tenantId } = get();
        return user?.tenants.find((t) => t.tenantId === tenantId) ?? null;
      },
      setSession: ({ accessToken, refreshToken, tenantId }) =>
        set({ accessToken, refreshToken, tenantId }),
      setUser: (user) =>
        set((s) => ({ user, tenantId: s.tenantId ?? user.activeTenantId })),
      setTenant: (tenantId) => set({ tenantId }),
      clear: () => set({ accessToken: null, refreshToken: null, tenantId: null, user: null }),
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
