// Bookings queue (/bookings) — "Today's queue". A reception TRIAGE queue (not a
// flat table): a header with an eyebrow + count + doctor filter + New booking, a
// time-horizon tab strip (Today / Upcoming / History with live counts), four stat
// cards, status filter chips + search, and status-GROUPED rows for Today.
//
// IA: Today / Upcoming / History are in-screen horizon TABS on this board — they
// are NOT sidebar nav children (the backend nav intentionally has no bookings.*
// children; the tabs are the intended information architecture). The list is
// partitioned client-side by slot date relative to "today" in Asia/Kolkata.
// TODO(server-side): move the today/upcoming/history split to server date filters
// (e.g. GET /bookings?horizon=today) once the API exposes them — this client-side
// partition is a stop-gap over the single flat /bookings list.
//
// CRUD stays in URL-addressable, focus-trapped slide-overs (openPanel + SlideOverHost):
// New booking → newBooking; row Manage → manage; Approve → approve; Reschedule →
// reschedule; the WhatsApp quick action → conversation. Forward status transitions
// (check-in / complete / no-show) reuse the existing optimistic hooks with an
// Idempotency-Key. PHI: masked phone only. No role branches — actions gate on the
// in-memory permission set. Loading / empty / error states are all implemented.

import { useState } from 'react';
import * as Tabs from '@radix-ui/react-tabs';
import { CalendarCheck, ChevronDown, CircleCheck, Clock, Plus, Search, UserX } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import type { BookingRow } from '@/lib/mock/contracts';
import type { BookingStatus } from '@/lib/types';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useBookings, useCheckInBooking, useCompleteBooking, useNoShowBooking } from './api';
import { QueueRow, type QueuePerms } from './components/QueueRow';

// ── Time-horizon partition (Asia/Kolkata) ────────────────────────────────────

type Horizon = 'today' | 'upcoming' | 'history';

/** YYYY-MM-DD in Asia/Kolkata for an ISO datetime, or null if unparseable. */
function istDayKey(value: string): string | null {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return null;
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Kolkata',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(d);
}

/** Bucket a row by its slot date relative to today (IST). Tolerates BOTH the live
 *  seam's ISO `slotStart` and the mock seam's label ("Today"/"Tomorrow"/"Yesterday").
 *  Unparseable / unknown → today (safe default, keeps the row visible on the hero tab). */
function horizonOf(row: BookingRow): Horizon {
  const raw = (row.date ?? '').trim();
  const lower = raw.toLowerCase();
  if (lower === 'today') return 'today';
  if (lower === 'tomorrow') return 'upcoming';
  if (lower === 'yesterday') return 'history';
  const day = istDayKey(raw);
  if (!day) return 'today';
  const today = istDayKey(new Date().toISOString());
  if (!today || day === today) return 'today';
  return day > today ? 'upcoming' : 'history';
}

// ── Status filter chips + groups ─────────────────────────────────────────────

type StatusFilter = 'all' | 'pending' | 'confirmed' | 'completed' | 'no_show';

/** Grouped sections for the Today tab, in display order. Each group's `filter`
 *  matches the status chip that isolates it; the dot is a token bg class. */
const GROUPS: { filter: Exclude<StatusFilter, 'all'>; statuses: BookingStatus[]; labelKey: string; dot: string }[] = [
  { filter: 'pending', statuses: ['pending'], labelKey: 'bookings.groupNeedsApproval', dot: 'bg-warn' },
  { filter: 'confirmed', statuses: ['confirmed', 'checked_in'], labelKey: 'bookings.groupConfirmedWaiting', dot: 'bg-primary' },
  { filter: 'no_show', statuses: ['no_show'], labelKey: 'bookings.groupNoShow', dot: 'bg-danger' },
  { filter: 'completed', statuses: ['completed'], labelKey: 'bookings.groupCompletedToday', dot: 'bg-muted-2' },
];

/** Chips shown above the queue. Counts derive from the active tab's scoped set. */
const CHIPS: { key: StatusFilter; labelKey: string; match: (s: BookingStatus) => boolean }[] = [
  { key: 'all', labelKey: 'bookings.tabAll', match: () => true },
  { key: 'pending', labelKey: 'bookings.tabPending', match: (s) => s === 'pending' },
  { key: 'confirmed', labelKey: 'bookings.tabConfirmed', match: (s) => s === 'confirmed' || s === 'checked_in' },
  { key: 'completed', labelKey: 'bookings.tabCompleted', match: (s) => s === 'completed' },
  { key: 'no_show', labelKey: 'bookings.tabNoShow', match: (s) => s === 'no_show' },
];

const tabTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function BookingsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const { data, isLoading, isError, refetch } = useBookings();

  const [tab, setTab] = useState<Horizon>('today');
  const [status, setStatus] = useState<StatusFilter>('all');
  const [doctor, setDoctor] = useState<string>('all');
  const [query, setQuery] = useState('');

  // Forward status transitions reuse the existing optimistic hooks (Idempotency-Key
  // generated ONCE per action). The hooks flip the cached row instantly (optimistic)
  // and roll back on error; we add a success toast.
  const checkIn = useCheckInBooking();
  const complete = useCompleteBooking();
  const noShow = useNoShowBooking();
  const onCheckIn = (b: BookingRow) => {
    checkIn.mutate({ bookingId: b.id, idempotencyKey: idempotencyKey() });
    toast.success(t('bookings.toastCheckedIn', { patient: b.patient }));
  };
  const onComplete = (b: BookingRow) => {
    complete.mutate({ bookingId: b.id, idempotencyKey: idempotencyKey() });
    toast.success(t('bookings.toastCompleted', { patient: b.patient }));
  };
  const onNoShow = (b: BookingRow) => {
    noShow.mutate({ bookingId: b.id, idempotencyKey: idempotencyKey() });
    toast.success(t('bookings.toastNoShow', { patient: b.patient }));
  };

  const perms: QueuePerms = {
    approve: can('docslot.booking.approve'),
    reschedule: can('docslot.booking.reschedule'),
    complete: can('docslot.booking.complete'),
    noShow: can('docslot.booking.no_show'),
    cancel: can('docslot.booking.cancel'),
    prescribe: can('docslot.prescription.create'),
  };

  const all = data ?? [];
  // Per-horizon buckets (unfiltered) drive the tab counts + the pristine-empty test.
  const buckets: Record<Horizon, BookingRow[]> = { today: [], upcoming: [], history: [] };
  for (const b of all) buckets[horizonOf(b)].push(b);

  // Doctor options: the doctors actually present anywhere in the queue.
  const doctors = Array.from(new Set(all.map((b) => b.doctorName).filter(Boolean))).sort((a, b) => a.localeCompare(b));

  // Scope the active tab by doctor + search (BEFORE the status filter — so chip /
  // stat counts reflect what the search & doctor filter already narrowed to).
  const q = query.trim().toLowerCase();
  const scoped = buckets[tab].filter((b) => {
    if (doctor !== 'all' && b.doctorName !== doctor) return false;
    if (!q) return true;
    return (
      b.patient.toLowerCase().includes(q) ||
      b.doctorName.toLowerCase().includes(q) ||
      String(b.token).includes(q) ||
      b.id.toLowerCase().includes(q) ||
      b.maskedPhone.toLowerCase().includes(q)
    );
  });

  const chipCount = (key: StatusFilter) =>
    key === 'all' ? scoped.length : scoped.filter((b) => CHIPS.find((c) => c.key === key)!.match(b.status as BookingStatus)).length;

  const isReady = !isError && !isLoading && Boolean(data);

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      {/* Header: eyebrow + title + doctor filter + New booking */}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('bookings.eyebrow')}</p>
          <h1 id="screen-heading" tabIndex={-1} className="mt-1 text-2xl font-semibold tracking-tight text-ink outline-none">
            {data ? t('bookings.count', { count: buckets.today.length }) : t('bookings.title')}
          </h1>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <DoctorFilter doctors={doctors} value={doctor} onChange={setDoctor} />
          {can('docslot.booking.create') ? (
            <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'newBooking' })}>
              <Plus size={15} aria-hidden="true" />
              {t('bookings.newBooking')}
            </Button>
          ) : null}
        </div>
      </div>

      {/* Time-horizon tabs with live counts */}
      <Tabs.Root className="min-w-0" value={tab} onValueChange={(v) => setTab(v as Horizon)}>
        <Tabs.List className="flex min-w-0 gap-1 overflow-x-auto border-b border-line" aria-label={t('bookings.title')}>
          {(['today', 'upcoming', 'history'] as const).map((h) => (
            <Tabs.Trigger key={h} value={h} className={tabTrigger}>
              {t(`bookings.tab${h === 'today' ? 'Today' : h === 'upcoming' ? 'Upcoming' : 'History'}`)}
              <span className="mono ml-1.5 text-[12px] text-muted-2">{data ? buckets[h].length : '·'}</span>
            </Tabs.Trigger>
          ))}
        </Tabs.List>
      </Tabs.Root>

      {/* Stat cards (Today only) */}
      {tab === 'today' && isReady ? (
        <StatCards scoped={scoped} active={status} onPick={setStatus} />
      ) : null}

      {/* Status chips + search */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          {CHIPS.map((c) => (
            <Chip key={c.key} active={status === c.key} onClick={() => setStatus(c.key)}>
              {t(c.labelKey)}
              <span className="mono ml-1 text-[11px] opacity-70">{isReady ? chipCount(c.key) : '·'}</span>
            </Chip>
          ))}
        </div>
        <label className="relative">
          <Search size={15} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-2" aria-hidden="true" />
          <input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t('bookings.searchPlaceholder')}
            aria-label={t('bookings.searchPlaceholder')}
            className="h-8 w-48 rounded-[var(--radius-sm)] border border-line bg-surface pl-8 pr-3 text-[13px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft sm:w-64"
          />
        </label>
      </div>

      {/* Content: error / loading / grouped (today) / flat (upcoming·history) */}
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
        <QueueSkeleton />
      ) : tab === 'today' ? (
        <TodayGroups scoped={scoped} status={status} pristineEmpty={buckets.today.length === 0} perms={perms} onCheckIn={onCheckIn} onComplete={onComplete} onNoShow={onNoShow} />
      ) : (
        <FlatQueue
          rows={status === 'all' ? scoped : scoped.filter((b) => CHIPS.find((c) => c.key === status)!.match(b.status as BookingStatus))}
          horizon={tab}
          pristineEmpty={buckets[tab].length === 0}
          perms={perms}
          onCheckIn={onCheckIn}
          onComplete={onComplete}
          onNoShow={onNoShow}
        />
      )}
    </section>
  );
}

