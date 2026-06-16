// Bookings list (/bookings) — "Today's queue". Status tabs (All / Pending /
// Confirmed / Completed / No-show), doctor + Today filter chips, search, and a
// data table. CRUD stays in slide-overs: "+ New booking" → newBooking panel; row
// Manage → manage panel; row Approve → approve panel (all URL-addressable via the
// shared store→URL sync in SlideOverHost). PHI: the table shows MASKED phone only
// (the list payload never carries the raw number). Loading / empty / error states
// are all implemented. No role branches — actions gate on the in-memory
// permission set; nav is backend-driven elsewhere.

import { useState } from 'react';
import * as Tabs from '@radix-ui/react-tabs';
import { Check, MoreHorizontal, Plus, Search } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { istSlot } from '@/lib/format';
import type { BookingRow } from '@/lib/mock/contracts';
import type { BookingStatus } from '@/lib/types';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useBookings } from './api';

// Tab → status filter. 'all' shows everything; the rest filter to one status.
type TabKey = 'all' | 'pending' | 'confirmed' | 'completed' | 'no_show';
const TABS: { key: TabKey; labelKey: string }[] = [
  { key: 'all', labelKey: 'bookings.tabAll' },
  { key: 'pending', labelKey: 'bookings.tabPending' },
  { key: 'confirmed', labelKey: 'bookings.tabConfirmed' },
  { key: 'completed', labelKey: 'bookings.tabCompleted' },
  { key: 'no_show', labelKey: 'bookings.tabNoShow' },
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
  const [tab, setTab] = useState<TabKey>('all');
  const [query, setQuery] = useState('');

  const all = data ?? [];
  const q = query.trim().toLowerCase();
  const rows = all.filter((b) => {
    if (tab !== 'all' && b.status !== tab) return false;
    if (!q) return true;
    return (
      b.patient.toLowerCase().includes(q) ||
      b.doctorName.toLowerCase().includes(q) ||
      String(b.token).includes(q)
    );
  });

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {data ? t('bookings.count', { count: all.length }) : t('bookings.title')}
        </h1>
        {can('docslot.booking.create') ? (
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'newBooking' })}>
            <Plus size={15} aria-hidden="true" />
            {t('bookings.newBooking')}
          </Button>
        ) : null}
      </div>

      <Tabs.Root className="min-w-0" value={tab} onValueChange={(v) => setTab(v as TabKey)}>
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-line">
          <Tabs.List className="flex min-w-0 flex-1 gap-1 overflow-x-auto" aria-label={t('bookings.title')}>
            {TABS.map((tabDef) => (
              <Tabs.Trigger key={tabDef.key} value={tabDef.key} className={tabTrigger}>
                {t(tabDef.labelKey)}
              </Tabs.Trigger>
            ))}
          </Tabs.List>

          <div className="flex items-center gap-2 pb-2">
            {/* Filter chips — static "All doctors" / "Today" affordances mirroring
                the prototype; they describe the current scope of the queue. */}
            <span className="hidden items-center gap-2 sm:flex">
              <Chip>{t('bookings.filterAllDoctors')}</Chip>
              <Chip active>{t('bookings.filterToday')}</Chip>
            </span>
            <label className="relative">
              <Search
                size={15}
                className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-2"
                aria-hidden="true"
              />
              <input
                type="search"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t('bookings.searchPlaceholder')}
                aria-label={t('bookings.searchPlaceholder')}
                className="h-8 w-44 rounded-[var(--radius-sm)] border border-line bg-surface pl-8 pr-3 text-[13px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft sm:w-56"
              />
            </label>
          </div>
        </div>

        {/* Single content region — the tab value already filters `rows`. */}
        <div className="pt-5">
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
            <BookingsTableSkeleton />
          ) : rows.length === 0 ? (
            <Card>
              <EmptyState
                title={all.length === 0 ? t('bookings.emptyTitle') : t('bookings.emptyFilteredTitle')}
                description={all.length === 0 ? t('bookings.emptyBody') : t('bookings.emptyFilteredBody')}
                actionLabel={all.length === 0 && can('docslot.booking.create') ? t('bookings.newBooking') : undefined}
                onAction={all.length === 0 && can('docslot.booking.create') ? () => openPanel({ type: 'newBooking' }) : undefined}
              />
            </Card>
          ) : (
            <BookingsTable rows={rows} />
          )}
        </div>
      </Tabs.Root>
    </section>
  );
}

function Chip({ children, active }: { children: React.ReactNode; active?: boolean }) {
  return (
    <span
      className={`inline-flex h-8 items-center rounded-full border px-3 text-[12px] font-medium ${
        active ? 'border-primary-soft bg-primary-soft text-primary' : 'border-line bg-surface text-muted'
      }`}
    >
      {children}
    </span>
  );
}

