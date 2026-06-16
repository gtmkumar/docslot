// Medical history tab — a read-only timeline. Purpose-gated: the query is
// disabled until a purpose is declared (handled by the parent gate). Critical
// entries are marked. This DOES render decrypted clinical content (title +
// description) since it's inside the authorized, purpose-declared view.

import { useTranslation } from 'react-i18next';
import { AlertTriangle } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { useMedicalHistory } from '../api';
import type { MedicalHistory, PurposeOfUse } from '@/lib/mock/contracts';

export function HistoryTab({ patientId, purpose }: { patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useMedicalHistory(patientId, purpose);

  if (isError) {
    return (
      <Card>
        <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
      </Card>
    );
  }
  if (isLoading || !data) {
    return (
      <div className="flex flex-col gap-3" role="status" aria-busy="true">
        {Array.from({ length: 3 }).map((_, i) => (
          <Skeleton key={i} className="h-16 w-full" />
        ))}
      </div>
    );
  }
  if (data.length === 0) {
    return (
      <Card>
        <EmptyState title={t('clinical.history.empty')} />
      </Card>
    );
  }

  return (
    <ol className="flex flex-col gap-2">
      {data.map((h) => (
        <HistoryItem key={h.historyId} item={h} />
      ))}
    </ol>
  );
}

function HistoryItem({ item }: { item: MedicalHistory }) {
  const { t } = useTranslation();
  return (
    <li>
      <Card className="p-3">
        <div className="flex items-start gap-2">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="truncate text-[13px] font-medium text-ink">{item.title}</span>
              {item.isCritical ? (
                <span className="inline-flex items-center gap-1 rounded-full bg-danger-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase text-danger">
                  <AlertTriangle size={10} aria-hidden="true" />
                  {t('clinical.history.critical')}
                </span>
              ) : null}
            </div>
            {item.description ? <p className="mt-0.5 text-[12px] text-muted">{item.description}</p> : null}
            <div className="mt-1 flex items-center gap-2 text-[11px] text-muted-2">
              <span className="rounded bg-surface-sunk px-1.5 capitalize">{item.recordType}</span>
              <span>{item.isActive ? t('clinical.history.active') : t('clinical.history.inactive')}</span>
            </div>
          </div>
          <span className="mono shrink-0 text-[11px] text-muted-2">{shortDate(item.addedAt)}</span>
        </div>
      </Card>
    </li>
  );
}
