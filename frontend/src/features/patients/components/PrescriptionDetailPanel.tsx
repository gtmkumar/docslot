// Prescription detail (decrypted) slide-over. Fetched with the declared purpose.
// Renders sensitive clinical content (complaints/examination/diagnosis/meds) —
// but treats it as PHI: no copy-to-clipboard of bulk content, text not
// selectable for bulk export, nothing logged. NOT URL-addressable (the panel
// carries the purpose + a PHI id).

import { useTranslation } from 'react-i18next';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { shortDate } from '@/lib/format';
import { usePrescription } from '../api';
import type { PurposeOfUse } from '@/lib/mock/contracts';

export function PrescriptionDetailPanel({
  prescriptionId,
  purpose,
  open,
  onClose,
}: {
  prescriptionId: string;
  purpose: PurposeOfUse;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = usePrescription(prescriptionId, purpose);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={data?.prescriptionNumber ?? t('clinical.rx.detailTitle')}
      description={t('clinical.rx.detailTitle')}
    >
      {isError ? (
        <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-20 w-full" />
          <Skeleton className="h-20 w-full" />
        </div>
      ) : (
        // select-none discourages bulk PHI selection/copy.
        <div className="flex select-none flex-col gap-4">
          <div className="flex items-center justify-between text-[12px] text-muted">
            <span>{data.doctorName}</span>
            <span className="mono">{shortDate(data.createdAt)}</span>
          </div>

          <Field label={t('clinical.rx.chiefComplaints')} value={data.chiefComplaints} />
          <Field label={t('clinical.rx.examination')} value={data.examination} />
          <Field label={t('clinical.rx.diagnosis')} value={data.diagnosis} />

          <section>
            <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('clinical.rx.medications')}</h3>
            <ul className="flex flex-col gap-1.5">
              {data.medications.map((m, i) => (
                <li key={i} className="rounded-[var(--radius-sm)] border border-line px-3 py-2">
                  <p className="text-[13px] font-medium text-ink">{m.name}</p>
                  <p className="mono text-[11px] text-muted">
                    {m.dose} · {m.frequency} · {m.duration}
                  </p>
                </li>
              ))}
            </ul>
          </section>

          <Field label={t('clinical.rx.advice')} value={data.advice} />
          {data.followUpInDays ? (
            <p className="text-[12px] text-muted">{t('clinical.rx.followUp', { days: data.followUpInDays })}</p>
          ) : null}
        </div>
      )}
    </SlideOver>
  );
}

function Field({ label, value }: { label: string; value: string | null }) {
  if (!value) return null;
  return (
    <div>
      <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{label}</h3>
      <p className="text-[13px] text-ink">{value}</p>
    </div>
  );
}
