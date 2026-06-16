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

import { getSessionSnapshot } from '@/stores/session';

const BASE_URL = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? '/api/v1';

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
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };

  const token = getAuthToken();
  if (token) headers['Authorization'] = `Bearer ${token}`;
  // Explicit tenantId wins; otherwise fall back to the active session tenant.
  const tenantId = req.tenantId ?? getActiveTenantId();
  if (tenantId) headers['X-Tenant-Id'] = tenantId;
  // Clinical PHI reads carry the declared purpose-of-use (DPDP; logged server-side).
  if (req.purposeOfUse) headers['X-Purpose-Of-Use'] = req.purposeOfUse;
  if (req.idempotency) {
    headers['Idempotency-Key'] = typeof req.idempotency === 'string' ? req.idempotency : idempotencyKey();
  }

  const res = await fetch(`${BASE_URL}${path}`, {
    method: req.method ?? 'GET',
    headers,
    body: req.body !== undefined ? JSON.stringify(req.body) : undefined,
    signal: req.signal,
  });

  const text = await res.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;

  if (!res.ok) {
    throw new ApiError(res.status, `Request failed: ${res.status} ${path}`, parsed);
  }
  return parsed as T;
}
