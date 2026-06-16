// Attribution ledger. booking, Care Partner, mechanism, verification, commission
// amount, and a FRAUD FLAG when fraud_score > 0.5. Raise-dispute gates on
// commission.dispute.raise. PHI: patient = first name + masked phone only.

import { useTranslation } from 'react-i18next';
import { TriangleAlert } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { inr } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useAttributions } from '../api';
import { CommissionBadge } from './CommissionBadge';
import type { Attribution } from '@/lib/mock/contracts';

const FRAUD_THRESHOLD = 0.5;

export function AttributionsTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useAttributions();

  return (
    <div className="flex flex-col gap-4">
      <p className="text-[13px] text-muted">{t('commission.attributions.sub')}</p>
      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 4 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-5 w-24" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('commission.attributions.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((a) => (
              <AttributionRow key={a.attributionId} attr={a} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function AttributionRow({ attr }: { attr: Attribution }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const flagged = attr.fraudScore > FRAUD_THRESHOLD;

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="mono truncate text-[12px] font-medium text-ink">{attr.bookingRef}</span>
          {flagged ? (
            <span className="inline-flex items-center gap-1 rounded-full bg-danger-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase text-danger">
              <TriangleAlert size={10} aria-hidden="true" />
              {t('commission.attributions.flagged')}
            </span>
          ) : null}
        </div>
        <p className="truncate text-[11px] text-muted">{attr.brokerName}</p>
      </div>

      {/* PHI: first name + masked phone only. */}
      <span className="mono hidden w-32 shrink-0 truncate text-[11px] text-muted md:block">
        {attr.patientFirstName} · {attr.patientMaskedPhone}
      </span>

      <span className="hidden w-24 shrink-0 text-[11px] text-muted lg:block">{t(`commission.attributions.source.${attr.attributionSource}`)}</span>

      <span className="hidden w-28 shrink-0 sm:block">
        <CommissionBadge tone={attr.verificationStatus === 'patient_denied' ? 'denied' : attr.verificationStatus === 'pending' ? 'pending' : 'ok'} label={t(`commission.attributions.verification.${attr.verificationStatus}`)} dot={false} />
      </span>

      <span className="mono w-20 shrink-0 text-right text-[12px] text-ink">{attr.commissionAmountInr != null ? inr(attr.commissionAmountInr) : '—'}</span>

      {can('commission.dispute.raise') ? (
        <Button variant="ghost" size="sm" onClick={() => openPanel({ type: 'raiseDispute', attributionId: attr.attributionId })}>
          {t('commission.attributions.raiseDispute')}
        </Button>
      ) : null}
    </li>
  );
}
