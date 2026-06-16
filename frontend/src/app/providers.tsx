// App-wide providers: TanStack Query (server cache), i18n (react-i18next is
// initialised on import), and the sonner Toaster for toasts/undo.

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { Toaster } from 'sonner';
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
  return (
    <QueryClientProvider client={queryClient}>
      {children}
      {/* Toaster styled via tokens; richColors off so we control palette. */}
      <Toaster position="bottom-right" toastOptions={{ className: 'mono' }} />
    </QueryClientProvider>
  );
}
