// Status pill (REACT_SKILL pattern 12). ALWAYS icon + text + color — never color
// alone (a11y). Colors come from tokens only. Note the healthcare anti-pattern
// guard: terracotta (accent) is reserved for genuinely negative states
// (cancelled / no-show), not for "pending".

import { CalendarClock, Check, CircleCheck, Clock, UserCheck, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { BookingStatus } from '@/lib/types';

interface StatusConfig {
  icon: typeof Check;
  /** token-driven classes for bg + text + border */
  className: string;
  i18nKey: string;
}

const CONFIG: Record<BookingStatus, StatusConfig> = {
  confirmed: { icon: Check, className: 'bg-primary-soft text-primary border-primary-soft', i18nKey: 'status.confirmed' },
  // checked_in: patient has arrived — a positive, informational milestone (not
  // destructive). Distinct icon + info tint so it reads apart from confirmed.
  checked_in: { icon: UserCheck, className: 'bg-info-soft text-info border-info-soft', i18nKey: 'status.checked_in' },
  pending: { icon: Clock, className: 'bg-warn-soft text-warn border-warn-soft', i18nKey: 'status.pending' },
  completed: { icon: CircleCheck, className: 'bg-surface-sunk text-muted border-line', i18nKey: 'status.completed' },
  cancelled: { icon: X, className: 'bg-accent-soft text-accent border-accent-soft', i18nKey: 'status.cancelled' },
  no_show: { icon: X, className: 'bg-danger-soft text-danger border-danger-soft', i18nKey: 'status.no_show' },
  // Rescheduled is a neutral/informational state (not destructive) — info tint.
  rescheduled: { icon: CalendarClock, className: 'bg-info-soft text-info border-info-soft', i18nKey: 'status.rescheduled' },
};

export function StatusPill({ status }: { status: BookingStatus }) {
  const { t } = useTranslation();
  const cfg = CONFIG[status];
  const Icon = cfg.icon;
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[12px] font-medium ${cfg.className}`}
    >
      <Icon size={12} aria-hidden="true" />
      {t(cfg.i18nKey)}
    </span>
  );
}
