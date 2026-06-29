// AI triage assist for the new-booking intake wizard (complaint/reason step).
//
// A "Run triage" affordance submits the typed complaint (+ the wizard's patient
// age) to the triage endpoint and renders the advisory result:
//   - an urgency banner (emergency = most prominent/danger → low = muted),
//   - a red-flags list, a surfaced-symptoms list,
//   - suggested-doctor chips. Clicking a chip asks the wizard to pre-select that
//     doctor (best-effort — only takes effect if the doctor is in the loaded list).
//
// PHI: the complaint is PHI. It is read from the form at submit time and passed as
// the mutation variable only — it is NEVER logged, NEVER put in a query key, and
// NEVER persisted (this is a mutation, not a cached query). The intake path is the
// FREE-TEXT triage: NO patientId/bookingId is sent, so the server requires no
// X-Purpose-Of-Use header.
//
// All states (REACT_SKILL): idle, loading (skeleton), empty (no red-flags/doctors),
// available:false ("triage unavailable"), and error.

import { AlertTriangle, Sparkles, Stethoscope } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { inr, istSlot } from '@/lib/format';
import { useTriage } from '../ai';
import type { SuggestedDoctor, TriageResult, UrgencyBand } from '@/lib/mock/contracts';

/** Urgency band → token-only banner classes (zero hex). emergency is the loudest
 *  (terracotta/accent), high is danger (red), medium is caution (amber), low is
 *  reassuring (teal/positive). Paired with an icon + text label (never color alone). */
const URGENCY_CLASS: Record<UrgencyBand, string> = {
  emergency: 'bg-accent-soft text-accent border-accent-soft',
  high: 'bg-danger-soft text-danger border-danger-soft',
  medium: 'bg-warn-soft text-warn border-warn-soft',
  low: 'bg-primary-soft text-primary border-primary-soft',
};

export function TriageAssist({
  complaint,
  patientAge,
  onPickDoctor,
}: {
  /** The currently-typed complaint (PHI) — read live from the form. */
  complaint: string;
  /** The age the wizard already collected (parsed); undefined if blank/invalid. */
  patientAge: number | undefined;
  /** Best-effort: pre-select this suggested doctor in the wizard's doctor step. */
  onPickDoctor: (doctorId: string) => void;
}) {
  const { t } = useTranslation();
  const triage = useTriage();
  const canRun = complaint.trim().length > 0 && !triage.isPending;

  const onRun = () => {
    if (!canRun) return;
    // FREE-TEXT path: no patientId/bookingId → no purpose-of-use needed.
    triage.mutate({ complaint: complaint.trim(), patientAge });
  };

  return (
    <section className="rounded-[var(--radius)] border border-line p-3">
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          <Sparkles size={13} aria-hidden="true" />
          {t('triage.heading')}
        </span>
        <Button variant="subtle" size="sm" type="button" onClick={onRun} disabled={!canRun}>
          {triage.isPending ? t('triage.running') : t('triage.run')}
        </Button>
      </div>

      {triage.isPending ? (
        <div className="mt-3 flex flex-col gap-2" role="status" aria-busy="true">
          <Skeleton className="h-9 w-full" />
          <Skeleton className="h-12 w-full" />
        </div>
      ) : triage.isError ? (
        <p className="mt-3 flex items-center gap-1.5 text-[12px] text-danger">
          <AlertTriangle size={13} aria-hidden="true" />
          {t('triage.error')}
        </p>
      ) : triage.data ? (
        <TriageResultBody result={triage.data} onPickDoctor={onPickDoctor} />
      ) : (
        <p className="mt-2 text-[12px] text-muted">{t('triage.idleHint')}</p>
      )}
    </section>
  );
}

function TriageResultBody({
  result,
  onPickDoctor,
}: {
  result: TriageResult;
  onPickDoctor: (doctorId: string) => void;
}) {
  const { t } = useTranslation();

  // AI sibling unreachable → honest "unavailable" line, never a fabricated band.
  if (!result.available || !result.urgencyBand) {
    return (
      <p className="mt-3 flex items-center gap-1.5 text-[12px] text-muted">
        <AlertTriangle size={13} aria-hidden="true" />
        {t('triage.unavailable')}
      </p>
    );
  }

  return (
    <div className="mt-3 flex flex-col gap-3">
      {/* Urgency banner */}
      <div className={`flex items-center gap-2 rounded-[var(--radius-sm)] border px-3 py-2 ${URGENCY_CLASS[result.urgencyBand]}`}>
        <AlertTriangle size={15} aria-hidden="true" />
        <span className="text-[13px] font-medium">
          {t('triage.urgency')}: {t(`triage.band.${result.urgencyBand}`)}
        </span>
      </div>

      {/* Red flags — empty state shown when none. */}
      <div>
        <h4 className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('triage.redFlags')}</h4>
        {result.redFlags.length === 0 ? (
          <p className="text-[12px] text-muted">{t('triage.noRedFlags')}</p>
        ) : (
          <ul className="flex flex-col gap-1">
            {result.redFlags.map((flag) => (
              <li key={flag} className="flex items-start gap-1.5 text-[12px] text-danger">
                <AlertTriangle size={12} aria-hidden="true" className="mt-0.5 shrink-0" />
                <span className={flag.match(/[ऀ-ॿ]/) ? 'deva' : ''}>{flag}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      {/* Symptoms — only when present. */}
      {result.symptoms.length > 0 ? (
        <div>
          <h4 className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('triage.symptoms')}</h4>
          <div className="flex flex-wrap gap-1.5">
            {result.symptoms.map((s) => (
              <span key={s} className="rounded-full border border-line bg-surface-sunk px-2 py-0.5 text-[12px] text-ink">
                {s}
              </span>
            ))}
          </div>
        </div>
      ) : null}

      {/* Suggested doctors — empty state shown when none. */}
      <div>
        <h4 className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('triage.suggestedDoctors')}</h4>
        {result.suggestedDoctors.length === 0 ? (
          <p className="text-[12px] text-muted">{t('triage.noDoctors')}</p>
        ) : (
          <div className="flex flex-wrap gap-2">
            {result.suggestedDoctors.map((d) => (
              <SuggestedDoctorChip key={d.doctorId} doctor={d} onPick={() => onPickDoctor(d.doctorId)} />
            ))}
          </div>
        )}
      </div>

      <p className="text-[11px] text-muted">{t('triage.advisoryNote')}</p>
    </div>
  );
}

function SuggestedDoctorChip({ doctor, onPick }: { doctor: SuggestedDoctor; onPick: () => void }) {
  const { t } = useTranslation();
  const meta = [
    doctor.specialization,
    doctor.consultationFee !== null ? inr(doctor.consultationFee) : null,
    doctor.nextAvailableSlot ? t('triage.next', { time: istSlot(doctor.nextAvailableSlot) }) : null,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <button
      type="button"
      onClick={onPick}
      className="flex items-center gap-2 rounded-[var(--radius-sm)] border border-line px-2.5 py-1.5 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
    >
      <span className="text-primary" aria-hidden="true">
        <Stethoscope size={14} />
      </span>
      <span className="min-w-0">
        <span className="block text-[13px] font-medium text-ink">{doctor.fullName}</span>
        {meta ? <span className="block text-[11px] text-muted">{meta}</span> : null}
      </span>
    </button>
  );
}
