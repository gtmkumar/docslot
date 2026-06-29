// Lab report detail (decrypted) slide-over. Fetched with the declared purpose.
// Renders structured results — treated as PHI (select-none, no bulk copy). A
// critical-findings banner is shown when flagged. NOT URL-addressable.

import { useTranslation } from 'react-i18next';
import { AlertTriangle } from 'lucide-react';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { shortDate } from '@/lib/format';
import { useLabReport } from '../api';
import { ConsentBlocked, isConsentDenied } from './ConsentBlocked';
import type { LabResultRow, PurposeOfUse } from '@/lib/mock/contracts';

const FLAG_TONE: Record<NonNullable<LabResultRow['flag']>, string> = {
  normal: 'text-muted',
  high: 'text-warn',
  low: 'text-warn',
  critical: 'text-danger font-semibold',
};

export function LabReportDetailPanel({
  reportId,
  patientId,
  purpose,
  open,
  onClose,
}: {
  reportId: string;
  patientId: string;
  purpose: PurposeOfUse;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data, isLoading, isError, error, refetch } = useLabReport(reportId, purpose);
  const consentDenied = isError && isConsentDenied(error);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={data?.reportNumber ?? t('clinical.reports.detailTitle')}
      description={t('clinical.reports.detailTitle')}
    >
      {consentDenied ? (
        <ConsentBlocked
          patientId={patientId}
          resourceType="lab_report"
          resourceId={reportId}
          reopen={{ type: 'labReportDetail', reportId, patientId, purpose }}
          onRetry={() => void refetch()}
          inPanel
        />
      ) : isError ? (
        <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : (
        <div className="flex select-none flex-col gap-4">
          <div className="flex items-center justify-between text-[12px] text-muted">
            <span>{data.testName}</span>
            <span className="mono">{shortDate(data.createdAt)}</span>
          </div>

          {data.hasCriticalFindings ? (
            <div className="flex items-center gap-2 rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[12px] font-medium text-danger">
              <AlertTriangle size={15} aria-hidden="true" />
              {t('clinical.reports.criticalBanner')}
            </div>
          ) : null}

          <section>
            <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('clinical.reports.results')}</h3>
            <div className="overflow-hidden rounded-[var(--radius-sm)] border border-line">
              <table className="w-full text-left text-[12px]">
                <thead className="bg-surface-sunk text-[10px] uppercase tracking-wider text-muted-2">
                  <tr>
                    <th className="px-3 py-1.5">{t('clinical.reports.analyte')}</th>
                    <th className="px-3 py-1.5 text-right">{t('clinical.reports.value')}</th>
                    <th className="px-3 py-1.5 text-right">{t('clinical.reports.ref')}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.results.map((row, i) => (
                    <tr key={i} className="border-t border-line">
                      <td className="px-3 py-1.5 text-ink">{row.analyte}</td>
                      <td className={`mono px-3 py-1.5 text-right ${row.flag ? FLAG_TONE[row.flag] : 'text-ink'}`}>
                        {row.value} {row.unit ?? ''}
                      </td>
                      <td className="mono px-3 py-1.5 text-right text-muted">{row.refRange ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </div>
      )}
    </SlideOver>
  );
}
