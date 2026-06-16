// DPDP rights tab. Data-subject requests table (export/erasure/correction) with
// status; header actions to open the Export (gated platform.export_requests.process)
// and Erase (gated platform.deletion.certify) slide-overs. An erasure-kind row
// offers a direct "Erase" action pre-filled with its requestId.
// PHI: subject identity is a MASKED phone only — never a name.

import { useTranslation } from 'react-i18next';
import { Download, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useDpdpRequests } from '../api';
import { SecurityBadge, SensitiveTag } from './SecurityBadges';
import type { DpdpRequest } from '@/lib/mock/contracts';

export function DpdpTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useDpdpRequests();
  const openPanel = useUI((s) => s.openPanel);
  const canExport = can('platform.export_requests.process');
  const canErase = can('platform.deletion.certify');

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-[13px] text-muted">{t('security.dpdp.sub')}</p>
        <div className="flex items-center gap-2">
          {canExport ? (
            <Button variant="ghost" size="sm" onClick={() => openPanel({ type: 'exportData' })}>
              <Download size={14} aria-hidden="true" />
              {t('security.dpdp.export')}
            </Button>
          ) : null}
          {canErase ? (
            <Button variant="danger" size="sm" onClick={() => openPanel({ type: 'eraseData' })}>
              <Trash2 size={14} aria-hidden="true" />
              {t('security.dpdp.erase')}
              <SensitiveTag label={t('security.irreversible')} tone="danger" />
            </Button>
          ) : null}
        </div>
      </div>

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 3 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 w-20" />
                <Skeleton className="h-3 w-28" />
                <Skeleton className="ml-auto h-5 w-20" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('security.dpdp.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((r) => (
              <DpdpRow key={r.requestId} req={r} canErase={canErase} onErase={() => openPanel({ type: 'eraseData', requestId: r.requestId })} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function DpdpRow({ req, canErase, onErase }: { req: DpdpRequest; canErase: boolean; onErase: () => void }) {
  const { t } = useTranslation();
  const kindLabel =
    req.kind === 'export' ? t('security.dpdp.kindExport') : req.kind === 'erasure' ? t('security.dpdp.kindErasure') : t('security.dpdp.kindCorrection');
  const statusLabel = t(`security.dpdp.status${req.status.charAt(0).toUpperCase()}${req.status.slice(1)}`);

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <span className="w-24 shrink-0 text-[13px] font-medium text-ink">{kindLabel}</span>
      {/* PHI: masked phone only. */}
      <span className="mono min-w-0 flex-1 truncate text-[12px] text-muted">{req.subjectMaskedPhone}</span>
      <SecurityBadge tone={req.status} label={statusLabel} />
      <span className="mono hidden w-24 shrink-0 text-right text-[11px] text-muted-2 sm:block">{shortDate(req.createdAt)}</span>
      {canErase && req.kind === 'erasure' && req.status === 'pending' ? (
        <Button variant="danger" size="sm" onClick={onErase}>
          {t('security.dpdp.eraseAction')}
        </Button>
      ) : null}
    </li>
  );
}
