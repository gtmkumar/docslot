// One practitioner card for the doctors directory. Avatar + name + qualifications,
// specialty tag (token-tinted), Fee / Today / Rating stats, an OPD-load progress
// bar, today's hours + next-available, and Schedule / Profile actions + overflow.
// Zero hex — the specialty tag colour comes from a token colorKey map.

import { Clock, MoreHorizontal, Star } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { inr, istSlot } from '@/lib/format';
import type { DoctorCard } from '@/lib/mock/contracts';

// Token-tint classes for the specialty tag, keyed off the same colorKey the bar
// uses. Never a hex.
const TAG_TINT: Record<string, string> = {
  primary: 'bg-primary-soft text-primary',
  accent: 'bg-accent-soft text-accent',
  info: 'bg-info-soft text-info',
  warn: 'bg-warn-soft text-warn',
  muted: 'bg-surface-sunk text-muted',
};

export function DoctorCardView({ doctor }: { doctor: DoctorCard }) {
  const { t } = useTranslation();
  const tint = TAG_TINT[doctor.colorKey] ?? TAG_TINT.muted;

  return (
    <Card className="flex flex-col gap-4 p-4">
      <header className="flex items-start gap-3">
        <Avatar name={doctor.name} initials={doctor.initials} size="lg" />
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-semibold text-ink">{doctor.name}</p>
          <p className="truncate text-[12px] text-muted">{doctor.qualification}</p>
          <span className={`mt-1.5 inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium ${tint}`}>
            {doctor.spec}
          </span>
        </div>
        <button
          type="button"
          aria-label={t('doctors.moreActions', { name: doctor.name })}
          className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-[var(--radius-sm)] text-muted-2 transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
        >
          <MoreHorizontal size={16} aria-hidden="true" />
        </button>
      </header>

      <div className="grid grid-cols-3 gap-px overflow-hidden rounded-[var(--radius-sm)] border border-line bg-line">
        <Stat label={t('doctors.fee')} value={inr(doctor.feeInr)} />
        <Stat label={t('doctors.today')} value={String(doctor.todayCount)} />
        <Stat
          label={t('doctors.rating')}
          value={
            <span className="inline-flex items-center gap-1">
              <Star size={13} className="text-warn" aria-hidden="true" fill="currentColor" />
              {doctor.rating}
            </span>
          }
        />
      </div>

      <div>
        <div className="mb-1 flex items-center justify-between text-[12px]">
          <span className="text-muted">{t('doctors.opdLoad')}</span>
          <span className="mono text-muted">
            {doctor.todayCount}/{doctor.todayCapacity}
          </span>
        </div>
        <ProgressBar
          value={doctor.todayCount}
          max={doctor.todayCapacity}
          colorKey={doctor.colorKey}
          label={t('doctors.loadLabel', {
            name: doctor.name,
            booked: doctor.todayCount,
            capacity: doctor.todayCapacity,
          })}
        />
      </div>

      <dl className="flex items-center justify-between text-[12px]">
        <div>
          <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('doctors.todayHours')}</dt>
          <dd className="mono mt-0.5 text-ink">{doctor.hours}</dd>
        </div>
        <div className="text-right">
          <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('doctors.nextAvailable')}</dt>
          <dd className="mt-0.5 inline-flex items-center gap-1 text-primary">
            <Clock size={12} aria-hidden="true" />
            <span className="mono">{istSlot(doctor.nextSlot)}</span>
          </dd>
        </div>
      </dl>

      <div className="grid grid-cols-2 gap-2">
        <Button variant="primary" size="sm">
          {t('doctors.schedule')}
        </Button>
        <Button variant="ghost" size="sm">
          {t('doctors.profile')}
        </Button>
      </div>
    </Card>
  );
}

function Stat({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="bg-surface p-2.5 text-center">
      <p className="text-[10px] uppercase tracking-wider text-muted-2">{label}</p>
      <p className="mono mt-0.5 text-sm font-semibold text-ink">{value}</p>
    </div>
  );
}
