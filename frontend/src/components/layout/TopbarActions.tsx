// Topbar right-side action cluster with graceful overflow.
//  - lg+ : roster + book-time render inline as labelled buttons.
//  - <lg : they collapse into a "more" overflow menu (disclosure popover,
//          outside-click + Esc close) so nothing clips.
//  - + New walk-in : icon-only below sm (label hidden) to fit 360px widths.
// Every action stays permission-gated via in-memory can() — no role checks.

import { useEffect, useRef, useState } from 'react';
import { Calendar, Clock, MoreHorizontal, Plus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';

export function TopbarActions() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const [moreOpen, setMoreOpen] = useState(false);
  const moreRef = useRef<HTMLDivElement>(null);

  const canCalendar = can('docslot.slot.read');
  const canCreate = can('docslot.booking.create');

  useEffect(() => {
    if (!moreOpen) return;
    const onDown = (e: MouseEvent) => {
      if (moreRef.current && !moreRef.current.contains(e.target as Node)) setMoreOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setMoreOpen(false);
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [moreOpen]);

  return (
    <div className="ml-auto flex shrink-0 items-center gap-2 md:ml-0">
      {/* Inline at lg+. Display is controlled on the wrapper span (not the Button)
          because Button's base `inline-flex` outranks a `hidden` passed via
          className, so it would never actually hide. */}
      {canCalendar ? (
        <span className="hidden lg:inline-flex">
          <Button variant="ghost" size="sm">
            <Calendar size={15} aria-hidden="true" />
            {t('topbar.todaysRoster')}
          </Button>
        </span>
      ) : null}
      {canCalendar ? (
        <span className="hidden lg:inline-flex">
          <Button variant="ghost" size="sm" onClick={() => openPanel({ type: 'bookTime' })}>
            <Clock size={15} aria-hidden="true" />
            {t('topbar.bookTime')}
          </Button>
        </span>
      ) : null}

      {/* Overflow menu for those same actions below lg */}
      {canCalendar ? (
        <div ref={moreRef} className="relative lg:hidden">
          <button
            type="button"
            aria-haspopup="menu"
            aria-expanded={moreOpen}
            aria-label={t('topbar.moreActions')}
            onClick={() => setMoreOpen((o) => !o)}
            className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] border border-line-strong text-ink transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
          >
            <MoreHorizontal size={16} aria-hidden="true" />
          </button>
          {moreOpen ? (
            <div
              role="menu"
              aria-label={t('topbar.moreActions')}
              className="absolute right-0 z-50 mt-2 w-48 rounded-[var(--radius)] border border-line bg-surface p-1.5 shadow-[var(--shadow-lg)]"
            >
              <MenuItem
                icon={<Calendar size={15} aria-hidden="true" />}
                label={t('topbar.todaysRoster')}
                onClick={() => setMoreOpen(false)}
              />
              <MenuItem
                icon={<Clock size={15} aria-hidden="true" />}
                label={t('topbar.bookTime')}
                onClick={() => {
                  setMoreOpen(false);
                  openPanel({ type: 'bookTime' });
                }}
              />
            </div>
          ) : null}
        </div>
      ) : null}

      {canCreate ? (
        <Button
          variant="primary"
          size="sm"
          onClick={() => openPanel({ type: 'newBooking' })}
          aria-label={t('topbar.newWalkIn')}
        >
          {/* Icon-only below sm; label appears from sm up. */}
          <Plus size={15} aria-hidden="true" className="sm:hidden" />
          <span className="hidden sm:inline">{t('topbar.newWalkIn')}</span>
        </Button>
      ) : null}
    </div>
  );
}

function MenuItem({ icon, label, onClick }: { icon: React.ReactNode; label: string; onClick: () => void }) {
  return (
    <button
      type="button"
      role="menuitem"
      onClick={onClick}
      className="flex w-full items-center gap-2.5 rounded-[var(--radius-sm)] px-2.5 py-2 text-left text-[13px] text-ink transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
    >
      <span className="text-muted">{icon}</span>
      {label}
    </button>
  );
}
