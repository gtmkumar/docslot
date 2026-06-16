// Analytics (/analytics) — "This month". Month/Quarter/Year toggle; KPI cards
// with trend deltas; a stacked weekly volume bar chart (WhatsApp vs direct); a
// top-departments horizontal ranking; and a WhatsApp conversation funnel. Charts
// are lightweight inline SVG/CSS (token-driven widths) — NO chart dependency.
// Loading / empty / error states all implemented. No role branches; the screen
// gates on docslot.analytics.read.

import { useState } from 'react';
import { TrendingDown, TrendingUp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import type { Analytics, AnalyticsKpi, FunnelStep, TopDepartment, VolumeBar } from '@/lib/mock/contracts';
import { usePermissions } from '@/lib/permissions';
import { useAnalytics } from './api';

type Range = 'month' | 'quarter' | 'year';

const KPI_LABEL: Record<AnalyticsKpi['key'], string> = {
  totalBookings: 'analytics.kpiTotalBookings',
  whatsappShare: 'analytics.kpiWhatsappShare',
  noShowRate: 'analytics.kpiNoShowRate',
  revenue: 'analytics.kpiRevenue',
};

const FUNNEL_LABEL: Record<FunnelStep['key'], string> = {
  startedChat: 'analytics.funnelStartedChat',
  pickedDept: 'analytics.funnelPickedDept',
  pickedDoctor: 'analytics.funnelPickedDoctor',
  pickedSlot: 'analytics.funnelPickedSlot',
  confirmed: 'analytics.funnelConfirmed',
};

export function AnalyticsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const [range, setRange] = useState<Range>('month');
  const { data, isLoading, isError, refetch } = useAnalytics(range);

  // Permission gate — the screen is only reachable via backend-driven nav, but we
  // still fail-closed if the effective set lacks analytics.read.
  if (!can('docslot.analytics.read')) {
    return (
      <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
        <Header range={range} onRange={setRange} hideToggle />
        <Card>
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} />
        </Card>
      </section>
    );
  }

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <Header range={range} onRange={setRange} />

      {isError ? (
        <Card>
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        </Card>
      ) : isLoading || !data ? (
        <AnalyticsSkeleton />
      ) : (
        <AnalyticsBody data={data} />
      )}
    </section>
  );
}

function Header({ range, onRange, hideToggle }: { range: Range; onRange: (r: Range) => void; hideToggle?: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('analytics.title')}
        </h1>
        <p className="mt-1 text-[13px] text-muted">{t('analytics.subtitle')}</p>
      </div>
      {hideToggle ? null : (
        <div className="inline-flex rounded-[var(--radius-sm)] border border-line bg-surface p-0.5" role="tablist">
          {(['month', 'quarter', 'year'] as Range[]).map((r) => (
            <button
              key={r}
              type="button"
              role="tab"
              aria-selected={range === r}
              onClick={() => onRange(r)}
              className={`rounded-[6px] px-3 py-1 text-[13px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ${
                range === r ? 'bg-primary text-bg' : 'text-muted hover:text-ink'
              }`}
            >
              {t(`analytics.range${r.charAt(0).toUpperCase()}${r.slice(1)}`)}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function AnalyticsBody({ data }: { data: Analytics }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-5">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {data.kpis.map((kpi) => (
          <KpiCard key={kpi.key} kpi={kpi} />
        ))}
      </div>

      <div className="grid gap-5 lg:grid-cols-[1.4fr_1fr]">
        <Card className="flex flex-col gap-4 p-4">
          <header>
            <h2 className="text-sm font-semibold text-ink">{t('analytics.volumeTitle')}</h2>
            <p className="text-[12px] text-muted">{t('analytics.volumeSub')}</p>
          </header>
          <VolumeChart bars={data.volume} />
        </Card>

        <Card className="flex flex-col gap-4 p-4">
          <header>
            <h2 className="text-sm font-semibold text-ink">{t('analytics.topDeptTitle')}</h2>
            <p className="text-[12px] text-muted">{t('analytics.topDeptSub')}</p>
          </header>
          <TopDepartments rows={data.topDepartments} />
        </Card>
      </div>

      <Card className="flex flex-col gap-4 p-4">
        <header>
          <h2 className="text-sm font-semibold text-ink">{t('analytics.funnelTitle')}</h2>
          <p className="text-[12px] text-muted">{t('analytics.funnelSub')}</p>
        </header>
        <Funnel steps={data.funnel} />
      </Card>
    </div>
  );
}

function KpiCard({ kpi }: { kpi: AnalyticsKpi }) {
  const { t } = useTranslation();
  const up = kpi.deltaPct >= 0;
  // "Good" = up when higherIsBetter, down otherwise. Drives the delta colour
  // (primary = good, accent = bad). Never colour-only: an arrow icon accompanies.
  const good = up === kpi.higherIsBetter;
  const Icon = up ? TrendingUp : TrendingDown;
  return (
    <Card className="flex flex-col justify-between gap-2 p-4">
      <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t(KPI_LABEL[kpi.key])}</p>
      <p className="mono text-3xl font-semibold text-ink">{kpi.value}</p>
      <div className="flex items-center gap-2 text-[12px]">
        <span className={`inline-flex items-center gap-1 font-medium ${good ? 'text-primary' : 'text-accent'}`}>
          <Icon size={13} aria-hidden="true" />
          {t(up ? 'analytics.deltaUp' : 'analytics.deltaDown', { value: Math.abs(kpi.deltaPct) })}
        </span>
        {kpi.caption ? <span className="text-muted-2">{kpi.caption}</span> : null}
      </div>
    </Card>
  );
}

// Stacked vertical bars: WhatsApp (teal) + direct (muted). Heights are token-driven
// percentages of the tallest day. Pure CSS — no SVG, no chart lib.
function VolumeChart({ bars }: { bars: VolumeBar[] }) {
  const { t } = useTranslation();
  const max = Math.max(...bars.map((b) => b.whatsapp + b.direct), 1);
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-end justify-between gap-2" style={{ height: 160 }} role="img" aria-label={t('analytics.volumeTitle')}>
        {bars.map((b) => {
          const total = b.whatsapp + b.direct;
          const totalPct = (total / max) * 100;
          const waShare = total > 0 ? (b.whatsapp / total) * 100 : 0;
          return (
            <div key={b.weekday} className="flex h-full flex-1 flex-col items-center justify-end gap-1.5">
              <span className="mono text-[10px] text-muted-2">{total}</span>
              <div
                className="flex w-full max-w-9 flex-col-reverse overflow-hidden rounded-[var(--radius-sm)] bg-surface-sunk"
                style={{ height: `${totalPct}%` }}
              >
                <div className="w-full bg-whatsapp" style={{ height: `${waShare}%` }} aria-hidden="true" />
                <div className="w-full bg-muted-2" style={{ height: `${100 - waShare}%` }} aria-hidden="true" />
              </div>
              <span className="text-[11px] text-muted">{b.weekday}</span>
            </div>
          );
        })}
      </div>
      <div className="flex items-center gap-4">
        <LegendDot className="bg-whatsapp" label={t('analytics.legendWhatsapp')} />
        <LegendDot className="bg-muted-2" label={t('analytics.legendDirect')} />
      </div>
    </div>
  );
}

