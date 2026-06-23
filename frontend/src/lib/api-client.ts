// Fetch wrapper for DocSlot.API. Today it is unused by the dashboard (the mock
// adapter under lib/mock serves data), but it defines the exact transport the
// real endpoints will flow through, so swapping mock → real is a one-line change
// in each feature's api.ts.
//
// Responsibilities:
//  - base URL from env (VITE_API_BASE_URL), default '/api/v1'
//  - Authorization: Bearer <token> (placeholder until auth wave)
//  - X-Tenant-Id from the active org (DocSlot is multi-tenant; every request is
//    tenant-scoped). docslot.patients is the one cross-tenant exception, handled
//    server-side, so the header is always safe to send.
//  - idempotencyKey() helper that POST callers attach as `Idempotency-Key`.

import { getSessionSnapshot, useSession } from '@/stores/session';

const BASE_URL = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? '/api/v1';

// Auth endpoints are exempt from the 401→refresh→retry flow: refreshing in
// response to their own 401 would recurse (login/refresh failures are terminal).
const AUTH_EXEMPT_PATHS = ['/auth/login', '/auth/refresh', '/auth/logout'];

// Single-flight refresh. The access token is a short-lived (15 min) JWT and the
// refresh token ROTATES server-side (one-time use), so N concurrent 401s must
// share ONE /auth/refresh call — otherwise the first rotates the token and the
// rest fail, killing the session. Concurrent callers await the same promise.
let refreshInFlight: Promise<string | null> | null = null;

async function refreshAccessToken(): Promise<string | null> {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    const { refreshToken, tenantId, setSession, clear } = useSession.getState();
    if (!refreshToken) {
      clear();
      return null;
    }
    try {
      const res = await fetch(`${BASE_URL}/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      });
      if (!res.ok) {
        // Refresh token expired/revoked → session is over; the route guard
        // redirects to /login on the next navigation.
        clear();
        return null;
      }
      const data = (await res.json()) as { accessToken: string; refreshToken: string };
      setSession({ accessToken: data.accessToken, refreshToken: data.refreshToken, tenantId });
      return data.accessToken;
    } catch {
      clear();
      return null;
    } finally {
      refreshInFlight = null;
    }
  })();
  return refreshInFlight;
}

/** Bearer token from the live session (falls back to a dev env token if unset). */
function getAuthToken(): string | null {
  return getSessionSnapshot().accessToken ?? (import.meta.env.VITE_DEV_BEARER as string | undefined) ?? null;
}

/** Active tenant for X-Tenant-Id when a caller doesn't pass one explicitly. */
function getActiveTenantId(): string | null {
  return getSessionSnapshot().tenantId;
}

/** RFC4122-ish key for the `Idempotency-Key` header on POSTs (REACT_SKILL). */
export function idempotencyKey(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `idmp-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

export interface ApiRequest {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  /** Active tenant/org id → X-Tenant-Id header. */
  tenantId?: string;
  /**
   * Declared purpose-of-use → `X-Purpose-Of-Use` header (DPDP). REQUIRED by the
   * clinical PHI endpoints; the server rejects clinical reads without it and logs
   * the access. The UI declares it once via the purpose gate, then attaches it to
   * every clinical read.
   */
  purposeOfUse?: string;
  body?: unknown;
  /** Attach an Idempotency-Key (auto-generated if `true`, or pass an explicit one). */
  idempotency?: boolean | string;
  signal?: AbortSignal;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly body?: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * Low-level JSON request. Returns parsed JSON; throws {@link ApiError} on non-2xx.
 * Callers in feature `api.ts` files should zod-parse the result before use.
 */
export async function apiFetch<T = unknown>(path: string, req: ApiRequest = {}): Promise<T> {
  // Tenant + purpose + idempotency are stable across an auth retry; only the
  // Bearer token is re-read per attempt (it changes after a refresh). The
  // idempotency key is fixed up-front so a retried POST reuses the same key.
  const tenantId = req.tenantId ?? getActiveTenantId();
  const idempotencyHeader = req.idempotency
    ? typeof req.idempotency === 'string'
      ? req.idempotency
      : idempotencyKey()
    : undefined;

  const send = (): Promise<Response> => {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' };
    const token = getAuthToken();
    if (token) headers['Authorization'] = `Bearer ${token}`;
    if (tenantId) headers['X-Tenant-Id'] = tenantId;
    // Clinical PHI reads carry the declared purpose-of-use (DPDP; logged server-side).
    if (req.purposeOfUse) headers['X-Purpose-Of-Use'] = req.purposeOfUse;
    if (idempotencyHeader) headers['Idempotency-Key'] = idempotencyHeader;

    return fetch(`${BASE_URL}${path}`, {
      method: req.method ?? 'GET',
      headers,
      body: req.body !== undefined ? JSON.stringify(req.body) : undefined,
      signal: req.signal,
    });
  };

  let res = await send();

  // The access token expired → transparently refresh once and replay. Auth
  // endpoints are exempt (their 401 is terminal, not a stale-token signal).
  if (res.status === 401 && !AUTH_EXEMPT_PATHS.some((p) => path.startsWith(p))) {
    const newToken = await refreshAccessToken();
    if (newToken) res = await send();
  }

  const text = await res.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;

  if (!res.ok) {
    throw new ApiError(res.status, `Request failed: ${res.status} ${path}`, parsed);
  }
  return parsed as T;
}
