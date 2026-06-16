// Department load today (image copy.png): per-department booked/capacity bars.
// Bar color comes from a token colorKey resolved server-side (no hex in JSX).

import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { useDepartmentLoad } from '../api';

export function DepartmentLoad() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useDepartmentLoad();

  return (
    <Card className="flex flex-col gap-4 p-4">
      <header>
        <h2 className="text-sm font-semibold text-ink">{t('overview.deptLoad')}</h2>
        <p className="text-[12px] text-muted">{t('overview.deptLoadSub')}</p>
      </header>

      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !data ? (
        <ul className="flex flex-col gap-3" role="status" aria-busy="true">
          {Array.from({ length: 6 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3">
              <Skeleton className="h-3 w-28" />
              <Skeleton className="h-2 flex-1" />
              <Skeleton className="h-3 w-10" />
            </li>
          ))}
        </ul>
      ) : (
        <ul className="flex flex-col gap-3">
          {data.map((d) => (
            <li key={d.id} className="flex items-center gap-3">
              <span className="flex w-32 shrink-0 items-center gap-2 text-[13px] text-ink">
                <span className={`h-2 w-2 shrink-0 rounded-full ${dot(d.colorKey)}`} aria-hidden="true" />
                <span className="truncate">{d.name}</span>
              </span>
              <ProgressBar
                value={d.booked}
                max={d.capacity}
                colorKey={d.colorKey}
                label={`${d.name}: ${d.booked} of ${d.capacity}`}
              />
              <span className="mono w-12 shrink-0 text-right text-[12px] text-muted">
                {d.booked}/{d.capacity}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}

const DOT: Record<string, string> = {
  primary: 'bg-primary',
  accent: 'bg-accent',
  info: 'bg-info',
  warn: 'bg-warn',
  muted: 'bg-muted',
};
function dot(key: string): string {
  return DOT[key] ?? DOT.primary;
}
