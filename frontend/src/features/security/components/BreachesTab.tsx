// Breach register tab (DPDP §8(6)). Lists breaches with the 72-hour DPB clock:
// reported breaches show when, unreported ones show time-to-deadline and turn
// OVERDUE (danger) once 72h since discovery has elapsed. "Report breach" gates on
// platform.breach.read (the controller's gate). Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { TriangleAlert } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { relativeTime } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useBreaches } from '../api';
import { SecurityBadge } from './SecurityBadges';
import type { Breach } from '@/lib/mock/contracts';

const DPB_WINDOW_MS = 72 * 3_600_000;

export function BreachesTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useBreaches();
  const openPanel = useUI((s) => s.openPanel);
  const canReport = can('platform.breach.read');

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-[13px] text-muted">{t('security.breaches.sub')}</p>
        {canReport ? (
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'reportBreach' })}>
            <TriangleAlert size={14} aria-hidden="true" />
            {t('security.breaches.report')}
          </Button>
        ) : null}
      </div>

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 3 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-5 w-16" />
                <Skeleton className="h-5 w-20" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('security.breaches.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((b) => (
              <BreachRow key={b.breachId} breach={b} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function BreachRow({ breach }: { breach: Breach }) {
  const { t } = useTranslation();
  const detected = new Date(breach.detectedAt).getTime();
  const deadline = detected + DPB_WINDOW_MS;
  const reported = Boolean(breach.reportedToDpbAt);
  const overdue = !reported && Date.now() > deadline;

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-[13px] font-medium text-ink">{breach.breachType.replace(/_/g, ' ')}</span>
          <SecurityBadge tone={breach.severity} label={t(`security.breach.sev${breach.severity.charAt(0).toUpperCase()}${breach.severity.slice(1)}`)} dot={false} />
        </div>
        <p className="truncate text-[12px] text-muted">{breach.description}</p>
      </div>

      <span className="mono hidden w-20 shrink-0 text-right text-[11px] text-muted md:block">
        {breach.affectedRecordCount != null ? t('security.breaches.records', { count: breach.affectedRecordCount }) : '—'}
      </span>

      {/* 72h DPB clock */}
      <div className="hidden w-28 shrink-0 text-right sm:block">
        {reported ? (
          <SecurityBadge tone="reported" label={t('security.breaches.reported')} />
        ) : overdue ? (
          <SecurityBadge tone="overdue" label={t('security.breaches.overdue')} />
        ) : (
          <span className="mono text-[11px] text-warn">{t('security.breaches.dueIn', { time: relativeTime(new Date(deadline).toISOString()).replace('in ', '') })}</span>
        )}
      </div>

      <span className="hidden w-20 shrink-0 text-right lg:block">
        <SecurityBadge tone={breach.resolvedAt ? 'resolved' : 'open'} label={breach.resolvedAt ? t('security.breaches.resolved') : t('security.breaches.open')} dot={false} />
      </span>
    </li>
  );
}
