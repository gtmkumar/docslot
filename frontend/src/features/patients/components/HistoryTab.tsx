// Medical history tab — a timeline with create/edit. Purpose-gated: the query is
// disabled until a purpose is declared (handled by the parent gate). Critical
// entries are marked. This DOES render decrypted clinical content (title +
// description) since it's inside the authorized, purpose-declared view.
//
// "Add" gates on docslot.medical_history.create; per-row "Edit" on
// docslot.medical_history.update — both via usePermissions().can() (NO role-in-JSX).
// The create/edit form opens in the right-side slide-over (transient — clinical PHI
// is never URL-encoded).

import { useTranslation } from 'react-i18next';
import { AlertTriangle, Pencil, Plus, Sparkles } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useMedicalHistory } from '../api';
import { ConsentBlocked, isConsentDenied } from './ConsentBlocked';
import type { MedicalHistory, PurposeOfUse } from '@/lib/mock/contracts';

export function HistoryTab({ patientId, purpose }: { patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const { data, isLoading, isError, error, refetch } = useMedicalHistory(patientId, purpose);
  const canEdit = can('docslot.medical_history.update');
  const consentDenied = isError && isConsentDenied(error);

  return (
    <div className="flex flex-col gap-4">
      {can('docslot.medical_history.read') || can('docslot.medical_history.create') ? (
        <div className="flex flex-wrap justify-end gap-2">
          {/* AI RAG ask (Slice 11): patient-bound PHI action → opens a transient
              slide-over (the question is a mutation variable; declared purpose
              forwarded as X-Purpose-Of-Use). */}
          {can('docslot.medical_history.read') ? (
            <Button variant="subtle" size="sm" onClick={() => openPanel({ type: 'ragAsk', patientId, purpose })}>
              <Sparkles size={14} aria-hidden="true" />
              {t('rag.action')}
            </Button>
          ) : null}
          {can('docslot.medical_history.create') ? (
            <Button
              variant="primary"
              size="sm"
              onClick={() => openPanel({ type: 'createHistory', patientId, purpose })}
            >
              <Plus size={14} aria-hidden="true" />
              {t('clinical.history.add')}
            </Button>
          ) : null}
        </div>
      ) : null}

      {consentDenied ? (
        <ConsentBlocked
          patientId={patientId}
          resourceType="medical_history"
          resourceId={null}
          onRetry={() => void refetch()}
        />
      ) : isError ? (
        <Card>
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        </Card>
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-16 w-full" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <Card>
          <EmptyState title={t('clinical.history.empty')} />
        </Card>
      ) : (
        <ol className="flex flex-col gap-2">
          {data.map((h) => (
            <HistoryItem
              key={h.historyId}
              item={h}
              canEdit={canEdit}
              onEdit={() => openPanel({ type: 'editHistory', patientId, purpose, entry: h })}
            />
          ))}
        </ol>
      )}
    </div>
  );
}

function HistoryItem({
  item,
  canEdit,
  onEdit,
}: {
  item: MedicalHistory;
  canEdit: boolean;
  onEdit: () => void;
}) {
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
          <div className="flex shrink-0 items-center gap-2">
            <span className="mono text-[11px] text-muted-2">{shortDate(item.addedAt)}</span>
            {canEdit ? (
              <button
                type="button"
                onClick={onEdit}
                aria-label={t('clinical.history.edit')}
                className="rounded-[var(--radius-sm)] p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <Pencil size={13} aria-hidden="true" />
              </button>
            ) : null}
          </div>
        </div>
      </Card>
    </li>
  );
}
