// Prescriptions tab. The LIST carries no clinical content (PRX number, doctor,
// status, date) — clinical detail is fetched only when a row is opened (with the
// declared purpose). "Issue prescription" gates on docslot.prescription.create.

import { useTranslation } from 'react-i18next';
import { ChevronRight, Plus } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { usePrescriptions } from '../api';
import type { PurposeOfUse } from '@/lib/mock/contracts';

export function PrescriptionsTab({ patientId, purpose }: { patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = usePrescriptions(patientId, purpose);
  const openPanel = useUI((s) => s.openPanel);

  return (
    <div className="flex flex-col gap-4">
      {can('docslot.prescription.create') ? (
        <div className="flex justify-end">
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'issuePrescription', patientId })}>
            <Plus size={14} aria-hidden="true" />
            {t('clinical.rx.issue')}
          </Button>
        </div>
      ) : null}

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ListSkeleton />
        ) : data.length === 0 ? (
          <EmptyState title={t('clinical.rx.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((rx) => (
              <li key={rx.prescriptionId}>
                <button
                  type="button"
                  onClick={() => openPanel({ type: 'prescriptionDetail', prescriptionId: rx.prescriptionId, patientId, purpose })}
                  className="flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left transition-colors last:border-0 hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                >
                  <div className="min-w-0 flex-1">
                    <p className="mono truncate text-[13px] font-medium text-ink">{rx.prescriptionNumber ?? '—'}</p>
                    <p className="text-[12px] text-muted">{rx.doctorName}</p>
                  </div>
                  <span className="hidden w-20 shrink-0 text-right text-[12px] text-muted sm:block">{rx.status}</span>
                  <span className="mono hidden w-24 shrink-0 text-right text-[11px] text-muted-2 md:block">{shortDate(rx.createdAt)}</span>
                  <ChevronRight size={16} className="shrink-0 text-muted-2" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function ListSkeleton() {
  return (
    <ul className="flex flex-col" role="status" aria-busy="true">
      {Array.from({ length: 3 }).map((_, i) => (
        <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
          <div className="flex flex-1 flex-col gap-2">
            <Skeleton className="h-3 w-40" />
            <Skeleton className="h-3 w-24" />
          </div>
          <Skeleton className="h-3 w-16" />
        </li>
      ))}
    </ul>
  );
}
