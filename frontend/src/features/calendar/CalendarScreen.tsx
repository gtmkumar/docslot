// Calendar (/calendar) — week-view capacity heatmap. Week/Day/Month segmented
// toggle, prev/next + range label, Book time / Block time actions, doctor filter
// pills, and a time grid (rows = slots, columns = Mon–Sun). Each cell shows
// "booked/cap" with a fill bar and a legend state (Available / Almost full / Full
// / Blocked / Off-hours). TODAY's column is highlighted. Clicking an open cell
// opens the bookTime slide-over. On small screens the grid collapses to a per-day
// list. Loading / empty / error states all implemented. No role branches.

import { useState } from 'react';
import { ChevronLeft, ChevronRight, Plus, Slash } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { istSlot } from '@/lib/format';
import { DEPARTMENTS } from '@/lib/data';
import type { CalendarCell, CalendarColumn, CalendarGrid } from '@/lib/mock/contracts';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useCalendarGrid } from './api';

type ViewMode = 'week' | 'day' | 'month';

// Cell state → token classes. Open/tight/full use soft tints; blocked/off are
// muted surfaces. Never a hex.
const CELL_STYLE: Record<CalendarCell['state'], string> = {
  open: 'bg-primary-soft text-primary hover:ring-2 hover:ring-primary',
  tight: 'bg-warn-soft text-warn hover:ring-2 hover:ring-warn',
  full: 'bg-danger-soft text-danger',
  blocked: 'bg-surface-sunk text-muted-2',
  off: 'bg-bg text-muted-2/60',
};
const FILL_STYLE: Record<CalendarCell['state'], string> = {
  open: 'bg-primary',
  tight: 'bg-warn',
  full: 'bg-danger',
  blocked: 'bg-muted-2',
  off: 'bg-transparent',
};

const LEGEND: { state: CalendarCell['state']; key: string }[] = [
  { state: 'open', key: 'calendar.legendAvailable' },
  { state: 'tight', key: 'calendar.legendTight' },
  { state: 'full', key: 'calendar.legendFull' },
  { state: 'blocked', key: 'calendar.legendBlocked' },
  { state: 'off', key: 'calendar.legendOff' },
];

export function CalendarScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const { data, isLoading, isError, refetch } = useCalendarGrid();
  const [view, setView] = useState<ViewMode>('week');
  const [dept, setDept] = useState<string>('all');

  const canBook = can('docslot.booking.create');
  const openCell = (_col: CalendarColumn, cell: CalendarCell) => {
    if (!canBook || cell.state === 'full' || cell.state === 'blocked' || cell.state === 'off') return;
    openPanel({ type: 'bookTime' });
  };

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('calendar.title')}
        </h1>
        <div className="flex items-center gap-2">
          {canBook ? (
            <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'bookTime' })}>
              <Plus size={15} aria-hidden="true" />
              {t('calendar.bookTime')}
            </Button>
          ) : null}
          {canBook ? (
            <Button variant="ghost" size="sm" onClick={() => openPanel({ type: 'bookTime' })}>
              <Slash size={14} aria-hidden="true" />
              {t('calendar.blockTime')}
            </Button>
          ) : null}
        </div>
      </div>

      {/* Controls row: view toggle + prev/next + range label + doctor pills. */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <Segmented
            value={view}
            onChange={setView}
            options={[
              { value: 'week', label: t('calendar.viewWeek') },
              { value: 'day', label: t('calendar.viewDay') },
              { value: 'month', label: t('calendar.viewMonth') },
            ]}
          />
          <div className="flex items-center gap-1">
            <IconButton label={t('calendar.prev')}>
              <ChevronLeft size={16} aria-hidden="true" />
            </IconButton>
            <span className="mono px-1 text-[13px] text-ink">{data?.rangeLabel ?? '—'}</span>
            <IconButton label={t('calendar.next')}>
              <ChevronRight size={16} aria-hidden="true" />
            </IconButton>
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          <DoctorPill active={dept === 'all'} onClick={() => setDept('all')}>
            {t('calendar.allDoctors')}
          </DoctorPill>
          {DEPARTMENTS.slice(0, 4).map((d) => (
            <DoctorPill key={d.id} active={dept === d.id} onClick={() => setDept(d.id)}>
              {d.name}
            </DoctorPill>
          ))}
        </div>
      </div>

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
        <CalendarSkeleton />
      ) : data.columns.length === 0 ? (
        <Card>
          <EmptyState title={t('calendar.emptyTitle')} description={t('calendar.emptyBody')} />
        </Card>
      ) : (
        <>
          <Legend />
          {/* Desktop: full grid. Mobile: per-day list. */}
          <div className="hidden lg:block">
            <WeekGrid grid={data} canBook={canBook} onOpen={openCell} />
          </div>
          <div className="flex flex-col gap-4 lg:hidden">
            {data.columns.map((col) => (
              <DayList key={col.key} col={col} times={data.times} canBook={canBook} onOpen={openCell} />
            ))}
          </div>
        </>
      )}
    </section>
  );
}

