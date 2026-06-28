// Reschedule slide-over (Phase 1). Right-side panel (the primary CRUD modality),
// URL-addressable via ?panel=reschedule&id=<bookingId> (the loader fetches the
// booking, so a refresh/deep-link restores it). Flow:
//   show the booking's doctor's available slots → staff picks a new slot →
//   optional reason → POST /bookings/{id}/reschedule.
//
// A reschedule SUPERSEDES the old booking with a new one (server-side lineage).
// On success we toast + close; the hook invalidates the booking list/detail +
// dashboard + badges so every view reflects the change (server state wins). The
// POST carries a STABLE Idempotency-Key generated once at action start.
//
// CONTRACT GAP (live mode): GET /bookings/{id} returns no doctorId, so the
// doctor's slot listing (GET /doctors/{id}/slots) can't be keyed in live mode —
// the panel shows a graceful "doctor unknown" notice there. The reschedule POST
// itself doesn't need the doctorId (newDoctorId is optional → same doctor), so
// adding doctorId to the booking-detail DTO would light up the live slot grid
// with zero panel changes. Mock mode (booking carries a real doctorId) is fully
// functional today.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { TextArea } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { istSlot } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import type { Slot } from '@/lib/mock/contracts';
import type { Booking } from '@/lib/types';
import { useSlots, useRescheduleBooking } from '../api';

export function ReschedulePanel({
  booking,
  open,
  onClose,
}: {
  booking: Booking;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  // The booking's doctor's available slots. In live mode booking.doctorId is ''
  // (the detail DTO omits it) → the query is disabled and we show the gap notice.
  const hasDoctor = Boolean(booking.doctorId);
  const { data: slots, isLoading, isError, refetch } = useSlots(booking.doctorId || undefined);
  const reschedule = useRescheduleBooking();
  const [slot, setSlot] = useState<Slot | null>(null);
  const [reason, setReason] = useState('');

  const onConfirm = async () => {
    if (!slot) return;
    try {
      await reschedule.mutateAsync({
        bookingId: booking.id,
        // Live: the slot GUID (slotId) is required. Mock: no slotId, so the time
        // string flows through (the mock reschedule ignores it).
        newSlotId: slot.slotId ?? slot.time,
        reason: reason.trim() || undefined,
        // Stable key per confirm — a retried reschedule maps to the same key.
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('reschedule.done', { patient: booking.patient }));
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('reschedule.eyebrow')}
      title={booking.patient}
      headerExtra={
        <dl className="grid grid-cols-3 gap-2 text-[12px]">
          <div>
            <dt className="text-[10px] uppercase tracking-wider text-muted-2">{t('reschedule.practitioner')}</dt>
            <dd className="text-ink">{booking.doctorName}</dd>
          </div>
          <div>
            <dt className="text-[10px] uppercase tracking-wider text-muted-2">{t('reschedule.currentSlot')}</dt>
            <dd className="mono text-ink">{istSlot(booking.time)}</dd>
          </div>
          <div>
            <dt className="text-[10px] uppercase tracking-wider text-muted-2">{t('reschedule.token')}</dt>
            <dd className="mono text-ink">#{booking.token}</dd>
          </div>
        </dl>
      }
      footer={
        <div className="grid w-full grid-cols-2 gap-2">
          <Button variant="ghost" size="md" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="primary"
            size="md"
            disabled={!slot || reschedule.isPending}
            onClick={() => void onConfirm()}
          >
            {t('reschedule.confirm')}
          </Button>
        </div>
      }
    >
      <div className="flex flex-col gap-5">
        <section>
          <span className="mb-2 block text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('reschedule.pickNewSlot')}
          </span>
          {!hasDoctor ? (
            // Live-mode gap: no doctorId on the booking detail → can't list slots.
            <EmptyState title={t('reschedule.doctorUnknownTitle')} description={t('reschedule.doctorUnknownBody')} />
          ) : isError ? (
            <EmptyState
              title={t('error.genericTitle')}
              description={t('error.genericBody')}
              actionLabel={t('common.retry')}
              onAction={() => void refetch()}
            />
          ) : isLoading || !slots ? (
            <div className="grid grid-cols-4 gap-2" role="status" aria-busy="true">
              {Array.from({ length: 8 }).map((_, i) => (
                <Skeleton key={i} className="h-12 w-full" />
              ))}
            </div>
          ) : slots.length === 0 ? (
            <p className="text-[12px] text-muted">{t('reschedule.noSlots')}</p>
          ) : (
            <div className="grid grid-cols-4 gap-2">
              {slots.map((s) => {
                const disabled = s.state === 'full' || s.state === 'blocked';
                const active = slot ? slot.time === s.time && slot.slotId === s.slotId : false;
                return (
                  <button
                    key={s.slotId ?? s.time}
                    type="button"
                    disabled={disabled}
                    onClick={() => setSlot(s)}
                    aria-pressed={active}
                    className={[
                      'mono rounded-[var(--radius-sm)] border px-1 py-2 text-[12px] transition-colors',
                      active
                        ? 'border-primary bg-primary text-bg'
                        : disabled
                          ? 'cursor-not-allowed border-line bg-surface-sunk text-muted-2 line-through'
                          : 'border-line bg-surface text-ink hover:bg-surface-sunk',
                    ].join(' ')}
                  >
                    {s.time}
                  </button>
                );
              })}
            </div>
          )}
        </section>

        <section>
          <label
            htmlFor="reschedule-reason"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2"
          >
            {t('reschedule.reason')}{' '}
            <span className="font-normal lowercase tracking-normal text-muted-2">({t('common.optional')})</span>
          </label>
          <TextArea
            id="reschedule-reason"
            rows={2}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder={t('reschedule.reasonPlaceholder')}
          />
        </section>

        <p className="text-[12px] text-muted">{t('reschedule.lineageNote')}</p>
      </div>
    </SlideOver>
  );
}
