// On the floor now (image copy.png): doctors with active OPD, room, next slot
// (explicit IST), seen-today count, and a Roster link.

import { Link } from '@tanstack/react-router';
import { ArrowRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { Card } from '@/components/ui/Card';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { istSlot } from '@/lib/format';
import { useFloorDoctors } from '../api';

export function OnTheFloor() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useFloorDoctors();

  return (
    <Card className="flex flex-col gap-3 p-4">
      <header className="flex items-center justify-between">
        <div>
          <h2 className="text-sm font-semibold text-ink">{t('overview.onTheFloor')}</h2>
          <p className="text-[12px] text-muted">{t('overview.onTheFloorSub')}</p>
        </div>
        <Link
          to="/doctors"
          className="inline-flex items-center gap-1 text-[13px] font-medium text-primary hover:underline"
        >
          {t('overview.roster')}
          <ArrowRight size={14} aria-hidden="true" />
        </Link>
      </header>

      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !data ? (
        <ul className="flex flex-col gap-2" role="status" aria-busy="true">
          {Array.from({ length: 5 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3 py-1.5">
              <Skeleton className="h-9 w-9 rounded-full" />
              <Skeleton className="h-3 flex-1" />
              <Skeleton className="h-3 w-12" />
            </li>
          ))}
        </ul>
      ) : data.length === 0 ? (
        <EmptyState title={t('overview.emptyQueueTitle')} description={t('overview.onTheFloorSub')} />
      ) : (
        <ul className="flex flex-col">
          {data.map((d) => (
            <li key={d.id} className="flex items-center gap-3 border-b border-line py-2.5 last:border-0">
              <Avatar name={d.name} initials={d.initials} size="sm" />
              <div className="min-w-0 flex-1">
                <p className="truncate text-[13px] font-medium text-ink">{d.name}</p>
                <p className="text-[11px] text-muted">
                  {d.room ? `${d.spec} · ${d.room}` : d.spec}
                </p>
              </div>
              <div className="shrink-0 text-right">
                <p className="text-[11px] text-muted-2">{t('overview.nextSlot')}</p>
                <p className="mono text-[13px] text-ink">{d.nextSlot ? istSlot(d.nextSlot) : '—'}</p>
              </div>
              <span className="mono w-12 shrink-0 text-right text-[12px] text-muted">
                {d.seenToday} {t('overview.seenToday')}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
