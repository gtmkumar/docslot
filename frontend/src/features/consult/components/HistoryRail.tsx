// Patient history at the doctor's fingertips — a collapsible rail listing PAST
// VISITS as prescription cards (newest first) + lab reports with critical flags.
// Each past prescription expands (purpose-gated detail fetch) to show its diagnosis
// + meds and offers "Repeat into current Rx" (copies diagnosis + meds into the
// draft, still editable). Lab rows open the existing detail panel. Reads go through
// the seam directly (no cross-feature import).

import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ChevronDown, CopyPlus, FlaskConical, History } from 'lucide-react';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { shortDate } from '@/lib/format';
import { getPrescription, listLabReports, listPrescriptions } from '@/lib/backend';
import { formatMedicationLine } from '@/lib/mock/contracts';
import type { PurposeOfUse, RxMedication } from '@/lib/mock/contracts';
import { useUI } from '@/stores/ui';

const DEVA = /[ऀ-ॿ]/;

export function HistoryRail({
  patientId,
  purpose,
  onRepeat,
}: {
  patientId: string;
  purpose: PurposeOfUse;
  onRepeat: (diagnosis: string | null, meds: RxMedication[]) => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-5">
      <PastVisits patientId={patientId} purpose={purpose} onRepeat={onRepeat} />
      <div>
        <h3 className="mb-2 flex items-center gap-1.5 text-[12px] font-semibold uppercase tracking-wide text-muted">
          <FlaskConical size={14} aria-hidden="true" />
          {t('consult.history.labs')}
        </h3>
        <LabList patientId={patientId} purpose={purpose} />
      </div>
    </div>
  );
}

function PastVisits({ patientId, purpose, onRepeat }: { patientId: string; purpose: PurposeOfUse; onRepeat: (d: string | null, m: RxMedication[]) => void }) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['clinical', 'prescriptions', patientId, purpose] as const,
    queryFn: () => listPrescriptions(patientId, purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
    retry: false,
  });

  return (
    <div>
      <h3 className="mb-2 flex items-center gap-1.5 text-[12px] font-semibold uppercase tracking-wide text-muted">
        <History size={14} aria-hidden="true" />
        {t('consult.history.pastVisits')}
      </h3>
      {isError ? (
        <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-2" role="status" aria-busy="true">
          {Array.from({ length: 2 }).map((_, i) => (
            <Skeleton key={i} className="h-16 w-full rounded-[var(--radius)]" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <EmptyState title={t('consult.history.emptyRx')} />
      ) : (
        <ul className="flex flex-col gap-2">
          {data.map((rx) => (
            <PastRxCard key={rx.prescriptionId} prescriptionId={rx.prescriptionId} doctorName={rx.doctorName} createdAt={rx.createdAt} purpose={purpose} onRepeat={onRepeat} />
          ))}
        </ul>
      )}
    </div>
  );
}

function PastRxCard({
  prescriptionId,
  doctorName,
  createdAt,
  purpose,
  onRepeat,
}: {
  prescriptionId: string;
  doctorName: string;
  createdAt: string;
  purpose: PurposeOfUse;
  onRepeat: (d: string | null, m: RxMedication[]) => void;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const detail = useQuery({
    queryKey: ['clinical', 'prescription', prescriptionId, purpose] as const,
    queryFn: () => getPrescription(prescriptionId, purpose),
    enabled: open,
    retry: false,
  });

  return (
    <li className="overflow-hidden rounded-[var(--radius)] border border-line bg-surface">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-2 px-3 py-2.5 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-primary"
      >
        <div className="min-w-0 flex-1">
          <p className="text-[13px] font-medium text-ink">{doctorName}</p>
          <p className="mono text-[11px] text-muted-2">{shortDate(createdAt)}</p>
        </div>
        <ChevronDown size={16} className={`shrink-0 text-muted-2 transition-transform ${open ? 'rotate-180' : ''}`} aria-hidden="true" />
      </button>

      {open ? (
        <div className="border-t border-line px-3 py-2.5">
          {detail.isLoading || !detail.data ? (
            detail.isError ? (
              <p className="text-[12px] text-danger">{t('consult.history.detailError')}</p>
            ) : (
              <Skeleton className="h-12 w-full" />
            )
          ) : (
            <div className="flex flex-col gap-2">
              {detail.data.diagnosis ? (
                <p className={`text-[12px] text-ink ${DEVA.test(detail.data.diagnosis) ? 'deva' : ''}`}>
                  <span className="font-semibold">{t('consult.diagnosis')}: </span>
                  {detail.data.diagnosis}
                </p>
              ) : null}
              {detail.data.medications.length > 0 ? (
                <ul className="flex flex-col gap-0.5">
                  {detail.data.medications.map((m, i) => (
                    <li key={i} className="text-[12px] text-muted">
                      <span className="font-medium text-ink">{m.name}</span>
                      <span className="mono"> · {formatMedicationLine(m, t)}</span>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="text-[12px] text-muted-2">{t('consult.history.noMeds')}</p>
              )}
              <button
                type="button"
                onClick={() => onRepeat(detail.data.diagnosis, detail.data.medications)}
                className="mt-1 inline-flex w-fit items-center gap-1.5 rounded-[var(--radius-sm)] border border-primary-soft bg-primary-soft px-2.5 py-1.5 text-[12px] font-medium text-primary transition-colors hover:bg-primary hover:text-bg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <CopyPlus size={13} aria-hidden="true" />
                {t('consult.history.repeat')}
              </button>
            </div>
          )}
        </div>
      ) : null}
    </li>
  );
}

function LabList({ patientId, purpose }: { patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const openPanel = useUI((s) => s.openPanel);
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['clinical', 'reports', patientId, purpose] as const,
    queryFn: () => listLabReports(patientId, purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
    retry: false,
  });

  if (isError) {
    return <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />;
  }
  if (isLoading || !data) {
    return (
      <div className="flex flex-col gap-2" role="status" aria-busy="true">
        {Array.from({ length: 2 }).map((_, i) => (
          <Skeleton key={i} className="h-11 w-full rounded-[var(--radius)]" />
        ))}
      </div>
    );
  }
  if (data.length === 0) {
    return <EmptyState title={t('consult.history.emptyLabs')} />;
  }
  return (
    <ul className="flex flex-col gap-2">
      {data.map((r) => (
        <li key={r.reportId}>
          <button
            type="button"
            onClick={() => openPanel({ type: 'labReportDetail', reportId: r.reportId, patientId, purpose })}
            className="flex w-full items-center gap-2 rounded-[var(--radius)] border border-line bg-surface px-3 py-2.5 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
          >
            <div className="min-w-0 flex-1">
              <p className="truncate text-[13px] font-medium text-ink">{r.testName}</p>
              <p className="mono text-[11px] text-muted-2">{shortDate(r.createdAt)}</p>
            </div>
            {r.hasCriticalFindings ? (
              <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-danger-soft px-2 py-0.5 text-[11px] font-medium text-danger">
                {t('consult.history.critical')}
              </span>
            ) : null}
          </button>
        </li>
      ))}
    </ul>
  );
}
