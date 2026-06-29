// ABDM records tab — consent-gated. If ABDM consent is NOT active, NO data is
// shown: instead a "no active consent" state offers the lawful options (request
// consent / break-glass emergency access). When consent is active, the list
// shows record metadata (type/ABHA/date) — never clinical content in the list.
// Fetch/Push gate on docslot.abdm.records.read/create.

import { useTranslation } from 'react-i18next';
import { ChevronRight, Download, KeyRound, ShieldX, Upload } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useAbdmRecords, usePushAbdm } from '../api';
import type { ConsentStatus, PurposeOfUse } from '@/lib/mock/contracts';

export function AbdmTab({
  patientId,
  purpose,
  abdmConsent,
}: {
  patientId: string;
  purpose: PurposeOfUse;
  abdmConsent: ConsentStatus | undefined;
}) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const push = usePushAbdm(patientId);
  const consentActive = abdmConsent === 'granted';

  // CONSENT GATE: without active ABDM consent, render NO data — only the lawful
  // options (request consent / break-glass). Data is never fetched here.
  if (!consentActive) {
    return (
      <Card className="p-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <span aria-hidden="true" className="flex h-12 w-12 items-center justify-center rounded-full bg-danger-soft text-danger">
            <ShieldX size={24} />
          </span>
          <p className="text-sm font-medium text-ink">{t('clinical.abdm.noConsentTitle')}</p>
          <p className="max-w-sm text-[13px] text-muted">{t('clinical.abdm.noConsentBody')}</p>
          <div className="mt-1 flex flex-wrap items-center justify-center gap-2">
            <Button variant="ghost" size="sm" onClick={() => toast.success(t('clinical.abdm.consentRequested'))}>
              {t('clinical.abdm.requestConsent')}
            </Button>
            {can('docslot.medical_access.break_glass') ? (
              <Button variant="danger" size="sm" onClick={() => openPanel({ type: 'breakGlass' })}>
                <KeyRound size={14} aria-hidden="true" />
                {t('clinical.abdm.breakGlass')}
              </Button>
            ) : null}
          </div>
        </div>
      </Card>
    );
  }

  return <AbdmList patientId={patientId} purpose={purpose} canPush={can('docslot.abdm.records.create')} pushing={push.isPending} onPush={() => push.mutate({ idempotencyKey: idempotencyKey() }, { onSuccess: () => toast.success(t('clinical.abdm.pushed')) })} />;
}

function AbdmList({
  patientId,
  purpose,
  canPush,
  pushing,
  onPush,
}: {
  patientId: string;
  purpose: PurposeOfUse;
  canPush: boolean;
  pushing: boolean;
  onPush: () => void;
}) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useAbdmRecords(patientId, purpose);
  const openPanel = useUI((s) => s.openPanel);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex justify-end gap-2">
        <Button variant="ghost" size="sm">
          <Download size={14} aria-hidden="true" />
          {t('clinical.abdm.fetch')}
        </Button>
        {canPush ? (
          <Button variant="primary" size="sm" onClick={onPush} disabled={pushing}>
            <Upload size={14} aria-hidden="true" />
            {t('clinical.abdm.push')}
          </Button>
        ) : null}
      </div>

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 2 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-3 w-24" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('clinical.abdm.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((r) => (
              <li key={r.recordId}>
                <button
                  type="button"
                  onClick={() => openPanel({ type: 'abdmDetail', recordId: r.recordId, patientId, purpose })}
                  className="flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left transition-colors last:border-0 hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                >
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-[13px] font-medium text-ink">{r.recordType}</p>
                    <p className="mono text-[11px] text-muted">{r.abhaNumber}</p>
                  </div>
                  {r.isLinkedToPhr ? (
                    <span className="rounded-full bg-primary-soft px-1.5 py-0.5 text-[10px] font-medium text-primary">{t('clinical.abdm.linked')}</span>
                  ) : null}
                  <span className="mono hidden w-24 shrink-0 text-right text-[11px] text-muted-2 md:block">{shortDate(r.createdAt)}</span>
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
