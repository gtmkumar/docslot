// Always-visible safety strip: the patient's active ALLERGIES + CHRONIC CONDITIONS,
// pulled from medical history, shown before the doctor prescribes. Critical items
// use the danger token. Purpose-gated read via the seam (no cross-feature import).

import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, HeartPulse, ShieldCheck } from 'lucide-react';
import { Skeleton } from '@/components/ui/Skeleton';
import { listMedicalHistory } from '@/lib/backend';
import type { PurposeOfUse } from '@/lib/mock/contracts';

const DEVA = /[ऀ-ॿ]/;

export function SafetyStrip({ patientId, purpose }: { patientId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const { data, isLoading, isError } = useQuery({
    queryKey: ['clinical', 'history', patientId, purpose] as const,
    queryFn: () => listMedicalHistory(patientId, purpose),
    enabled: Boolean(patientId) && Boolean(purpose),
    retry: false,
  });

  if (isLoading) {
    return <Skeleton className="h-14 w-full rounded-[var(--radius)]" />;
  }
  // A denied read (consent 403) or error must NOT masquerade as "no allergies" — say so.
  if (isError) {
    return (
      <div className="flex items-center gap-2 rounded-[var(--radius)] border border-line bg-surface-sunk px-3 py-2.5 text-[12px] text-muted">
        <AlertTriangle size={15} aria-hidden="true" />
        {t('consult.safety.unavailable')}
      </div>
    );
  }

  const active = (data ?? []).filter((h) => h.isActive);
  const allergies = active.filter((h) => h.recordType === 'allergy');
  const chronic = active.filter((h) => h.recordType === 'chronic_condition');

  if (allergies.length === 0 && chronic.length === 0) {
    return (
      <div className="flex items-center gap-2 rounded-[var(--radius)] border border-line bg-surface-sunk px-3 py-2.5 text-[12px] text-muted">
        <ShieldCheck size={15} className="text-primary" aria-hidden="true" />
        {t('consult.safety.none')}
      </div>
    );
  }

  const hasCritical = allergies.some((a) => a.isCritical);
  return (
    <div className={`rounded-[var(--radius)] border p-3 ${hasCritical ? 'border-danger/40 bg-danger-soft' : 'border-line bg-surface'}`}>
      {allergies.length > 0 ? (
        <div className="flex flex-wrap items-center gap-2">
          <span className={`inline-flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wide ${hasCritical ? 'text-danger' : 'text-warn'}`}>
            <AlertTriangle size={14} aria-hidden="true" />
            {t('consult.safety.allergies')}
          </span>
          <ul className="flex flex-wrap gap-1.5">
            {allergies.map((a) => (
              <li key={a.historyId}>
                <span className={`inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-[12px] font-medium ${a.isCritical ? 'bg-danger text-bg' : 'bg-warn-soft text-warn'} ${DEVA.test(a.title) ? 'deva' : ''}`}>
                  {a.title}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {chronic.length > 0 ? (
        <div className={`flex flex-wrap items-center gap-2 ${allergies.length > 0 ? 'mt-2' : ''}`}>
          <span className="inline-flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wide text-muted">
            <HeartPulse size={14} aria-hidden="true" />
            {t('consult.safety.chronic')}
          </span>
          <ul className="flex flex-wrap gap-1.5">
            {chronic.map((c) => (
              <li key={c.historyId}>
                <span className={`inline-flex items-center rounded-full bg-surface-sunk px-2.5 py-1 text-[12px] font-medium text-ink ${DEVA.test(c.title) ? 'deva' : ''}`}>
                  {c.title}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
}
