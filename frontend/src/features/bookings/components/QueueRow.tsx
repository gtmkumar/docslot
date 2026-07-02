// A single reception-queue row (replaces the old flat table row). Shows the
// token chip, patient + demographic subline (masked phone only — DPDP), doctor +
// specialty, the channel badge, the truncated bilingual reason note, the IST slot
// time and a StatusPill. Contextual actions depend on status:
//   - pending  → inline [WhatsApp (whatsapp-only)] [Manage] [Approve] buttons
//   - other    → a real accessible kebab (KebabMenu) whose items each gate on a
//                permission and route to the existing hook/panel.
// Destructive transitions (no-show) live ONLY in the kebab, never as a bare button.
// Tokens only; React Compiler is on (no manual memoization).

import { Check, CalendarClock, CircleCheck, ClipboardList, MessageCircle, Phone, SlidersHorizontal, Stethoscope, UserCheck, UserX, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { KebabMenu, type KebabItem } from '@/components/ui/KebabMenu';
import { StatusPill } from '@/components/ui/StatusPill';
import { istSlot } from '@/lib/format';
import type { BookingRow } from '@/lib/mock/contracts';
import type { BookingSource, BookingStatus } from '@/lib/types';
import { useUI } from '@/stores/ui';

/** Effective permission flags resolved ONCE by the screen (in-memory set) and
 *  passed down — never a per-row network check, never a role branch. */
export interface QueuePerms {
  approve: boolean;
  reschedule: boolean;
  /** docslot.booking.complete — gates both Check-in and Complete. */
  complete: boolean;
  noShow: boolean;
  cancel: boolean;
  /** docslot.prescription.create — gates the "Prescribe" action → composer. */
  prescribe: boolean;
}

/** Deva detection: a Devanagari code-point anywhere in the note flips the font token. */
const DEVA = /[ऀ-ॿ]/;

/** Channel badge: WhatsApp = green pill + live dot; walk-in / phone / dashboard /
 *  api = neutral pill (+ a lucide glyph for walk-in and phone). Tokens only. */
function ChannelBadge({ source }: { source: BookingSource }) {
  const { t } = useTranslation();
  if (source === 'whatsapp') {
    return (
      <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-whatsapp-soft px-2 py-0.5 text-[11px] font-medium text-whatsapp-ink">
        <span className="h-1.5 w-1.5 rounded-full bg-whatsapp" aria-hidden="true" />
        {t('source.whatsapp')}
      </span>
    );
  }
  const Icon = source === 'walk_in' ? ClipboardList : source === 'phone_call' ? Phone : null;
  return (
    <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-surface-sunk px-2 py-0.5 text-[11px] font-medium text-muted">
      {Icon ? <Icon size={11} aria-hidden="true" /> : null}
      {t(`source.${source}`)}
    </span>
  );
}

/** Build the demographic subline: "31F", "31", "F" or "" (age/gender may be null),
 *  then " · " + the masked phone. Never a raw phone. */
function subline(b: BookingRow): string {
  const demo = b.age != null ? `${b.age}${b.gender ?? ''}` : (b.gender ?? '');
  return [demo, b.maskedPhone].filter(Boolean).join(' · ');
}

export function QueueRow({
  booking: b,
  perms,
  onCheckIn,
  onComplete,
  onNoShow,
}: {
  booking: BookingRow;
  perms: QueuePerms;
  onCheckIn: (b: BookingRow) => void;
  onComplete: (b: BookingRow) => void;
  onNoShow: (b: BookingRow) => void;
}) {
  const { t } = useTranslation();
  const openPanel = useUI((s) => s.openPanel);
  const navigate = useNavigate();
  const isDeva = DEVA.test(b.note);
  const status = b.status as BookingStatus;

  const openManage = () => openPanel({ type: 'manage', bookingId: b.id });
  const openReschedule = () => openPanel({ type: 'reschedule', bookingId: b.id });
  const goPrescribe = () => void navigate({ to: '/consult/$bookingId', params: { bookingId: b.id } });
  // Prescribing happens during/after the consultation → offer it once the patient is
  // confirmed or checked in. Gated on the resolved permission set (never a role).
  const canPrescribeNow = perms.prescribe && (status === 'confirmed' || status === 'checked_in');

  return (
    <li className="flex items-center gap-3 border-b border-line px-3 py-3 transition-colors last:border-0 hover:bg-surface-sunk sm:px-4">
      {/* Token + patient + demographic subline */}
      <div className="flex min-w-0 flex-[2] items-center gap-3">
        <Avatar name={b.patient} size="md" />
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="mono shrink-0 rounded bg-surface-sunk px-1.5 py-0.5 text-[11px] text-muted">
              #{b.token}
            </span>
            <span className="truncate text-sm font-medium text-ink">{b.patient}</span>
          </div>
          <p className="mono truncate text-[11px] text-muted-2">{subline(b)}</p>
        </div>
      </div>

      {/* Doctor + specialty */}
      <div className="hidden min-w-0 flex-1 lg:block">
        <p className="truncate text-[13px] text-ink">{b.doctorName}</p>
        <p className="truncate text-[11px] text-muted-2">{b.dept}</p>
      </div>

      {/* Channel + reason note (bilingual) */}
      <div className="hidden min-w-0 flex-1 items-center gap-2 md:flex">
        <ChannelBadge source={b.source as BookingSource} />
        {b.note ? (
          <span className={`truncate text-[12px] text-muted ${isDeva ? 'deva' : ''}`} title={b.note}>
            {b.note}
          </span>
        ) : null}
      </div>

      {/* Slot time (explicit IST) */}
      <span className="mono hidden shrink-0 text-[13px] text-ink sm:inline">{istSlot(b.time)}</span>

      {/* Status */}
      <span className="hidden shrink-0 sm:inline-flex">
        <StatusPill status={status} />
      </span>

      {/* Contextual actions */}
      <div className="flex shrink-0 items-center justify-end gap-1.5">
        {status === 'pending' ? (
          <>
            {b.source === 'whatsapp' ? (
              <button
                type="button"
                onClick={() => openPanel({ type: 'conversation', bookingId: b.id })}
                aria-label={t('bookings.actionWhatsApp')}
                title={t('bookings.actionWhatsApp')}
                className="inline-flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] text-whatsapp-ink transition-colors hover:bg-whatsapp-soft focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <MessageCircle size={16} aria-hidden="true" />
              </button>
            ) : null}
            <Button variant="ghost" size="sm" onClick={openManage}>
              {t('bookings.manage')}
            </Button>
            {perms.approve ? (
              <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'approve', bookingId: b.id })}>
                <Check size={14} aria-hidden="true" />
                {t('bookings.approve')}
              </Button>
            ) : null}
          </>
        ) : (
          <>
            {canPrescribeNow ? (
              <Button variant="ghost" size="sm" onClick={goPrescribe}>
                <Stethoscope size={14} aria-hidden="true" />
                {t('bookings.prescribe')}
              </Button>
            ) : null}
            <KebabMenu label={t('bookings.moreActions', { patient: b.patient })} items={kebabItems()} />
          </>
        )}
      </div>
    </li>
  );

  /** Status-appropriate kebab items — each gated on its permission and wired to an
   *  existing hook (check-in / complete / no-show) or slide-over (manage / reschedule
   *  / cancel-via-manage). Cancel always routes to Manage because a cancel REASON is
   *  required (never a one-click cancel). */
  function kebabItems(): KebabItem[] {
    const items: KebabItem[] = [
      { key: 'manage', label: t('bookings.manage'), icon: <SlidersHorizontal size={15} />, onSelect: openManage },
    ];
    const canReschedule = status === 'confirmed' || status === 'no_show';
    if (perms.reschedule && canReschedule) {
      items.push({ key: 'reschedule', label: t('bookings.actionReschedule'), icon: <CalendarClock size={15} />, onSelect: openReschedule });
    }
    if (perms.complete && status === 'confirmed') {
      items.push({ key: 'checkin', label: t('bookings.actionCheckIn'), icon: <UserCheck size={15} />, onSelect: () => onCheckIn(b) });
    }
    if (perms.complete && (status === 'confirmed' || status === 'checked_in')) {
      items.push({ key: 'complete', label: t('bookings.actionComplete'), icon: <CircleCheck size={15} />, onSelect: () => onComplete(b) });
    }
    if (perms.noShow && status === 'confirmed') {
      items.push({ key: 'noshow', label: t('bookings.actionNoShow'), icon: <UserX size={15} />, tone: 'danger', onSelect: () => onNoShow(b) });
    }
    if (perms.cancel && (status === 'confirmed' || status === 'checked_in')) {
      items.push({ key: 'cancel', label: t('bookings.actionCancel'), icon: <X size={15} />, tone: 'danger', onSelect: openManage });
    }
    return items;
  }
}
