// Manage appointment slide-over (image copy 2). Patient chip + appointment
// details grid + "Patient said" highlight + primary Approve + Other actions
// (Reschedule, Swap token) + cancellation reason + danger Cancel.
//
// Approve/Cancel are optimistic (useApproveBooking/useCancelBooking) and close
// the panel; the queue reconciles via the shared bookings cache. Cancel requires
// a reason (the textarea) — the danger button is disabled until one is entered.

import { useState } from 'react';
import { ArrowLeftRight, CalendarClock, Check, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { TextArea } from '@/components/ui/Field';
import { istSlot } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import type { Booking } from '@/lib/types';
import { useApproveBooking, useCancelBooking } from '../api';
import { PatientChip } from './PatientChip';

export function ManageAppointmentPanel({
  booking,
  open,
  onClose,
}: {
  booking: Booking;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const approve = useApproveBooking();
  const cancel = useCancelBooking();
  const [reason, setReason] = useState('');

  const onApprove = () => {
    approve.mutate({ bookingId: booking.id, idempotencyKey: idempotencyKey() });
    toast.success(`${booking.patient} · ${t('status.confirmed')}`);
    onClose();
  };

  const onCancel = () => {
    if (!reason.trim()) return;
    cancel.mutate({ bookingId: booking.id, reason: reason.trim(), idempotencyKey: idempotencyKey() });
    toast(`${booking.patient} · ${t('status.cancelled')}`);
    onClose();
  };

  const details: { label: string; value: string; mono?: boolean }[] = [
    { label: t('manage.department'), value: booking.dept },
    { label: t('manage.practitioner'), value: booking.doctorName },
    { label: t('manage.date'), value: booking.date },
    { label: t('manage.timeSlot'), value: istSlot(booking.time), mono: true },
    { label: t('manage.token'), value: `#${booking.token}`, mono: true },
    { label: t('manage.source'), value: t(`source.${booking.source}`) },
  ];

  return (
    <SlideOver open={open} onClose={onClose} eyebrow={t('panel.manage')} title={booking.patient}>
      <div className="flex flex-col gap-5">
        <PatientChip booking={booking} />

        <section>
          <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('manage.appointmentDetails')}
          </h3>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-3 rounded-[var(--radius)] border border-line p-3">
            {details.map((d) => (
              <div key={d.label}>
                <dt className="text-[11px] uppercase tracking-wider text-muted-2">{d.label}</dt>
                <dd className={`mt-0.5 text-[13px] text-ink ${d.mono ? 'mono' : ''}`}>{d.value}</dd>
              </div>
            ))}
          </dl>
        </section>

        {booking.note && booking.note !== '—' ? (
          <section>
            <h3 className="mb-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              {t('manage.patientSaid')}
            </h3>
            <p
              className={`rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2 text-[13px] text-ink ${booking.note.match(/[ऀ-ॿ]/) ? 'deva' : ''}`}
            >
              “{booking.note}”
            </p>
          </section>
        ) : null}

        <Button variant="primary" size="md" className="w-full" onClick={onApprove} disabled={approve.isPending}>
          <Check size={16} aria-hidden="true" />
          {t('manage.approveAppointment')}
        </Button>

        <section>
          <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('manage.otherActions')}
          </h3>
          <div className="flex flex-col gap-2">
            <ActionRow
              icon={<CalendarClock size={16} aria-hidden="true" />}
              title={t('manage.reschedule')}
              hint={t('manage.rescheduleHint')}
            />
            <ActionRow
              icon={<ArrowLeftRight size={16} aria-hidden="true" />}
              title={t('manage.swapToken')}
              hint={t('manage.swapTokenHint')}
              trailing={<span className="mono text-[12px] text-muted">#{booking.token}</span>}
            />
          </div>
        </section>

        <section>
          <label htmlFor="cancel-reason" className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('manage.cancellationReason')}{' '}
            <span className="font-normal lowercase tracking-normal text-muted-2">({t('common.optional')})</span>
          </label>
          <TextArea
            id="cancel-reason"
            rows={2}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder={t('manage.cancellationPlaceholder')}
          />
          <Button
            variant="danger"
            size="md"
            className="mt-2 w-full"
            onClick={onCancel}
            disabled={!reason.trim() || cancel.isPending}
          >
            <X size={16} aria-hidden="true" />
            {t('manage.cancelAppointment')}
          </Button>
        </section>
      </div>
    </SlideOver>
  );
}

function ActionRow({
  icon,
  title,
  hint,
  trailing,
}: {
  icon: React.ReactNode;
  title: string;
  hint: string;
  trailing?: React.ReactNode;
}) {
  return (
    <button
      type="button"
      className="flex w-full items-center gap-3 rounded-[var(--radius-sm)] border border-line px-3 py-2.5 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
    >
      <span className="text-primary">{icon}</span>
      <span className="min-w-0 flex-1">
        <span className="block text-[13px] font-medium text-ink">{title}</span>
        <span className="block text-[11px] text-muted">{hint}</span>
      </span>
      {trailing}
    </button>
  );
}
