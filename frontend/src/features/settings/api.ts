// Workspace Settings feature (Phase 1): the tenant facility profile, business hours,
// booking-rule defaults, and integration status. Co-located per feature-folder rule.
//
// Both data fns have a LIVE .NET implementation and are imported from '@/lib/backend'
// (each switches live/mock by VITE_USE_REAL_API): getSettings (GET /settings) and
// updateSettings (PATCH /settings). The PATCH carries NO Idempotency-Key — the backend
// documents it as a configuration write, not a money/booking mutation.

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getSettings, updateSettings } from '@/lib/backend';
import type { Settings, UpdateSettingsRequest } from '@/lib/mock/contracts';

export const settingsQueryKey = ['settings'] as const;

/** The workspace settings. Enabled by the caller — the screen only fetches when the
 *  signed-in user holds tenant.settings.read (so a viewer without it never fires a 403).
 *  A GET 404 (no facility row) surfaces as `isError` with `error.status === 404`, which
 *  the screen distinguishes from a forbidden/other error. */
export function useSettings(enabled = true) {
  return useQuery({ queryKey: settingsQueryKey, queryFn: getSettings, enabled });
}

/** Save one or more settings sections. Each supplied section REPLACES the stored one
 *  (send the full section object). On success the server's re-read DTO replaces the
 *  cache so the form reflects the saved state without a refetch flash. */
export function useUpdateSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: UpdateSettingsRequest) => updateSettings(req),
    onSuccess: (dto: Settings) => qc.setQueryData(settingsQueryKey, dto),
  });
}