function WeekGrid({
  grid,
  canBook,
  onOpen,
}: {
  grid: CalendarGrid;
  canBook: boolean;
  onOpen: (col: CalendarColumn, cell: CalendarCell) => void;
}) {
  const { t } = useTranslation();
  return (
    <Card className="overflow-x-auto p-3">
      <div
        className="grid min-w-[760px] gap-1"
        style={{ gridTemplateColumns: `64px repeat(${grid.columns.length}, minmax(0, 1fr))` }}
      >
        {/* Header row: empty corner + weekday labels. */}
        <div aria-hidden="true" />
        {grid.columns.map((col) => (
          <div
            key={col.key}
            className={`rounded-[var(--radius-sm)] px-2 py-1.5 text-center ${
              col.isToday ? 'bg-primary-soft' : ''
            }`}
          >
            <p className={`text-[11px] font-semibold uppercase tracking-wider ${col.isToday ? 'text-primary' : 'text-muted-2'}`}>
              {col.weekday}
            </p>
            <p className={`mono text-sm font-semibold ${col.isToday ? 'text-primary' : 'text-ink'}`}>{col.dayOfMonth}</p>
            {col.isToday ? <p className="text-[10px] font-medium text-primary">{t('calendar.today')}</p> : null}
          </div>
        ))}

        {/* Time rows. */}
        {grid.times.map((time, ti) => (
          <Row key={time}>
            <div className="mono flex items-start justify-end pr-2 pt-1 text-[11px] text-muted-2">{time}</div>
            {grid.columns.map((col) => (
              <CellView
                key={`${col.key}-${time}`}
                col={col}
                time={time}
                cell={col.cells[ti]}
                canBook={canBook}
                onOpen={onOpen}
              />
            ))}
          </Row>
        ))}
      </div>
    </Card>
  );
}

// A row spans the grid by re-using the parent's columns (display: contents).
function Row({ children }: { children: React.ReactNode }) {
  return <div className="contents">{children}</div>;
}

function CellView({
  col,
  time,
  cell,
  canBook,
  onOpen,
}: {
  col: CalendarColumn;
  time: string;
  cell: CalendarCell;
  canBook: boolean;
  onOpen: (col: CalendarColumn, cell: CalendarCell) => void;
}) {
  const { t } = useTranslation();
  const interactive = canBook && (cell.state === 'open' || cell.state === 'tight');
  const fillPct = cell.capacity > 0 ? Math.round((cell.booked / cell.capacity) * 100) : 0;
  const base = `relative flex flex-col justify-between rounded-[var(--radius-sm)] px-2 py-1.5 text-left transition-shadow ${CELL_STYLE[cell.state]} ${col.isToday ? 'ring-1 ring-primary-soft' : ''}`;

  const body = (
    <>
      {cell.state === 'off' || cell.state === 'blocked' ? (
        <span className="mono text-[11px]">{cell.state === 'blocked' ? '—' : ''}</span>
      ) : (
        <span className="mono text-[12px] font-medium">
          {cell.booked}/{cell.capacity}
        </span>
      )}
      {cell.state !== 'off' ? (
        <span className="mt-1 h-1 w-full overflow-hidden rounded-full bg-bg/40" aria-hidden="true">
          <span className={`block h-full rounded-full ${FILL_STYLE[cell.state]}`} style={{ width: `${fillPct}%` }} />
        </span>
      ) : null}
    </>
  );

  if (interactive) {
    return (
      <button
        type="button"
        onClick={() => onOpen(col, cell)}
        aria-label={t('calendar.openCell', {
          day: col.label,
          time: istSlot(time),
          booked: cell.booked,
          capacity: cell.capacity,
        })}
        className={`${base} focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary`}
      >
        {body}
      </button>
    );
  }
  return (
    <div
      className={base}
      aria-label={t('calendar.cellLabel', {
        day: col.label,
        time: istSlot(time),
        booked: cell.booked,
        capacity: cell.capacity,
      })}
    >
      {body}
    </div>
  );
}

