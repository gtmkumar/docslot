// Support-impersonation feature: the begin/end helpers + the React mutation that
// wires "Exit impersonation". The begin UI itself is out of scope (no admin panel
// yet) — `beginImpersonation` exists so a future panel can reuse the exact wiring.
//
// Contract (issue #3):
//   POST /api/v1/auth/impersonation/begin → { token, impersonationId, targetTenantId, expiresAtUtc }
//        token.accessToken carries the signed `impersonated_tenant` claim.
//   POST /api/v1/auth/impersonation/end   → a CLEAN TokenResponse (no claim).
//
// Both helpers parse the response with zod and push the returned token bundle
// through the session store so the added/cleared claim takes effect immediately
// (the banner reads the claim off the live access token).

import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/lib/api-client';
import {
  BeginImpersonationResultSchema,
  EndImpersonationResultSchema,
  type BeginImpersonationRequest,
  type BeginImpersonationResult,
  type EndImpersonationResult,
} from '@/lib/mock/contracts';
import { useSession } from '@/stores/session';

const BEGIN_PATH = '/auth/impersonation/begin';
const END_PATH = '/auth/impersonation/end';

/**
 * Begin impersonating a tenant. Mints an access token carrying the
 * `impersonated_tenant` claim, stores it (so the claim takes effect) along with
 * the impersonation linkage (needed later by /end), and returns the parsed
 * result. Reusable by a future "begin" admin UI.
 */
export async function beginImpersonation(
  input: Omit<BeginImpersonationRequest, 'refreshToken'>,
): Promise<BeginImpersonationResult> {
  const { refreshToken, setSession, setImpersonation } = useSession.getState();
  if (!refreshToken) throw new Error('No active session');

  const raw = await apiFetch(BEGIN_PATH, {
    method: 'POST',
    body: { ...input, refreshToken },
    idempotency: true,
  });
  const result = BeginImpersonationResultSchema.parse(raw);

  // Adopt the impersonation token (claim now present) + remember the linkage so
  // the exit call has the id it needs.
  setSession({
    accessToken: result.token.accessToken,
    refreshToken: result.token.refreshToken,
    tenantId: result.token.activeTenantId,
  });
  setImpersonation({ impersonationId: result.impersonationId, targetTenantId: result.targetTenantId });

  return result;
}

/**
 * End the active impersonation. Calls /end with the stored impersonationId, swaps
 * in the clean token bundle (claim gone), and forgets the impersonation linkage.
 */
export async function endImpersonation(): Promise<EndImpersonationResult> {
  const { impersonationId, refreshToken, setSession, clearImpersonation } = useSession.getState();
  if (!impersonationId) throw new Error('No active impersonation');
  if (!refreshToken) throw new Error('No active session');

  const raw = await apiFetch(END_PATH, {
    method: 'POST',
    body: { impersonationId, refreshToken },
    idempotency: true,
  });
  const token = EndImpersonationResultSchema.parse(raw);

  // Adopt the clean bundle (no `impersonated_tenant` claim) and drop the linkage.
  setSession({
    accessToken: token.accessToken,
    refreshToken: token.refreshToken,
    tenantId: token.activeTenantId,
  });
  clearImpersonation();

  return token;
}

/**
 * Mutation behind the banner's "Exit impersonation" action. On success the
 * server cache for the impersonated tenant (menus, permissions, every list) is
 * stale — drop it so the UI re-bootstraps under the operator's own scope.
 */
export function useEndImpersonation() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => endImpersonation(),
    onSuccess: () => {
      // The impersonated tenant's data must not bleed into the restored session.
      qc.clear();
    },
  });
}