// ── Grouped Today view ───────────────────────────────────────────────────────

function TodayGroups({
  scoped,
  status,
  pristineEmpty,
  perms,
  onCheckIn,
  onComplete,
  onNoShow,
}: {
  scoped: BookingRow[];
  status: StatusFilter;
  pristineEmpty: boolean;
  perms: QueuePerms;
  onCheckIn: (b: BookingRow) => void;
  onComplete: (b: BookingRow) => void;
  onNoShow: (b: BookingRow) => void;
}) {
  const { t } = useTranslation();
  const openPanel = useUI((s) => s.openPanel);
  const { can } = usePermissions();

  const groups = GROUPS
    .filter((g) => status === 'all' || g.filter === status)
    .map((g) => ({ ...g, rows: scoped.filter((b) => g.statuses.includes(b.status as BookingStatus)) }))
    .filter((g) => g.rows.length > 0);

  if (groups.length === 0) {
    const canCreate = can('docslot.booking.create');
    return (
      <Card>
        <EmptyState
          title={pristineEmpty ? t('bookings.emptyTitle') : t('bookings.emptyFilteredTitle')}
          description={pristineEmpty ? t('bookings.emptyBody') : t('bookings.emptyFilteredBody')}
          actionLabel={pristineEmpty && canCreate ? t('bookings.newBooking') : undefined}
          onAction={pristineEmpty && canCreate ? () => openPanel({ type: 'newBooking' }) : undefined}
        />
      </Card>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      {groups.map((g) => (
        <Card key={g.filter} className="overflow-hidden">
          <GroupHeader dot={g.dot} label={t(g.labelKey)} count={g.rows.length} />
          <ul className="flex flex-col">
            {g.rows.map((b) => (
              <QueueRow key={b.id} booking={b} perms={perms} onCheckIn={onCheckIn} onComplete={onComplete} onNoShow={onNoShow} />
            ))}
          </ul>
        </Card>
      ))}
    </div>
  );
}

function GroupHeader({ dot, label, count }: { dot: string; label: string; count: number }) {
  return (
    <div className="flex items-center gap-2 border-b border-line bg-surface-sunk px-4 py-2">
      <span className={`h-2 w-2 rounded-full ${dot}`} aria-hidden="true" />
      <span className="text-[12px] font-semibold uppercase tracking-wide text-muted">{label}</span>
      <span className="mono text-[12px] text-muted-2">{count}</span>
    </div>
  );
}

// ── Flat Upcoming / History view (lighter) ───────────────────────────────────

function FlatQueue({
  rows,
  horizon,
  pristineEmpty,
  perms,
  onCheckIn,
  onComplete,
  onNoShow,
}: {
  rows: BookingRow[];
  horizon: 'upcoming' | 'history';
  pristineEmpty: boolean;
  perms: QueuePerms;
  onCheckIn: (b: BookingRow) => void;
  onComplete: (b: BookingRow) => void;
  onNoShow: (b: BookingRow) => void;
}) {
  const { t } = useTranslation();
  if (rows.length === 0) {
    const emptyTitle = pristineEmpty
      ? horizon === 'upcoming'
        ? t('bookings.emptyUpcomingTitle')
        : t('bookings.emptyHistoryTitle')
      : t('bookings.emptyFilteredTitle');
    const emptyBody = pristineEmpty
      ? horizon === 'upcoming'
        ? t('bookings.emptyUpcomingBody')
        : t('bookings.emptyHistoryBody')
      : t('bookings.emptyFilteredBody');
    return (
      <Card>
        <EmptyState title={emptyTitle} description={emptyBody} />
      </Card>
    );
  }
  return (
    <Card className="overflow-hidden">
      <ul className="flex flex-col">
        {rows.map((b) => (
          <QueueRow key={b.id} booking={b} perms={perms} onCheckIn={onCheckIn} onComplete={onComplete} onNoShow={onNoShow} />
        ))}
      </ul>
    </Card>
  );
}

// ── Stat cards ───────────────────────────────────────────────────────────────

const STAT_TILE = {
  pending: 'bg-warn-soft text-warn',
  confirmed: 'bg-primary-soft text-primary',
  completed: 'bg-surface-sunk text-muted',
  no_show: 'bg-danger-soft text-danger',
} as const;

function StatCards({ scoped, active, onPick }: { scoped: BookingRow[]; active: StatusFilter; onPick: (s: StatusFilter) => void }) {
  const { t } = useTranslation();
  const count = (m: (s: BookingStatus) => boolean) => scoped.filter((b) => m(b.status as BookingStatus)).length;
  const cards: { key: Exclude<StatusFilter, 'all'>; labelKey: string; Icon: typeof Clock; value: number }[] = [
    { key: 'pending', labelKey: 'bookings.statPending', Icon: Clock, value: count((s) => s === 'pending') },
    { key: 'confirmed', labelKey: 'bookings.statConfirmed', Icon: CalendarCheck, value: count((s) => s === 'confirmed' || s === 'checked_in') },
    { key: 'completed', labelKey: 'bookings.statCompleted', Icon: CircleCheck, value: count((s) => s === 'completed') },
    { key: 'no_show', labelKey: 'bookings.statNoShow', Icon: UserX, value: count((s) => s === 'no_show') },
  ];
  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
      {cards.map(({ key, labelKey, Icon, value }) => {
        const isActive = active === key;
        return (
          <button
            key={key}
            type="button"
            aria-pressed={isActive}
            onClick={() => onPick(isActive ? 'all' : key)}
            className={[
              'flex items-center gap-3 rounded-[var(--radius)] border p-3 text-left shadow-[var(--shadow-sm)] transition-colors',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary',
              isActive ? 'border-primary bg-primary-soft' : 'border-line bg-surface hover:bg-surface-sunk',
            ].join(' ')}
          >
            <span className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-[var(--radius-sm)] ${STAT_TILE[key]}`}>
              <Icon size={18} aria-hidden="true" />
            </span>
            <span className="min-w-0">
              <span className="mono block text-xl font-semibold leading-none text-ink">{value}</span>
              <span className="mt-1 block truncate text-[12px] text-muted">{t(labelKey)}</span>
            </span>
          </button>
        );
      })}
    </div>
  );
}

// ── Chip + skeleton ──────────────────────────────────────────────────────────

function Chip({ children, active, onClick }: { children: React.ReactNode; active?: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={`inline-flex h-8 items-center rounded-full border px-3 text-[12px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ${
        active ? 'border-primary-soft bg-primary-soft text-primary' : 'border-line bg-surface text-muted hover:text-ink'
      }`}
    >
      {children}
    </button>
  );
}

function DoctorFilter({ doctors, value, onChange }: { doctors: string[]; value: string; onChange: (v: string) => void }) {
  const { t } = useTranslation();
  return (
    <div className="relative">
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-label={t('bookings.filterDoctorLabel')}
        className="h-8 appearance-none rounded-[var(--radius-sm)] border border-line bg-surface pl-3 pr-8 text-[13px] text-ink outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
      >
        <option value="all">{t('bookings.filterAllDoctors')}</option>
        {doctors.map((d) => (
          <option key={d} value={d}>
            {d}
          </option>
        ))}
      </select>
      <ChevronDown size={14} aria-hidden="true" className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-muted-2" />
    </div>
  );
}

function QueueSkeleton() {
  return (
    <div className="flex flex-col gap-4" role="status" aria-busy="true">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <div key={i} className="flex items-center gap-3 rounded-[var(--radius)] border border-line bg-surface p-3 shadow-[var(--shadow-sm)]">
            <Skeleton className="h-10 w-10 rounded-[var(--radius-sm)]" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-4 w-8" />
              <Skeleton className="h-3 w-16" />
            </div>
          </div>
        ))}
      </div>
      <Card className="overflow-hidden">
        <ul className="flex flex-col">
          {Array.from({ length: 6 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
              <Skeleton className="h-10 w-10 rounded-full" />
              <div className="flex flex-1 flex-col gap-2">
                <Skeleton className="h-3 w-1/4" />
                <Skeleton className="h-3 w-1/3" />
              </div>
              <Skeleton className="hidden h-3 w-24 sm:block" />
              <Skeleton className="h-6 w-20 rounded-full" />
              <Skeleton className="h-8 w-16" />
            </li>
          ))}
        </ul>
      </Card>
    </div>
  );
}
