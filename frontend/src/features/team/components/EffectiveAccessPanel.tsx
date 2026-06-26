// Effective-access viewer slide-over. Renders GET /users/{id}/effective-access —
// the resolved permission-key set for a user — grouped by resource/module for
// readability, with copy explaining the resolution: role grants − denies + grants
// (deny wins). Gated upstream by tenant.users.read. Read-only; no PHI.

import { useTranslation } from 'react-i18next';
import { KeyRound } from 'lucide-react';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { useEffectiveAccess, useTenantUsers } from '../api';

export function EffectiveAccessPanel({ userId, open, onClose }: { userId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: users } = useTenantUsers();
  const user = users?.find((u) => u.userId === userId);
  const { data, isLoading, isError, refetch } = useEffectiveAccess(userId, open);

  // Group the flat key set by its resource segment (the 2nd dotted token, e.g.
  // `docslot.booking.read` → "booking"). Purely a readability grouping.
  const grouped = (() => {
    const map = new Map<string, string[]>();
    for (const key of data?.permissionKeys ?? []) {
      const parts = key.split('.');
      const resource = parts.length >= 3 ? parts[1] : (parts[0] ?? 'other');
      (map.get(resource) ?? map.set(resource, []).get(resource)!).push(key);
    }
    return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
  })();

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.effective.eyebrow')}
      title={user?.fullName ?? t('team.effective.eyebrow')}
      description={t('team.effective.description')}
    >
      <div className="flex flex-col gap-4">
        {user ? <p className="-mt-1 text-[12px] text-muted">{user.email}</p> : null}

        <p className="flex items-start gap-2 rounded-[var(--radius-sm)] border border-line bg-bg-2 px-3 py-2 text-[12px] text-muted">
          <KeyRound size={14} className="mt-px shrink-0 text-muted-2" aria-hidden="true" />
          {t('team.effective.explainer')}
        </p>

        {isError ? (
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        ) : isLoading || !data ? (
          <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
            {Array.from({ length: 6 }).map((_, i) => (
              <Skeleton key={i} className="h-7 w-full" />
            ))}
          </div>
        ) : data.permissionKeys.length === 0 ? (
          <EmptyState title={t('team.effective.emptyTitle')} description={t('team.effective.emptyBody')} />
        ) : (
          <div className="flex flex-col gap-3">
            <p className="text-[12px] text-muted">
              {t('team.effective.count', { count: data.permissionKeys.length })}
            </p>
            {grouped.map(([resource, keys]) => (
              <section key={resource}>
                <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{resource}</h3>
                <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
                  {keys.map((key) => (
                    <li key={key} className="px-3 py-1.5">
                      <span className="mono text-[12px] text-ink">{key}</span>
                    </li>
                  ))}
                </ul>
              </section>
            ))}
          </div>
        )}
      </div>
    </SlideOver>
  );
}