function BookingsTable({ rows }: { rows: BookingRow[] }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);

  // The panels need the full Booking (phone, note, language, etc.); the list row
  // carries a masked phone only. We open by id — the panel fetches the full record
  // (GET /bookings/{id} live / prototype seam mock), so it works for a REAL booking.
  const openManage = (id: string) => openPanel({ type: 'manage', bookingId: id });
  const openApprove = (id: string) => openPanel({ type: 'approve', bookingId: id });

  return (
    <Card className="overflow-hidden">
      {/* `relative` makes this scroll container the containing block for the
          sr-only (position:absolute) label in the actions <th>; without it that
          span escapes to <html> at the table's far edge and forces a document-
          wide horizontal scroll on narrow viewports. */}
      <div className="relative min-w-0 overflow-x-auto">
        <table className="w-full min-w-[760px] border-collapse text-left">
          <thead>
            <tr className="border-b border-line text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              <th scope="col" className="px-4 py-2.5 font-semibold">{t('bookings.colToken')}</th>
              <th scope="col" className="px-4 py-2.5 font-semibold">{t('bookings.colPatient')}</th>
              <th scope="col" className="px-4 py-2.5 font-semibold">{t('bookings.colDoctor')}</th>
              <th scope="col" className="px-4 py-2.5 font-semibold">{t('bookings.colSource')}</th>
              <th scope="col" className="px-4 py-2.5 font-semibold">{t('bookings.colTime')}</th>
              <th scope="col" className="px-4 py-2.5 font-semibold">{t('bookings.colStatus')}</th>
              <th scope="col" className="px-4 py-2.5 text-right font-semibold">
                <span className="sr-only">{t('bookings.colActions')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {rows.map((b) => (
              <BookingRowView
                key={b.id}
                booking={b}
                canApprove={can('docslot.booking.approve')}
                onManage={() => openManage(b.id)}
                onApprove={() => openApprove(b.id)}
              />
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

function BookingRowView({
  booking: b,
  canApprove,
  onManage,
  onApprove,
}: {
  booking: BookingRow;
  canApprove: boolean;
  onManage: () => void;
  onApprove: () => void;
}) {
  const { t } = useTranslation();
  const isDeva = /[ऀ-ॿ]/.test(b.note);

  return (
    <tr className="border-b border-line last:border-0 transition-colors hover:bg-surface-sunk">
      <td className="px-4 py-3 align-middle">
        <span className="mono rounded bg-surface-sunk px-1.5 py-0.5 text-[12px] text-muted">#{b.token}</span>
      </td>
      <td className="px-4 py-3 align-middle">
        <div className="flex items-center gap-3">
          <Avatar name={b.patient} size="sm" />
          <div className="min-w-0">
            <p className="truncate text-sm font-medium text-ink">{b.patient}</p>
            <p className="mono text-[11px] text-muted-2">{b.maskedPhone}</p>
          </div>
        </div>
      </td>
      <td className="px-4 py-3 align-middle">
        <p className="text-[13px] text-ink">{b.doctorName}</p>
        <p className="text-[11px] text-muted-2">{b.dept}</p>
      </td>
      <td className="px-4 py-3 align-middle">
        <div className="flex items-center gap-2">
          {b.source === 'whatsapp' ? (
            <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-whatsapp-soft px-2 py-0.5 text-[11px] font-medium text-whatsapp-ink">
              <span className="h-1.5 w-1.5 rounded-full bg-whatsapp" aria-hidden="true" />
              {t('source.whatsapp')}
            </span>
          ) : (
            <span className="inline-flex shrink-0 items-center rounded-full bg-surface-sunk px-2 py-0.5 text-[11px] font-medium text-muted">
              {t(`source.${b.source}`)}
            </span>
          )}
          <span className={`truncate text-[12px] text-muted ${isDeva ? 'deva' : ''}`} title={b.note}>
            {b.note}
          </span>
        </div>
      </td>
      <td className="mono px-4 py-3 align-middle text-[13px] text-ink">{istSlot(b.time)}</td>
      <td className="px-4 py-3 align-middle">
        <StatusPill status={b.status as BookingStatus} />
      </td>
      <td className="px-4 py-3 align-middle">
        <div className="flex items-center justify-end gap-2">
          {b.status === 'pending' && canApprove ? (
            <Button variant="primary" size="sm" onClick={onApprove}>
              <Check size={14} aria-hidden="true" />
              {t('bookings.approve')}
            </Button>
          ) : (
            <Button variant="ghost" size="sm" onClick={onManage}>
              {t('bookings.manage')}
            </Button>
          )}
          <button
            type="button"
            onClick={onManage}
            aria-label={t('bookings.moreActions', { patient: b.patient })}
            className="inline-flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] text-muted-2 transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
          >
            <MoreHorizontal size={16} aria-hidden="true" />
          </button>
        </div>
      </td>
    </tr>
  );
}

function BookingsTableSkeleton() {
  return (
    <Card className="overflow-hidden" role="status" aria-busy="true">
      <ul className="flex flex-col">
        {Array.from({ length: 8 }).map((_, i) => (
          <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
            <Skeleton className="h-6 w-10" />
            <Skeleton className="h-8 w-8 rounded-full" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-3 w-1/4" />
              <Skeleton className="h-3 w-1/3" />
            </div>
            <Skeleton className="h-3 w-24" />
            <Skeleton className="h-6 w-20 rounded-full" />
            <Skeleton className="h-8 w-24" />
          </li>
        ))}
      </ul>
    </Card>
  );
}