function TopDepartments({ rows }: { rows: TopDepartment[] }) {
  const { t } = useTranslation();
  const max = Math.max(...rows.map((r) => r.bookings), 1);
  return (
    <ul className="flex flex-col gap-3">
      {rows.map((d) => (
        <li key={d.id} className="flex flex-col gap-1">
          <div className="flex items-center justify-between text-[12px]">
            <span className="text-ink">{d.name}</span>
            <span className="mono text-muted">{t('analytics.bookings', { count: d.bookings })}</span>
          </div>
          <ProgressBar value={d.bookings} max={max} colorKey={d.colorKey} label={d.name} />
        </li>
      ))}
    </ul>
  );
}

// Conversation funnel: each step's bar width = its pct, with the count + pct.
function Funnel({ steps }: { steps: FunnelStep[] }) {
  const { t } = useTranslation();
  return (
    <ol className="flex flex-col gap-2.5">
      {steps.map((step) => (
        <li key={step.key}>
          <div className="mb-1 flex items-center justify-between text-[12px]">
            <span className="text-ink">{t(FUNNEL_LABEL[step.key])}</span>
            <span className="mono text-muted">
              {step.count.toLocaleString('en-IN')} · {step.pct}%
            </span>
          </div>
          <ProgressBar value={step.pct} colorKey="whatsapp" label={t(FUNNEL_LABEL[step.key])} />
        </li>
      ))}
    </ol>
  );
}

function LegendDot({ className, label }: { className: string; label: string }) {
  return (
    <span className="flex items-center gap-1.5 text-[12px] text-muted">
      <span className={`h-2.5 w-2.5 rounded-[3px] ${className}`} aria-hidden="true" />
      {label}
    </span>
  );
}

function AnalyticsSkeleton() {
  return (
    <div className="flex flex-col gap-5" role="status" aria-busy="true">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <Card key={i} className="p-4">
            <Skeleton className="mb-3 h-3 w-24" />
            <Skeleton className="h-8 w-20" />
            <Skeleton className="mt-2 h-3 w-28" />
          </Card>
        ))}
      </div>
      <div className="grid gap-5 lg:grid-cols-[1.4fr_1fr]">
        <Card className="p-4">
          <Skeleton className="mb-4 h-4 w-48" />
          <Skeleton className="h-40 w-full" />
        </Card>
        <Card className="p-4">
          <Skeleton className="mb-4 h-4 w-40" />
          <div className="flex flex-col gap-3">
            {Array.from({ length: 6 }).map((_, i) => (
              <Skeleton key={i} className="h-3 w-full" />
            ))}
          </div>
        </Card>
      </div>
      <Card className="p-4">
        <Skeleton className="mb-4 h-4 w-56" />
        <div className="flex flex-col gap-3">
          {Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-3 w-full" />
          ))}
        </div>
      </Card>
    </div>
  );
}