// Mobile fallback: a per-day list of slots.
function DayList({
  col,
  times,
  canBook,
  onOpen,
}: {
  col: CalendarColumn;
  times: string[];
  canBook: boolean;
  onOpen: (col: CalendarColumn, cell: CalendarCell) => void;
}) {
  const { t } = useTranslation();
  return (
    <Card className={`p-3 ${col.isToday ? 'border-primary-soft' : ''}`}>
      <header className="mb-2 flex items-center justify-between">
        <h2 className="text-sm font-semibold text-ink">{t('calendar.dayListHeading', { day: col.label })}</h2>
        {col.isToday ? (
          <span className="rounded-full bg-primary-soft px-2 py-0.5 text-[11px] font-medium text-primary">
            {t('calendar.today')}
          </span>
        ) : null}
      </header>
      <ul className="flex flex-col">
        {times.map((time, ti) => {
          const cell = col.cells[ti];
          if (cell.state === 'off') return null;
          const interactive = canBook && (cell.state === 'open' || cell.state === 'tight');
          const inner = (
            <div className="flex items-center justify-between gap-3 py-2">
              <span className="mono text-[12px] text-muted">{istSlot(time)}</span>
              <span className="flex items-center gap-2">
                <span className={`h-2 w-2 rounded-full ${FILL_STYLE[cell.state]}`} aria-hidden="true" />
                <span className="mono text-[12px] text-ink">
                  {cell.state === 'blocked' ? t('calendar.legendBlocked') : `${cell.booked}/${cell.capacity}`}
                </span>
              </span>
            </div>
          );
          return (
            <li key={time} className="border-b border-line last:border-0">
              {interactive ? (
                <button
                  type="button"
                  onClick={() => onOpen(col, cell)}
                  className="w-full rounded-[var(--radius-sm)] px-1 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                  aria-label={t('calendar.openCell', { day: col.label, time: istSlot(time), booked: cell.booked, capacity: cell.capacity })}
                >
                  {inner}
                </button>
              ) : (
                inner
              )}
            </li>
          );
        })}
      </ul>
    </Card>
  );
}

function Legend() {
  const { t } = useTranslation();
  return (
    <ul className="flex flex-wrap items-center gap-x-4 gap-y-2">
      {LEGEND.map(({ state, key }) => (
        <li key={state} className="flex items-center gap-1.5 text-[12px] text-muted">
          <span className={`h-2.5 w-2.5 rounded-[3px] ${CELL_STYLE[state].split(' ')[0]}`} aria-hidden="true" />
          {t(key)}
        </li>
      ))}
    </ul>
  );
}

function Segmented<T extends string>({
  value,
  onChange,
  options,
}: {
  value: T;
  onChange: (v: T) => void;
  options: { value: T; label: string }[];
}) {
  return (
    <div className="inline-flex rounded-[var(--radius-sm)] border border-line bg-surface p-0.5" role="tablist">
      {options.map((opt) => (
        <button
          key={opt.value}
          type="button"
          role="tab"
          aria-selected={value === opt.value}
          onClick={() => onChange(opt.value)}
          className={`rounded-[6px] px-3 py-1 text-[13px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ${
            value === opt.value ? 'bg-primary text-bg' : 'text-muted hover:text-ink'
          }`}
        >
          {opt.label}
        </button>
      ))}
    </div>
  );
}

function DoctorPill({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={`inline-flex h-8 items-center rounded-full px-3 text-[12px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ${
        active ? 'bg-primary-soft text-primary' : 'border border-line bg-surface text-muted hover:bg-surface-sunk hover:text-ink'
      }`}
    >
      {children}
    </button>
  );
}

function IconButton({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <button
      type="button"
      aria-label={label}
      className="inline-flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] border border-line bg-surface text-muted transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
    >
      {children}
    </button>
  );
}

function CalendarSkeleton() {
  return (
    <Card className="p-3" role="status" aria-busy="true">
      <div className="grid gap-1" style={{ gridTemplateColumns: '64px repeat(7, minmax(0, 1fr))' }}>
        <div />
        {Array.from({ length: 7 }).map((_, i) => (
          <Skeleton key={`h-${i}`} className="h-10 w-full" />
        ))}
        {Array.from({ length: 8 }).map((_, r) => (
          <div key={`r-${r}`} className="contents">
            <Skeleton className="h-9 w-12 justify-self-end" />
            {Array.from({ length: 7 }).map((_, c) => (
              <Skeleton key={`c-${r}-${c}`} className="h-9 w-full" />
            ))}
          </div>
        ))}
      </div>
    </Card>
  );
}
