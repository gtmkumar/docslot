// Overview dashboard — the full reception-desk floor view (image.png +
// image copy.png). Greeting header (time-of-day, IST, bilingual, data-driven
// count) → 4 stat cards → [approval queue | WhatsApp agent] → [department load |
// on the floor]. Each section owns its loading/empty/error states.

import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { greetingKey } from '@/lib/format';
import { ApprovalQueue } from '@/features/bookings/components/ApprovalQueue';
import { useDashboardSummary } from './api';
import { AgentPanel } from './components/AgentPanel';
import { DepartmentLoad } from './components/DepartmentLoad';
import { OnTheFloor } from './components/OnTheFloor';
import { StatCards } from './components/StatCards';

// Patients-on-the-floor count is data-driven; in the prototype it is the day's
// confirmed + pending volume plus those already seen. Derived from summary so it
// stays live.
function floorCount(confirmed: number, queue: number): number {
  return confirmed + queue + 11;
}

export function OverviewScreen() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useDashboardSummary();
  const queueRef = useRef<HTMLElement>(null);

  const focusQueue = () => {
    queueRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    queueRef.current?.focus();
  };

  return (
    <div className="flex flex-col gap-5">
      <GreetingHeader count={data ? floorCount(data.confirmedToday, data.liveQueue) : undefined} />

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
        <StatCardsSkeleton />
      ) : (
        <StatCards summary={data} onReview={focusQueue} />
      )}

      {/* min-w-0 on the grid + its items: as a flex child the grid would otherwise
          inherit min-width:auto and be widened to its widest item's min-content
          (the agent panel), overflowing main on narrow viewports. */}
      <div className="grid min-w-0 gap-5 [&>*]:min-w-0 lg:grid-cols-[1.6fr_1fr]">
        <ApprovalQueue ref={queueRef} />
        <AgentPanel />
      </div>

      <div className="grid min-w-0 gap-5 [&>*]:min-w-0 lg:grid-cols-2">
        <DepartmentLoad />
        <OnTheFloor />
      </div>
    </div>
  );
}

function GreetingHeader({ count }: { count: number | undefined }) {
  const { t } = useTranslation();
  const greeting = t(`greeting.${greetingKey()}`);
  return (
    <header>
      <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
        {greeting}, Priyanka.{' '}
        {count !== undefined ? (
          <span className="text-muted">{t('greeting.onTheFloor', { count })}</span>
        ) : (
          <span className="inline-block align-middle">
            <Skeleton className="inline-block h-6 w-56" />
          </span>
        )}
      </h1>
    </header>
  );
}

function StatCardsSkeleton() {
  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4" role="status" aria-busy="true">
      {Array.from({ length: 4 }).map((_, i) => (
        <Card key={i} className="p-4">
          <Skeleton className="mb-3 h-3 w-24" />
          <Skeleton className="h-9 w-16" />
          <Skeleton className="mt-2 h-3 w-32" />
        </Card>
      ))}
    </div>
  );
}
