// Tiny client-side JWT payload reader. We ONLY decode the payload to read
// non-secret display claims (e.g. the support-impersonation `impersonated_tenant`
// claim) — signature verification is the server's job and is never attempted
// here. No new dependency: a JWT payload is a base64url-encoded JSON segment.
//
// A claim read here is advisory UI state only. The token is server-signed; the
// API independently enforces the impersonation scope on every request.

/** Decode a base64url string (JWT segments are base64url, not standard base64). */
function decodeBase64Url(segment: string): string | null {
  try {
    // base64url → base64: swap the URL-safe alphabet and re-pad to a multiple of 4.
    const base64 = segment.replace(/-/g, '+').replace(/_/g, '/').padEnd(
      segment.length + ((4 - (segment.length % 4)) % 4),
      '=',
    );
    const binary = atob(base64);
    // Decode UTF-8 bytes (claims may contain non-ASCII, e.g. a tenant display name).
    const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
    return new TextDecoder().decode(bytes);
  } catch {
    return null;
  }
}

/** Parse a JWT's payload claims. Returns null for any malformed/non-JWT input. */
export function decodeJwtPayload(token: string | null | undefined): Record<string, unknown> | null {
  if (!token) return null;
  const parts = token.split('.');
  if (parts.length < 2) return null;
  const json = decodeBase64Url(parts[1]);
  if (!json) return null;
  try {
    const parsed = JSON.parse(json) as unknown;
    return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/**
 * True when `token` is a JWT whose `exp` claim is in the past (i.e. the access
 * token has expired). Returns FALSE for a missing token, a non-JWT/opaque token,
 * or a token with no readable `exp` — we never lock a session out on a claim we
 * can't prove is stale; the server (and the 401→refresh flow) are the real
 * authority. `exp` is seconds-since-epoch per RFC 7519. A small clock-skew grace
 * avoids flapping right at the boundary.
 */
export function isJwtExpired(token: string | null | undefined, skewSeconds = 30): boolean {
  const claims = decodeJwtPayload(token);
  const exp = claims?.['exp'];
  if (typeof exp !== 'number') return false;
  return exp * 1000 <= Date.now() - skewSeconds * 1000;
}

/**
 * The target tenant id when the current access token is a support-impersonation
 * token, else null. The backend mints this token with a signed
 * `impersonated_tenant` claim (the target tenant's UUID); its presence is the
 * single source of truth for "am I impersonating right now?".
 */
export function readImpersonatedTenant(token: string | null | undefined): string | null {
  const claims = decodeJwtPayload(token);
  const value = claims?.['impersonated_tenant'];
  return typeof value === 'string' && value.length > 0 ? value : null;
}
