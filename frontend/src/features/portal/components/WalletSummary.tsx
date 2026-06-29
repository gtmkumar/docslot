// Care Partner wallet summary — the commission buckets. Pending / ready-to-pay /
// earned / lifetime-paid plus the current-month total and attribution count. Money
// is mono ₹; colours/spacing from tokens only. Owns its own skeleton/empty/error.

import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { inr } from '@/lib/format';
import { useBrokerWallet } from '../api';

export function WalletSummary() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useBrokerWallet();

  if (isError) {
    return (
      <Card>
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      </Card>
    );
  }

  if (isLoading || !data) {
    return (
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4" role="status" aria-busy="true">
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-24 w-full" />
        ))}
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
      {/* Ready to pay — the headline number, emphasised. */}
      <Card tone="emphasis" className="flex flex-col justify-between p-4">
        <p className="text-[11px] font-semibold uppercase tracking-wider text-bg/60">
          {t('portal.wallet.readyToPay')}
        </p>
        <p className="mono mt-3 text-3xl font-semibold text-bg">{inr(data.readyToPayInr)}</p>
        <p className="mt-1 text-[12px] text-bg/60">{t('portal.wallet.readyToPaySub')}</p>
      </Card>

      <Bucket label={t('portal.wallet.pending')} value={inr(data.pendingInr)} sub={t('portal.wallet.pendingSub')} />
      <Bucket label={t('portal.wallet.earned')} value={inr(data.earnedInr)} sub={t('portal.wallet.earnedSub')} />
      <Bucket label={t('portal.wallet.lifetimePaid')} value={inr(data.lifetimePaidInr)} sub={t('portal.wallet.lifetimePaidSub')} />

      {/* Current month — full-width row across the grid. */}
      <Card className="flex flex-col gap-1 p-4 sm:col-span-2 lg:col-span-4">
        <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          {t('portal.wallet.currentMonth')}
        </p>
        <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
          <p className="mono text-2xl font-semibold text-ink">{inr(data.currentMonthInr)}</p>
          <p className="text-[12px] text-muted">
            {t('portal.wallet.currentMonthAttributions', { count: data.currentMonthAttributions })}
          </p>
        </div>
      </Card>
    </div>
  );
}

function Bucket({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <Card className="flex flex-col justify-between p-4">
      <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{label}</p>
      <p className="mono mt-3 text-2xl font-semibold text-ink">{value}</p>
      <p className="mt-1 text-[12px] text-muted">{sub}</p>
    </Card>
  );
}
