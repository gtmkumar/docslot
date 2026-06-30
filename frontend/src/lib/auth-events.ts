// Decoupling seam between the non-React api-client and the React router/query
// cache. When a token refresh fails terminally, the session is over — but
// api-client (a plain module) can't navigate or touch the query cache without
// importing the router, which would create a circular import
// (router → AppShell → features → backend → api-client). Instead it EMITS here,
// and the app subscribes once at bootstrap (see app/providers.tsx) to redirect
// to /login and clear React Query so dead-session polls (e.g. /me/badges) stop.

type Listener = () => void;

const listeners = new Set<Listener>();

/** Subscribe to terminal session-expiry. Returns an unsubscribe fn. */
export function onSessionExpired(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Fired by api-client after clear() when /auth/refresh fails (session is dead). */
export function emitSessionExpired(): void {
  for (const listener of listeners) listener();
}
