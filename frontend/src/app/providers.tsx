// App-wide providers: TanStack Query (server cache), i18n (react-i18next is
// initialised on import), and the sonner Toaster for toasts/undo.

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { Toaster } from 'sonner';
import { onSessionExpired } from '@/lib/auth-events';
import { router } from '@/app/router';
import './i18n';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      refetchOnWindowFocus: false,
      staleTime: 30_000,
    },
  },
});

export function AppProviders({ children }: { children: ReactNode }) {
  // Terminal session-expiry handler (fix B): api-client emits when /auth/refresh
  // fails. Stop every in-flight/scheduled query (kills the dead-session polls)
  // and bounce to /login unless we're already there (avoids a redirect loop).
  useEffect(() => {
    return onSessionExpired(() => {
      queryClient.clear();
      if (router.state.location.pathname !== '/login') {
        void router.navigate({ to: '/login', search: { redirect: router.state.location.pathname } });
      }
    });
  }, []);

  return (
    <QueryClientProvider client={queryClient}>
      {children}
      {/* Toaster styled via tokens; richColors off so we control palette. */}
      <Toaster position="bottom-right" toastOptions={{ className: 'mono' }} />
    </QueryClientProvider>
  );
}
