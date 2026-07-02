// Medical-history batch viewer — opened from a timeline "medical_history_batch"
// card. Lists the rows that came in with one import batch (or a single clinic
// row), each with its paper-Rx badge, attachment view, Verify (unverified external
// rows, gated update) and edit. Reads flow through the purpose-gated history query;
// a consent denial surfaces the break-glass affordance. NOT URL-addressable.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, BadgeCheck, Paperclip, Pencil } from 'lucide-react';
import { toast } from 'sonner';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { ExternalRecordBadge } from '@/components/ui/ExternalRecordBadge';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { shortDate } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useMedicalHistory, useVerifyMedicalHistory } from '../api';
import { ConsentBlocked, isConsentDenied } from './ConsentBlocked';
import { isUnverifiedExternal, type MedicalHistory, type PurposeOfUse } from '@/lib/mock/contracts';

export function HistoryBatchPanel({
  batchId,
  patientId,
  purpose,
  open,
  onClose,
}: {
  batchId: string;
  patientId: string;
  purpose: PurposeOfUse;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data, isLoading, isError, error, refetch } = useMedicalHistory(patientId, purpose);
  const consentDenied = isError && isConsentDenied(error);

  // A batch groups rows sharing an importBatchId; a clinic row is its own batch
  // (ref id = historyId), so match either.
  const rows = (data ?? []).filter((h) => h.importBatchId === batchId || h.historyId === batchId);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('clinical.tabHistory')}
      description={t('clinical.tabHistory')}
    >
      {consentDenied ? (
        <ConsentBlocked
          patientId={patientId}
          resourceType="medical_history"
          resourceId={null}
          reopen={{ type: 'historyBatch', batchId, patientId, purpose }}
          onRetry={() => void refetch()}
          inPanel
        />
      ) : isError ? (
        <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-16 w-full" />
          <Skeleton className="h-16 w-full" />
        </div>
      ) : rows.length === 0 ? (
        <EmptyState title={t('clinical.history.empty')} />
      ) : (
        <ol className="flex flex-col gap-2">
          {rows.map((h) => (
            <BatchRow key={h.historyId} item={h} patientId={patientId} purpose={purpose} />
          ))}
        </ol>
      )}
    </SlideOver>
  );
}

function BatchRow({ item, patientId, purpose }: { item: MedicalHistory; patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const canEdit = can('docslot.medical_history.update');
  const unverified = isUnverifiedExternal(item);

  return (
    <li className="rounded-[var(--radius)] border border-line p-3">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="truncate text-[13px] font-medium text-ink">{item.title}</span>
            {item.isCritical ? (
              <span className="inline-flex items-center gap-1 rounded-full bg-danger-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase text-danger">
                <AlertTriangle size={10} aria-hidden="true" />
                {t('clinical.history.critical')}
              </span>
            ) : null}
            <ExternalRecordBadge source={item.source} verifiedAt={item.verifiedAt} externalDoctorName={item.externalDoctorName} />
          </div>
          {item.description ? <p className="mt-0.5 text-[12px] text-muted">{item.description}</p> : null}
          <div className="mt-1 flex flex-wrap items-center gap-2 text-[11px] text-muted-2">
            <span className="rounded bg-surface-sunk px-1.5 capitalize">{item.recordType}</span>
            <span>{item.isActive ? t('clinical.history.active') : t('clinical.history.inactive')}</span>
            {item.attachmentFileName ? (
              <button
                type="button"
                onClick={() => openPanel({ type: 'historyAttachment', patientId, historyId: item.historyId, purpose, fileName: item.attachmentFileName ?? '' })}
                className="inline-flex items-center gap-1 rounded px-1 text-primary transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <Paperclip size={11} aria-hidden="true" />
                {t('clinical.history.attachment.view')}
              </button>
            ) : null}
          </div>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-2">
          <span className="mono text-[11px] text-muted-2">{shortDate(item.addedAt)}</span>
          <div className="flex items-center gap-1">
            {unverified && canEdit ? <VerifyButton patientId={patientId} historyId={item.historyId} /> : null}
            {canEdit ? (
              <button
                type="button"
                onClick={() => openPanel({ type: 'editHistory', patientId, purpose, entry: item })}
                aria-label={t('clinical.history.edit')}
                className="rounded-[var(--radius-sm)] p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <Pencil size={13} aria-hidden="true" />
              </button>
            ) : null}
          </div>
        </div>
      </div>
    </li>
  );
}

function VerifyButton({ patientId, historyId }: { patientId: string; historyId: string }) {
  const { t } = useTranslation();
  const verify = useVerifyMedicalHistory(patientId);
  const [pending, setPending] = useState(false);
  const onVerify = async () => {
    setPending(true);
    try {
      await verify.mutateAsync({ historyId, idempotencyKey: idempotencyKey() });
      toast.success(t('clinical.history.external.verified'));
    } catch (e) {
      toast.error(toUserError(e));
    } finally {
      setPending(false);
    }
  };
  return (
    <button
      type="button"
      disabled={pending}
      onClick={() => void onVerify()}
      className="inline-flex items-center gap-1 rounded-[var(--radius-sm)] border border-primary-soft bg-primary-soft px-2 py-1 text-[11px] font-medium text-primary transition-colors hover:bg-primary hover:text-bg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:cursor-not-allowed disabled:opacity-50"
    >
      <BadgeCheck size={12} aria-hidden="true" />
      {t('clinical.history.external.verify')}
    </button>
  );
}
