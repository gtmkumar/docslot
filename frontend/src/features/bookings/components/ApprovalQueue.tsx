// Approval queue (image.png center). Pending bookings with Manage (ghost →
// manage panel) + Approve (primary → optimistic confirm with a 5s undo window).
//
// Patterns: 4 (optimistic UI), 5 (skeleton), 6 (empty), 7 (toast+undo deferred
// mutation), 11 (j/k row nav, Enter → manage), 12 (StatusPill not used here as
// all rows are pending, but slot time is explicit IST). Forwarding a ref lets the
// Overview "Review" button focus the section.
//
// Deferred-mutation undo: on Approve we optimistically hide the row and show a
// sonner toast with Undo; the real mutation only fires after the 5s window
// elapses. Undo cancels the timer and restores the row.

import { forwardRef, useEffect, useRef, useState } from 'react';
import { Link } from '@tanstack/react-router';
import { ArrowRight, Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { istSlot } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import type { BookingRow } from '@/lib/mock/contracts';
import { useUI } from '@/stores/ui';
import { useApproveBooking, usePendingBookings } from '../api';

const UNDO_MS = 5000;

export const ApprovalQueue = forwardRef<HTMLElement>(function ApprovalQueue(_props, ref) {
  const { t } = useTranslation();
  const { data: pending, isLoading, isError, refetch } = usePendingBookings();
  const approve = useApproveBooking();
  const openPanel = useUI((s) => s.openPanel);

  // Rows pending a deferred approve (hidden optimistically, restorable via undo).
  const [deferred, setDeferred] = useState<Set<string>>(new Set());
  const timers = useRef<Map<string, ReturnType<typeof setTimeout>>>(new Map());
  const [focusIdx, setFocusIdx] = useState(0);
  const rowRefs = useRef<(HTMLLIElement | null)[]>([]);

  const visible = (pending ?? []).filter((b) => !deferred.has(b.id));

  useEffect(() => {
    const map = timers.current;
    return () => map.forEach((tm) => clearTimeout(tm));
  }, []);

  // Open by id — the manage panel fetches the full booking (GET /bookings/{id}
  // live / prototype seam mock), so it opens for a REAL pending booking.
  const openManage = (b: BookingRow) => openPanel({ type: 'manage', bookingId: b.id });

  const handleApprove = (b: BookingRow) => {
    // Generate the Idempotency-Key ONCE here, at action start, and reuse it when
    // the deferred timer fires — so a retry (or a double-click that re-arms) maps
    // to the same key and the server de-dupes the approve.
    const key = idempotencyKey();
    setDeferred((s) => new Set(s).add(b.id));
    const tm = setTimeout(() => {
      approve.mutate({ bookingId: b.id, idempotencyKey: key });
      timers.current.delete(b.id);
    }, UNDO_MS);
    timers.current.set(b.id, tm);

    toast.success(`${b.patient} · ${t('status.confirmed')}`, {
      duration: UNDO_MS,
      action: {
        label: t('common.undo'),
        onClick: () => {
          const existing = timers.current.get(b.id);
          if (existing) clearTimeout(existing);
          timers.current.delete(b.id);
          setDeferred((s) => {
            const next = new Set(s);
            next.delete(b.id);
            return next;
          });
        },
      },
    });
  };

  // Keyboard: j/k move focus between rows, Enter opens manage for the focused row.
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (visible.length === 0) return;
    if (e.key === 'j' || e.key === 'ArrowDown') {
      e.preventDefault();
      setFocusIdx((i) => Math.min(visible.length - 1, i + 1));
    } else if (e.key === 'k' || e.key === 'ArrowUp') {
      e.preventDefault();
      setFocusIdx((i) => Math.max(0, i - 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const b = visible[focusIdx];
      if (b) openManage(b);
    }
  };

  useEffect(() => {
    rowRefs.current[focusIdx]?.focus();
  }, [focusIdx, visible.length]);

  return (
    <Card>
      <header className="flex items-center justify-between border-b border-line px-4 py-3">
        <div>
          <h2 className="text-sm font-semibold text-ink">{t('overview.approvalQueue')}</h2>
          <p className="text-[12px] text-muted">{t('overview.approvalQueueSub')}</p>
        </div>
        <Link
          to="/bookings"
          className="inline-flex items-center gap-1 text-[13px] font-medium text-primary hover:underline"
        >
          {t('overview.allBookings')}
          <ArrowRight size={14} aria-hidden="true" />
        </Link>
      </header>

      <section
        ref={ref}
        tabIndex={-1}
        aria-label={t('overview.approvalQueue')}
        onKeyDown={onKeyDown}
        className="px-2 py-2 focus:outline-none"
      >
        {isError ? (
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        ) : isLoading ? (
          <ul className="flex flex-col gap-1 p-2" role="status" aria-busy="true">
            {Array.from({ length: 4 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 px-2 py-2">
                <Skeleton className="h-10 w-10 rounded-full" />
                <div className="flex flex-1 flex-col gap-2">
                  <Skeleton className="h-3 w-1/3" />
                  <Skeleton className="h-3 w-1/2" />
                </div>
                <Skeleton className="h-8 w-40" />
              </li>
            ))}
          </ul>
        ) : visible.length === 0 ? (
          <EmptyState title={t('overview.emptyQueueTitle')} description={t('overview.emptyQueueBody')} />
        ) : (
          <ul className="flex flex-col">
            {visible.map((b, i) => (
              <li
                key={b.id}
                ref={(el) => {
                  rowRefs.current[i] = el;
                }}
                tabIndex={focusIdx === i ? 0 : -1}
                onFocus={() => setFocusIdx(i)}
                className="flex items-center gap-3 rounded-[var(--radius-sm)] px-2 py-2.5 outline-none focus-visible:ring-2 focus-visible:ring-primary data-[focused=true]:bg-surface-sunk"
                data-focused={focusIdx === i}
              >
                <Avatar name={b.patient} size="md" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate text-sm font-medium text-ink">{b.patient}</span>
                    <span className="mono shrink-0 rounded bg-surface-sunk px-1.5 text-[11px] text-muted">
                      #{b.token}
                    </span>
                  </div>
                  <p className={`truncate text-[12px] text-muted ${b.note.match(/[ऀ-ॿ]/) ? 'deva' : ''}`}>
                    “{b.note}”
                  </p>
                </div>
                <div className="hidden shrink-0 text-right sm:block">
                  <p className="mono text-[13px] text-ink">{istSlot(b.time)}</p>
                  <p className="text-[11px] text-muted">{b.doctorName}</p>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <Button variant="ghost" size="sm" onClick={() => openManage(b)}>
                    {t('manage.manageShort')}
                  </Button>
                  <Button variant="primary" size="sm" onClick={() => handleApprove(b)}>
                    <Check size={14} aria-hidden="true" />
                    {t('manage.approveShort')}
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>
    </Card>
  );
});
