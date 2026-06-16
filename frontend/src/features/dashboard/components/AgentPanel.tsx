// WhatsApp agent panel (image.png right column): active-conversations count,
// an SVG sparkline, a 2x2 metrics grid, and the today funnel as labeled progress
// bars. All colors via tokens (SVG uses currentColor / token text classes).

import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { useAgentPanel } from '../api';
import type { AgentPanel as AgentPanelData } from '@/lib/mock/contracts';

export function AgentPanel() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useAgentPanel();

  return (
    <Card className="flex flex-col gap-4 p-4">
      <header className="flex items-center justify-between">
        <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('agent.label')}</p>
        <span className="inline-flex items-center gap-1.5 text-[12px] text-whatsapp-ink">
          <span className="h-2 w-2 rounded-full bg-whatsapp" aria-hidden="true" />
          {t('agent.online')}
        </span>
      </header>

      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-4" role="status" aria-busy="true">
          <Skeleton className="h-10 w-24" />
          <Skeleton className="h-12 w-full" />
          <div className="grid grid-cols-2 gap-3">
            {Array.from({ length: 4 }).map((_, i) => (
              <Skeleton key={i} className="h-12 w-full" />
            ))}
          </div>
        </div>
      ) : (
        <AgentBody data={data} />
      )}
    </Card>
  );
}

function AgentBody({ data }: { data: AgentPanelData }) {
  const { t } = useTranslation();
  return (
    <>
      <div className="flex items-end justify-between gap-3">
        <div>
          <p className="mono text-3xl font-semibold text-ink">{data.activeConversations}</p>
          <p className="text-[12px] text-muted">{t('agent.activeConversations')}</p>
        </div>
        <Sparkline values={data.sparkline} />
      </div>

      <div className="grid grid-cols-2 gap-px overflow-hidden rounded-[var(--radius-sm)] border border-line bg-line">
        <Metric label={t('agent.avgResponse')} value={`${data.avgResponseMins} ${t('agent.mins')}`} />
        <Metric label={t('agent.selfServed')} value={`${data.selfServedPct}%`} />
        <Metric label={t('agent.handed')} value={`${data.handedPct}%`} />
        <Metric label={t('agent.dropOff')} value={`${data.dropOffPct}%`} />
      </div>

      <div>
        <p className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('agent.funnelToday')}</p>
        <ul className="flex flex-col gap-2.5">
          {data.funnel.map((step) => (
            <li key={step.key}>
              <div className="mb-1 flex items-center justify-between text-[12px]">
                <span className="text-ink">{t(`agent.funnel${cap(step.key)}`)}</span>
                <span className="mono text-muted">
                  {step.count} · {step.pct}%
                </span>
              </div>
              <ProgressBar value={step.pct} colorKey="whatsapp" label={t(`agent.funnel${cap(step.key)}`)} />
            </li>
          ))}
        </ul>
      </div>
    </>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div className="bg-surface p-3">
      <p className="text-[11px] uppercase tracking-wider text-muted-2">{label}</p>
      <p className="mono mt-0.5 text-lg font-semibold text-ink">{value}</p>
    </div>
  );
}

/** Token-colored SVG sparkline. Stroke uses currentColor set to the teal token. */
function Sparkline({ values }: { values: number[] }) {
  const w = 120;
  const h = 40;
  const step = values.length > 1 ? w / (values.length - 1) : w;
  const points = values.map((v, i) => `${i * step},${h - v * h}`).join(' ');
  return (
    <svg
      width={w}
      height={h}
      viewBox={`0 0 ${w} ${h}`}
      className="text-primary"
      role="img"
      aria-label="conversation volume, last 24 hours"
      preserveAspectRatio="none"
    >
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinejoin="round" />
    </svg>
  );
}

function cap(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}
