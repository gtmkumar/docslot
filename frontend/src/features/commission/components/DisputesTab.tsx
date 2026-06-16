// Disputes list + resolve workflow. Resolve gates on commission.dispute.resolve.

import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useDisputes } from '../api';
import { CommissionBadge } from './CommissionBadge';
import type { Dispute } from '@/lib/mock/contracts';

export function DisputesTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useDisputes();

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[13px] text-muted">{t('commission.disputes.sub')}</p>
      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 2 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-5 w-24" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('commission.disputes.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((d) => (
              <DisputeRow key={d.disputeId} dispute={d} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function DisputeRow({ dispute }: { dispute: Dispute }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const openState = dispute.status === 'open' || dispute.status === 'investigating';
  const tone = openState ? 'pending' : dispute.status === 'resolved_tenant_wins' || dispute.status === 'closed_no_action' ? 'inactive' : 'ok';

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <div className="min-w-0 flex-1">
        <span className="mono truncate text-[12px] font-medium text-ink">{dispute.bookingRef}</span>
        <p className="truncate text-[11px] text-muted">{dispute.brokerName} · {dispute.disputeReason.replace(/_/g, ' ')}</p>
      </div>
      <span className="hidden w-24 shrink-0 text-[11px] text-muted sm:block">{dispute.raisedBy}</span>
      <span className="mono hidden w-24 shrink-0 text-right text-[11px] text-muted-2 md:block">{shortDate(dispute.raisedAt)}</span>
      <CommissionBadge tone={tone} label={t(`commission.disputes.status.${dispute.status}`)} dot={false} />
      {openState && can('commission.dispute.resolve') ? (
        <Button variant="ghost" size="sm" onClick={() => openPanel({ type: 'resolveDispute', disputeId: dispute.disputeId })}>
          {t('commission.disputes.resolve')}
        </Button>
      ) : null}
    </li>
  );
}
