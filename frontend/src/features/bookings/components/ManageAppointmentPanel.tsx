// Manage appointment slide-over (image copy 2). Patient chip + appointment
// details grid + "Patient said" highlight + (Phase 1) a behalf/consent summary +
// primary Approve + Check-in (for confirmed bookings) + Other actions (Reschedule,
// Swap token) + cancellation reason + danger Cancel.
//
// Approve/Cancel/Check-in are optimistic and close the panel; the queue reconciles
// via the shared bookings cache. Each action affordance is gated on its own
// in-memory permission (REACT_SKILL / RBAC: never role=== in JSX).
//
// CONSENT GATE (Phase 1): for a booking made on behalf of the patient, Approve is
// disabled until the patient's consent is 'confirmed' (or 'not_required'). The
// server enforces this with a 422; we mirror it so the button isn't misleading.
//
// Cancel requires a reason (the server requires it) — the danger button stays
// disabled until a reason is entered.

import { useState } from 'react';
import { ArrowLeftRight, CalendarClock, Check, ShieldCheck, ShieldAlert, UserCheck, Users, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { TextArea } from '@/components/ui/Field';
import { istSlot } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import type { Booking, PatientConsentStatus } from '@/lib/types';
import { useApproveBooking, useCancelBooking, useCheckInBooking } from '../api';
import { PatientChip } from './PatientChip';
import { NoShowRiskBadge } from './NoShowRiskBadge';

/** Consent states that BLOCK Approve (the server returns 422 for these). */
const CONSENT_BLOCKS_APPROVE: ReadonlySet<PatientConsentStatus> = new Set<PatientConsentStatus>([
  'pending',
  'denied',
  'expired',
]);

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
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const approve = useApproveBooking();
  const cancel = useCancelBooking();
  const checkIn = useCheckInBooking();
  const [reason, setReason] = useState('');

  // Approve is blocked while a behalf booking awaits/has-no patient consent. A
  // 'self' booking is always 'not_required' → never blocked.
  const consentBlocksApprove = CONSENT_BLOCKS_APPROVE.has(booking.patientConsentStatus);

  const onApprove = () => {
    if (consentBlocksApprove) return;
    approve.mutate({ bookingId: booking.id, idempotencyKey: idempotencyKey() });
    toast.success(`${booking.patient} · ${t('status.confirmed')}`);
    onClose();
  };

  const onCheckIn = () => {
    checkIn.mutate({ bookingId: booking.id, idempotencyKey: idempotencyKey() });
    toast.success(`${booking.patient} · ${t('status.checked_in')}`);
    onClose();
  };

  const onCancel = () => {
    if (!reason.trim()) return;
    cancel.mutate({ bookingId: booking.id, reason: reason.trim(), idempotencyKey: idempotencyKey() });
    toast(`${booking.patient} · ${t('status.cancelled')}`);
    onClose();
  };

  const onReschedule = () => {
    // Open the reschedule slide-over (URL-addressable ?panel=reschedule&id=).
    openPanel({ type: 'reschedule', bookingId: booking.id });
  };

  const details: { label: string; value: string; mono?: boolean }[] = [
    { label: t('manage.department'), value: booking.dept },
    { label: t('manage.practitioner'), value: booking.doctorName },
    { label: t('manage.date'), value: booking.date },
    { label: t('manage.timeSlot'), value: istSlot(booking.time), mono: true },
    { label: t('manage.token'), value: `#${booking.token}`, mono: true },
    { label: t('manage.source'), value: t(`source.${booking.source}`) },
  ];

  const canApprove = can('docslot.booking.approve');
  const canCheckIn = can('docslot.booking.complete'); // backend gates check-in on this key
  const canReschedule = can('docslot.booking.reschedule');
  const canCancel = can('docslot.booking.cancel');

  return (
    <SlideOver open={open} onClose={onClose} eyebrow={t('panel.manage')} title={booking.patient}>
      <div className="flex flex-col gap-5">
        <PatientChip booking={booking} />

        {/* AI no-show risk (on-demand; loading/unavailable/error all handled inside). */}
        <div>
          <NoShowRiskBadge bookingId={booking.id} />
        </div>

        {booking.bookedByType === 'behalf' ? <BehalfConsentSection booking={booking} /> : null}

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

        {canApprove ? (
          <div>
            <Button
              variant="primary"
              size="md"
              className="w-full"
              onClick={onApprove}
              disabled={approve.isPending || consentBlocksApprove}
            >
              <Check size={16} aria-hidden="true" />
              {t('manage.approveAppointment')}
            </Button>
            {consentBlocksApprove ? (
              <p className="mt-1.5 flex items-center gap-1.5 text-[12px] text-warn">
                <ShieldAlert size={13} aria-hidden="true" />
                {t('consent.awaiting')}
              </p>
            ) : null}
          </div>
        ) : null}

        {/* Check-in: only meaningful for a CONFIRMED booking (confirmed → checked_in). */}
        {canCheckIn && booking.status === 'confirmed' ? (
          <Button
            variant="subtle"
            size="md"
            className="w-full"
            onClick={onCheckIn}
            disabled={checkIn.isPending}
          >
            <UserCheck size={16} aria-hidden="true" />
            {t('manage.checkIn')}
          </Button>
        ) : null}

        {canReschedule ? (
          <section>
            <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              {t('manage.otherActions')}
            </h3>
            <div className="flex flex-col gap-2">
              <ActionRow
                icon={<CalendarClock size={16} aria-hidden="true" />}
                title={t('manage.reschedule')}
                hint={t('manage.rescheduleHint')}
                onClick={onReschedule}
              />
              <ActionRow
                icon={<ArrowLeftRight size={16} aria-hidden="true" />}
                title={t('manage.swapToken')}
                hint={t('manage.swapTokenHint')}
                trailing={<span className="mono text-[12px] text-muted">#{booking.token}</span>}
              />
            </div>
          </section>
        ) : null}

        {canCancel ? (
          <section>
            <label htmlFor="cancel-reason" className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              {t('manage.cancellationReason')}{' '}
              <span className="font-normal lowercase tracking-normal text-accent">({t('manage.cancellationRequired')})</span>
            </label>
            <TextArea
              id="cancel-reason"
              rows={2}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder={t('manage.cancellationPlaceholder')}
              required
              aria-required="true"
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
        ) : null}
      </div>
    </SlideOver>
  );
}

/** Consent status → token-driven badge classes + icon + i18n key. */
const CONSENT_BADGE: Record<
  PatientConsentStatus,
  { icon: typeof ShieldCheck; className: string; i18nKey: string }
> = {
  not_required: { icon: ShieldCheck, className: 'bg-surface-sunk text-muted border-line', i18nKey: 'consent.not_required' },
  confirmed: { icon: ShieldCheck, className: 'bg-primary-soft text-primary border-primary-soft', i18nKey: 'consent.confirmed' },
  pending: { icon: ShieldAlert, className: 'bg-warn-soft text-warn border-warn-soft', i18nKey: 'consent.pending' },
  denied: { icon: ShieldAlert, className: 'bg-accent-soft text-accent border-accent-soft', i18nKey: 'consent.denied' },
  expired: { icon: ShieldAlert, className: 'bg-accent-soft text-accent border-accent-soft', i18nKey: 'consent.expired' },
};

/** Read-only behalf/consent summary for a booking made on behalf of the patient. */
function BehalfConsentSection({ booking }: { booking: Booking }) {
  const { t } = useTranslation();
  const relationLabel = booking.behalfRelation
    ? t(`behalf.relation.${booking.behalfRelation}`)
    : t('behalf.relation.other');
  const badge = CONSENT_BADGE[booking.patientConsentStatus];
  const BadgeIcon = badge.icon;

  return (
    <section className="rounded-[var(--radius)] border border-line p-3">
      <div className="flex items-start gap-3">
        <span className="mt-0.5 text-muted" aria-hidden="true">
          <Users size={16} />
        </span>
        <div className="min-w-0 flex-1">
          <p className="text-[13px] font-medium text-ink">{t('behalf.bookedOnBehalf', { relation: relationLabel })}</p>
          <div className="mt-1.5 flex items-center gap-2">
            <span className="text-[11px] uppercase tracking-wider text-muted-2">{t('behalf.consentLabel')}</span>
            <span
              className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[12px] font-medium ${badge.className}`}
            >
              <BadgeIcon size={12} aria-hidden="true" />
              {t(badge.i18nKey)}
            </span>
          </div>
        </div>
      </div>
    </section>
  );
}

function ActionRow({
  icon,
  title,
  hint,
  trailing,
  onClick,
}: {
  icon: React.ReactNode;
  title: string;
  hint: string;
  trailing?: React.ReactNode;
  onClick?: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!onClick}
      className="flex w-full items-center gap-3 rounded-[var(--radius-sm)] border border-line px-3 py-2.5 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:cursor-not-allowed disabled:opacity-60 disabled:hover:bg-transparent"
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
