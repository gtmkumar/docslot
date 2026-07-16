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
import { USE_REAL_API } from '@/lib/backend/flag';
import { useSession } from '@/stores/session';
import { useDashboardSummary } from './api';
import { AgentPanel } from './components/AgentPanel';
import { DepartmentLoad } from './components/DepartmentLoad';
import { OnTheFloor } from './components/OnTheFloor';
import { StatCards } from './components/StatCards';

// Patients-on-the-floor count is data-driven: the day's confirmed volume plus
// the still-pending queue. Derived from summary so it stays live.
function floorCount(confirmed: number, queue: number): number {
  return confirmed + queue;
}

export function OverviewScreen() {
  const { t } = useTranslation();
  const tenantId = useSession((s) => s.tenantId);
  // Platform scope renders the empty state below — never fire the tenant-scoped
  // summary query without a tenant (it can only 403).
  const hasTenant = !USE_REAL_API || Boolean(tenantId);
  const { data, isLoading, isError, refetch } = useDashboardSummary(hasTenant);
  const queueRef = useRef<HTMLElement>(null);

  const focusQueue = () => {
    queueRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    queueRef.current?.focus();
  };

  // PLATFORM scope (live mode, no active tenant — a super_admin before impersonation):
  // every reception widget is tenant-scoped and would 403, so show an honest state
  // pointing at Impersonate instead of six error cards. Mock mode is unaffected.
  if (USE_REAL_API && !tenantId) {
    return (
      <div className="flex flex-col gap-5">
        <GreetingHeader count={undefined} showFloorCount={false} />
        <Card>
          <EmptyState title={t('overview.platformScopeTitle')} description={t('overview.platformScopeBody')} />
        </Card>
      </div>
    );
  }

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

function GreetingHeader({ count, showFloorCount = true }: { count: number | undefined; showFloorCount?: boolean }) {
  const { t } = useTranslation();
  const greeting = t(`greeting.${greetingKey()}`);
  // The signed-in user's first name from the real session — never a hardcoded
  // prototype name. No user yet (initial load) → greeting alone.
  const fullName = useSession((s) => s.user?.fullName);
  const firstName = fullName?.trim().split(/\s+/)[0];
  return (
    <header>
      <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
        {firstName ? `${greeting}, ${firstName}.` : `${greeting}.`}{' '}
        {!showFloorCount ? null : count !== undefined ? (
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
