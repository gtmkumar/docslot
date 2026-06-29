// Compact AI no-show risk badge for the booking manage/approve slide-overs.
// Renders "No-show risk: High · 72%" with a band-driven, token-only color (never
// a hex): low → primary/positive, medium → warn/amber, high → danger/red.
//
// States (REACT_SKILL: loading + unavailable + error all handled):
//   - loading      → a small skeleton pill (shaped like the badge).
//   - available:false (AI sibling unreachable) → a subtle muted "risk unavailable"
//     chip — NEVER a fabricated score.
//   - error (404 / network) → the same subtle "risk unavailable" chip.
//   - success      → the band chip + probability %.
//
// NO PHI: the no-show endpoint carries no patient data; this fetch sends no
// purpose-of-use header. Fetched on demand (enabled only while the panel is open).

import { Activity, TrendingUp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Skeleton } from '@/components/ui/Skeleton';
import { useNoShowRisk } from '../ai';
import type { RiskBand } from '@/lib/mock/contracts';

/** Band → token-only chip classes (zero hex). low is reassuring (teal/positive),
 *  medium is caution (amber), high is alarm (red) — never color alone, each pairs
 *  with the icon + text label below for a11y. */
const BAND_CLASS: Record<RiskBand, string> = {
  low: 'bg-primary-soft text-primary border-primary-soft',
  medium: 'bg-warn-soft text-warn border-warn-soft',
  high: 'bg-danger-soft text-danger border-danger-soft',
};

export function NoShowRiskBadge({ bookingId }: { bookingId: string }) {
  const { t } = useTranslation();
  const { data, isLoading, isError } = useNoShowRisk(bookingId);

  if (isLoading) {
    return <Skeleton className="h-6 w-44" aria-label={t('common.loading')} />;
  }

  // Unreachable AI service (available:false) OR a fetch failure → subtle, honest
  // "unavailable" chip. We never invent a band/score.
  if (isError || !data || !data.available || !data.band || data.probability === null) {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full border border-line bg-surface-sunk px-2.5 py-0.5 text-[12px] font-medium text-muted">
        <Activity size={12} aria-hidden="true" />
        {t('noShow.unavailable')}
      </span>
    );
  }

  const pct = Math.round(data.probability * 100);
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[12px] font-medium ${BAND_CLASS[data.band]}`}
    >
      <TrendingUp size={12} aria-hidden="true" />
      <span>
        {t('noShow.label')}: {t(`noShow.band.${data.band}`)} · <span className="mono">{pct}%</span>
      </span>
    </span>
  );
}
