// Patient identity chip used at the top of the manage / approve / conversation
// panels (image copy 2/3/4). Avatar + name + booking id + demographics + status.
// Phone is shown UNMASKED here intentionally — this is an opened detail panel
// where the staff action (calling/confirming the patient) requires it; list
// views mask it. See lib/format.maskPhone and the PHI note in the report.

import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { StatusPill } from '@/components/ui/StatusPill';
import type { Booking } from '@/lib/types';

export function PatientChip({ booking, slotTime }: { booking: Booking; slotTime?: string }) {
  const { t } = useTranslation();
  const genderLabel = booking.gender === 'F' ? t('newBooking.sexFemale') : booking.gender === 'M' ? t('newBooking.sexMale') : t('newBooking.sexOther');
  return (
    <div className="flex items-start gap-3 rounded-[var(--radius)] border border-line bg-bg-2 p-3">
      <Avatar name={booking.patient} size="lg" />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <p className="truncate text-sm font-semibold text-ink">{booking.patient}</p>
          <span className="mono text-[12px] text-muted">{booking.id}</span>
        </div>
        <p className="mono mt-0.5 text-[12px] text-muted">{booking.phone}</p>
        <div className="mt-1.5 flex items-center gap-2">
          <span className="text-[11px] uppercase tracking-wider text-muted-2">
            {booking.age} · {genderLabel}
          </span>
          <StatusPill status={booking.status} />
        </div>
      </div>
      {slotTime ? <span className="mono shrink-0 text-[13px] font-medium text-ink">{slotTime}</span> : null}
    </div>
  );
}
