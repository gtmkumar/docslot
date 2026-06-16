// The 4 top stat cards (image.png): LIVE QUEUE (dark card + Review button),
// CONFIRMED TODAY, TODAY'S REVENUE (mono ₹), NO-SHOW RATE. Each carries the
// small sub-metric line from the prototype. Colors/spacing via tokens only.

import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { inr } from '@/lib/format';
import type { DashboardSummary } from '@/lib/mock/contracts';

export function StatCards({ summary, onReview }: { summary: DashboardSummary; onReview: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
      {/* Live queue — emphasised dark card. tone="emphasis" sets bg-ink + light
          text on the Card itself, so the number + Review stay visible in both
          light and dark themes (no bg-surface/bg-ink class conflict). */}
      <Card tone="emphasis" className="flex flex-col justify-between p-4">
        <div className="flex items-start justify-between">
          <p className="text-[11px] font-semibold uppercase tracking-wider text-bg/60">
            {t('overview.statLiveQueue')}
          </p>
          <Button variant="danger" size="sm" onClick={onReview}>
            {t('overview.statReview')}
          </Button>
        </div>
        <p className="mono mt-3 text-4xl font-semibold text-bg">{summary.liveQueue}</p>
        <p className="mt-1 text-[12px] text-bg/60">
          {t('overview.statLiveQueueSub', { whatsapp: summary.liveQueueWhatsapp, walkin: summary.liveQueueWalkIn })}
        </p>
      </Card>

      <StatCard
        label={t('overview.statConfirmedToday')}
        value={String(summary.confirmedToday)}
        sub={t('overview.statLiveQueuePending', { count: summary.liveQueue })}
      />
      <StatCard
        label={t('overview.statRevenueToday')}
        value={inr(summary.revenueToday)}
        sub={t('overview.revenueSub')}
        mono
      />
      <StatCard
        label={t('overview.statNoShowRate')}
        value={`${summary.noShowRate}%`}
        sub={t('overview.noShowSub')}
        mono
      />
    </div>
  );
}

function StatCard({ label, value, sub, mono }: { label: string; value: string; sub: string; mono?: boolean }) {
  return (
    <Card className="flex flex-col justify-between p-4">
      <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{label}</p>
      <p className={`mt-3 text-3xl font-semibold text-ink ${mono ? 'mono' : ''}`}>{value}</p>
      <p className="mt-1 text-[12px] text-muted">{sub}</p>
    </Card>
  );
}
