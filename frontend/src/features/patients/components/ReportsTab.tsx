// Lab reports tab. List (RPT number, test, status, critical flag) — no result
// values shown until a row is opened (with the declared purpose). "Upload" gates
// on docslot.report.upload; "Deliver" on docslot.report.deliver.

import { useTranslation } from 'react-i18next';
import { ChevronRight, Upload } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useDeliverLabReport, useLabReports } from '../api';
import type { LabReportListItem, PurposeOfUse } from '@/lib/mock/contracts';

export function ReportsTab({ patientId, purpose }: { patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useLabReports(patientId, purpose);
  const openPanel = useUI((s) => s.openPanel);

  return (
    <div className="flex flex-col gap-4">
      {can('docslot.report.upload') ? (
        <div className="flex justify-end">
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'uploadReport', patientId })}>
            <Upload size={14} aria-hidden="true" />
            {t('clinical.reports.upload')}
          </Button>
        </div>
      ) : null}

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 3 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-5 w-16" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('clinical.reports.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((r) => (
              <ReportRow key={r.reportId} report={r} patientId={patientId} purpose={purpose} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function ReportRow({ report, patientId, purpose }: { report: LabReportListItem; patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const deliver = useDeliverLabReport(patientId);

  const onDeliver = () => {
    deliver.mutate({ reportId: report.reportId, idempotencyKey: idempotencyKey() }, { onSuccess: () => toast.success(t('clinical.reports.deliverDone')) });
  };

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <button
        type="button"
        onClick={() => openPanel({ type: 'labReportDetail', reportId: report.reportId, patientId, purpose })}
        className="flex min-w-0 flex-1 items-center gap-2 text-left focus-visible:outline-none"
      >
        <div className="min-w-0">
          <p className="truncate text-[13px] font-medium text-ink">{report.testName}</p>
          <p className="mono text-[11px] text-muted">{report.reportNumber ?? '—'}</p>
        </div>
        {report.hasCriticalFindings ? (
          <span className="rounded-full bg-danger-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase text-danger">
            {t('clinical.reports.critical')}
          </span>
        ) : null}
      </button>

      <span className="mono hidden w-24 shrink-0 text-right text-[11px] text-muted-2 md:block">{shortDate(report.createdAt)}</span>
      <span className={`w-20 shrink-0 text-right text-[12px] ${report.status === 'delivered' ? 'text-primary' : 'text-warn'}`}>
        {report.status === 'delivered' ? t('clinical.reports.delivered') : t('clinical.reports.pending')}
      </span>
      {report.status === 'pending' && can('docslot.report.deliver') ? (
        <Button variant="ghost" size="sm" onClick={onDeliver} disabled={deliver.isPending}>
          {t('clinical.reports.deliver')}
        </Button>
      ) : null}
      <ChevronRight size={16} className="shrink-0 text-muted-2" aria-hidden="true" />
    </li>
  );
}
