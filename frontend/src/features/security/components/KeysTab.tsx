// Encryption key health tab (read-only; mirrors v_key_rotation_status). Shows
// which keys are due/overdue for rotation. Gated by the menu
// (platform.encryption_keys.read server-side). NO key material is ever shown —
// only metadata + rotation status. Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { useKeyStatus } from '../api';
import { SecurityBadge } from './SecurityBadges';
import type { KeyStatus } from '@/lib/mock/contracts';

export function KeysTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useKeyStatus();

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[13px] text-muted">{t('security.keys.sub')}</p>

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 4 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 w-40" />
                <Skeleton className="ml-auto h-5 w-20" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('security.keys.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((k) => (
              <KeyRow key={k.keyId} keyStatus={k} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function KeyRow({ keyStatus: k }: { keyStatus: KeyStatus }) {
  const { t } = useTranslation();
  const statusLabel =
    k.rotationStatus === 'ok' ? t('security.keys.statusOk') : k.rotationStatus === 'due_soon' ? t('security.keys.statusDueSoon') : t('security.keys.statusOverdue');
  const rotationText =
    k.daysUntilRotation == null
      ? '—'
      : k.daysUntilRotation < 0
        ? t('security.keys.overdueBy', { days: Math.abs(k.daysUntilRotation) })
        : t('security.keys.dueIn', { days: k.daysUntilRotation });

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <div className="min-w-0 flex-1">
        <p className="truncate text-[13px] text-ink">{k.tenantName ?? t('security.keys.platform')}</p>
        <p className="mono text-[11px] text-muted">{k.dataClass}</p>
      </div>
      <span className="mono hidden w-24 shrink-0 text-right text-[11px] text-muted-2 md:block">{shortDate(k.activatedAt)}</span>
      <span className="mono hidden w-24 shrink-0 text-right text-[11px] text-muted lg:block">{rotationText}</span>
      <SecurityBadge tone={k.rotationStatus} label={statusLabel} />
      <span className="mono hidden w-20 shrink-0 text-right text-[11px] text-muted-2 lg:block">{k.usageCount.toLocaleString('en-IN')}</span>
    </li>
  );
}
