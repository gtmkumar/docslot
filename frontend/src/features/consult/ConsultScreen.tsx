// Consultation composer (/consult/$bookingId) — the doctor prescription-writing
// experience. A deliberate FULL-SCREEN two-pane focus route (exception to the
// slide-over-primary rule; the two-pane live preview needs the width). Left pane:
// quick-start templates → patient history rail → safety strip → vitals → diagnosis
// → medications → investigations → advice → follow-up. Right pane: a live Rx
// preview (letterhead) + Print / Save PDF / Finalize & send.
//
// PHI: purpose-of-use gated on entry (one declaration covers the draft + the
// history-rail reads). The route param is a booking id (not PHI); the declared
// purpose + clinical content are NEVER URL-encoded and reset on navigation.
// Autosave (debounced PATCH) means intake is never lost. Finalize is the doctor's
// signing act — blocked by unoverridden high/critical drug alerts until a reason is
// given. All states (loading / error / empty / finalized) are implemented.

import { useActionState, useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle,
  ArrowLeft,
  Check,
  CircleCheck,
  FileText,
  Loader2,
  Lock,
  Printer,
  Send,
} from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { PurposeBanner, PurposeGate } from '@/components/ui/PurposeGate';
import { idempotencyKey } from '@/lib/api-client';
import { shortDate } from '@/lib/format';
import { DOCTORS } from '@/lib/data';
import { usePermissions } from '@/lib/permissions';
import { useSession } from '@/stores/session';
import type { DrugAlert, PurposeOfUse, RxMedication, StructuredMedication } from '@/lib/mock/contracts';
import { useConsultation, useConsultBooking, useFinalizeConsultation, useSaveConsultation } from './api';
import { FORMULARY, TEMPLATES, fromFormulary, type QuickTemplate } from './constants';
import { EMPTY_FORM, draftToForm, formToSave, toStructured, type ConsultForm } from './model';
import { VitalsRow } from './components/VitalsRow';
import { ChipTypeahead } from './components/ChipTypeahead';
import { MedicationsEditor } from './components/MedicationsEditor';
import { AdvicePicker } from './components/AdvicePicker';
import { FollowUpPicker } from './components/FollowUpPicker';
import { SafetyStrip } from './components/SafetyStrip';
import { HistoryRail } from './components/HistoryRail';
import { RxPreview } from './components/RxPreview';
import { DIAGNOSES, INVESTIGATIONS } from './constants';

const mergeUnique = (cur: string[], add: string[]) => Array.from(new Set([...cur, ...add]));

/** Append incoming meds, skipping any whose name (case-insensitive) is already in
 *  the draft — so applying a template / repeating a past Rx never creates duplicate
 *  medication lines. */
function mergeMeds(cur: StructuredMedication[], add: StructuredMedication[]): StructuredMedication[] {
  const have = new Set(cur.map((m) => m.name.trim().toLowerCase()));
  const next = [...cur];
  for (const m of add) {
    const key = m.name.trim().toLowerCase();
    if (key && !have.has(key)) {
      have.add(key);
      next.push(m);
    }
  }
  return next;
}

interface FinalizeState {
  phase: 'idle' | 'blocked' | 'done';
  alerts: DrugAlert[];
  prescriptionNumber: string | null;
}

export function ConsultScreen() {
  const { t } = useTranslation();
  const { can, isLoading: permsLoading } = usePermissions();
  const { bookingId } = useParams({ from: '/authed/consult/$bookingId' });
  const [purpose, setPurpose] = useState<PurposeOfUse | null>(null);

  // Phase A gates the whole composer on prescription.create (Phase B adds a
  // draft-only intake mode). Never a role check — the resolved permission set.
  const canCreate = can('docslot.prescription.create');

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <Link to="/bookings" className="inline-flex w-fit items-center gap-1 text-[13px] font-medium text-primary hover:underline">
        <ArrowLeft size={14} aria-hidden="true" />
        {t('consult.backToQueue')}
      </Link>

      <div>
        <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('consult.eyebrow')}</p>
        <h1 id="screen-heading" tabIndex={-1} className="mt-1 text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('consult.title')}
        </h1>
      </div>

      {permsLoading ? (
        <Skeleton className="h-40 w-full rounded-[var(--radius)]" />
      ) : !canCreate ? (
        <Card className="p-6">
          <div className="flex items-start gap-3">
            <span aria-hidden="true" className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-surface-sunk text-muted">
              <Lock size={20} />
            </span>
            <div>
              <h2 className="text-base font-semibold text-ink">{t('consult.noPermission.title')}</h2>
              <p className="mt-1 text-[13px] text-muted">{t('consult.noPermission.body')}</p>
            </div>
          </div>
        </Card>
      ) : purpose === null ? (
        <PurposeGate onDeclare={setPurpose} />
      ) : (
        <>
          <PurposeBanner purpose={purpose} onChange={() => setPurpose(null)} />
          <Composer bookingId={bookingId} purpose={purpose} />
        </>
      )}
    </section>
  );
}

function Composer({ bookingId, purpose }: { bookingId: string; purpose: PurposeOfUse }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const draftQ = useConsultation(bookingId, purpose);
  const bookingQ = useConsultBooking(bookingId);
  const user = useSession((s) => s.user);
  const activeTenant = useSession((s) => s.activeTenant());

  const consultationId = draftQ.data?.consultationId;
  const patientId = draftQ.data?.patientId;
  const save = useSaveConsultation(consultationId);
  const finalize = useFinalizeConsultation(consultationId);

  const [form, setForm] = useState<ConsultForm>(EMPTY_FORM);
  const hydratedRef = useRef(false);
  const lastSavedSig = useRef('');
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'saved' | 'error'>('idle');
  // Repeat-into-Rx feedback: scroll to + briefly ring the medications section.
  const medsRef = useRef<HTMLDivElement>(null);
  const [flashMeds, setFlashMeds] = useState(false);

  // Hydrate the form ONCE from the loaded draft (subsequent edits are the source of
  // truth; autosave owns writes back). Guard the initial save so hydration itself
  // doesn't trigger a PATCH.
  useEffect(() => {
    if (draftQ.data && !hydratedRef.current) {
      const f = draftToForm(draftQ.data);
      setForm(f);
      lastSavedSig.current = JSON.stringify(formToSave(f));
      hydratedRef.current = true;
    }
  }, [draftQ.data]);

  const loadedFinalized = draftQ.data?.status === 'finalized';

  // Debounced autosave. Skips until hydrated, when nothing changed, and once
  // finalized (a signed Rx is immutable here). Keep the latest payload in a ref so
  // finalize can flush it first.
  const payload = formToSave(form);
  const sig = JSON.stringify(payload);
  const payloadRef = useRef(payload);
  payloadRef.current = payload;

  const [finState, finalizeAction, finalizing] = useActionState(
    async (_prev: FinalizeState, formData: FormData): Promise<FinalizeState> => {
      const reason = ((formData.get('overrideReason') as string | null) ?? '').trim() || null;
      // Flush the latest edits so the server signs the current draft.
      try {
        await save.mutateAsync(payloadRef.current);
        lastSavedSig.current = JSON.stringify(payloadRef.current);
        setSaveState('saved');
      } catch {
        setSaveState('error');
      }
      const res = await finalize.mutateAsync({ overrideReason: reason, idempotencyKey: idempotencyKey() });
      if (res.finalized) {
        if (patientId) {
          void qc.invalidateQueries({ queryKey: ['clinical', 'prescriptions', patientId] });
          // The just-signed Rx is a new timeline item — refresh the patient's
          // unified timeline so it appears without a remount.
          void qc.invalidateQueries({ queryKey: ['clinical', 'timeline', patientId] });
        }
        return { phase: 'done', alerts: [], prescriptionNumber: res.prescriptionNumber };
      }
      return { phase: 'blocked', alerts: res.alerts, prescriptionNumber: null };
    },
    { phase: 'idle', alerts: [], prescriptionNumber: null } as FinalizeState,
  );

  const finalized = finState.phase === 'done' || loadedFinalized;
  const readOnly = finalized;

  useEffect(() => {
    if (!hydratedRef.current || !consultationId || readOnly) return;
    if (sig === lastSavedSig.current) return;
    setSaveState('saving');
    const id = window.setTimeout(() => {
      save.mutate(payloadRef.current, {
        onSuccess: () => {
          lastSavedSig.current = sig;
          setSaveState('saved');
        },
        onError: () => setSaveState('error'),
      });
    }, 800);
    return () => window.clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig, consultationId, readOnly]);

  // ── loading / error ──────────────────────────────────────────────────────────
  if (draftQ.isError) {
    return (
      <Card>
        <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void draftQ.refetch()} />
      </Card>
    );
  }
  if (draftQ.isLoading || !draftQ.data) {
    return <ComposerSkeleton />;
  }

  const patientName = draftQ.data.patientName;
  const b = bookingQ.data;
  const genderLabel = b?.gender ? t(`consult.gender.${b.gender}`) : '';
  const patientMeta = [b?.age ? t('consult.yrs', { count: b.age }) : null, genderLabel, b?.phone].filter(Boolean).join(' · ');
  const doctorName = b?.doctorName ?? user?.fullName ?? t('consult.doctorFallback');
  const doctorQual = DOCTORS.find((d) => d.name === b?.doctorName)?.qual ?? null;
  const clinicName = activeTenant?.displayName ?? 'DocSlot';

  const applyTemplate = (tpl: QuickTemplate) => {
    setForm((f) => ({
      ...f,
      diagnoses: mergeUnique(f.diagnoses, tpl.diagnoses),
      medications: mergeMeds(f.medications, tpl.medItemIds.map((id) => FORMULARY.find((x) => x.id === id)).filter(Boolean).map((x) => fromFormulary(x!))),
      investigations: mergeUnique(f.investigations, tpl.investigations ?? []),
      adviceChips: mergeUnique(f.adviceChips, tpl.advice),
      followUpInDays: tpl.followUpInDays,
    }));
    toast.success(t('consult.template.applied', { name: t(tpl.labelKey) }));
  };

  const repeatFromHistory = (diagnosis: string | null, meds: RxMedication[]) => {
    if (meds.length === 0) return; // nothing to copy (button is also disabled)
    setForm((f) => ({
      ...f,
      diagnoses: diagnosis ? mergeUnique(f.diagnoses, diagnosis.split(',').map((s) => s.trim()).filter(Boolean)) : f.diagnoses,
      medications: mergeMeds(f.medications, meds.map(toStructured)),
    }));
    // The setForm above changes the autosave signature → the debounced PATCH fires.
    toast.success(t('consult.history.repeated'));
    // Feedback: bring the medications section into view + briefly highlight it.
    setFlashMeds(true);
    window.setTimeout(() => setFlashMeds(false), 1200);
    requestAnimationFrame(() => medsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
  };

  const hasContent = form.diagnoses.length > 0 || form.medications.length > 0;

  return (
    <div className="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(340px,420px)]">
      {/* ── LEFT: edit pane ── */}
      <div className="flex min-w-0 flex-col gap-4">
        {/* Patient header + quick-start */}
        <Card className="flex flex-col gap-3 p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="min-w-0">
              <p className={`text-base font-semibold text-ink ${/[ऀ-ॿ]/.test(patientName) ? 'deva' : ''}`}>{patientName}</p>
              <p className="mono text-[12px] text-muted">{patientMeta || '—'}</p>
            </div>
            {b?.time ? (
              <span className="mono inline-flex items-center rounded-full bg-surface-sunk px-2.5 py-1 text-[12px] text-muted">
                {b.time}
              </span>
            ) : null}
          </div>
          {!readOnly ? (
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-[12px] font-medium text-muted-2">{t('consult.quickStart')}</span>
              {TEMPLATES.map((tpl) => (
                <button
                  key={tpl.key}
                  type="button"
                  onClick={() => applyTemplate(tpl)}
                  className="inline-flex items-center gap-1.5 rounded-full border border-line bg-surface px-3 py-1.5 text-[12px] font-medium text-ink transition-colors hover:border-primary-soft hover:bg-primary-soft focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                >
                  <span aria-hidden="true">{tpl.emoji}</span>
                  {t(tpl.labelKey)}
                </button>
              ))}
            </div>
          ) : null}
        </Card>

        {/* Safety strip — allergies + chronic conditions (always visible) */}
        {patientId ? <SafetyStrip patientId={patientId} purpose={purpose} /> : null}

        {/* History rail — past visits + labs */}
        {patientId ? (
          <Card className="p-4">
            <HistoryRail patientId={patientId} purpose={purpose} onRepeat={repeatFromHistory} excludeId={draftQ.data.consultationId} />
          </Card>
        ) : null}

        <Section title={t('consult.section.vitals')}>
          <VitalsRow value={form.vitals} onChange={(patch) => setForm((f) => ({ ...f, vitals: { ...f.vitals, ...patch } }))} disabled={readOnly} />
        </Section>

        <Section title={t('consult.section.diagnosis')}>
          <ChipTypeahead
            options={DIAGNOSES}
            value={form.diagnoses}
            onChange={(v) => setForm((f) => ({ ...f, diagnoses: v }))}
            placeholder={t('consult.diagnosisPlaceholder')}
            tone="accent"
            disabled={readOnly}
          />
        </Section>

        <div ref={medsRef} className={`rounded-[var(--radius)] transition-shadow duration-[var(--dur-base)] ${flashMeds ? 'ring-2 ring-primary' : ''}`}>
          <Section title={t('consult.section.medications')} count={form.medications.length}>
            <MedicationsEditor value={form.medications} onChange={(v) => setForm((f) => ({ ...f, medications: v }))} disabled={readOnly} />
          </Section>
        </div>

        <Section title={t('consult.section.investigations')} subtitle={t('consult.section.investigationsSub')}>
          <ChipTypeahead
            options={INVESTIGATIONS}
            value={form.investigations}
            onChange={(v) => setForm((f) => ({ ...f, investigations: v }))}
            placeholder={t('consult.investigationsPlaceholder')}
            tone="info"
            disabled={readOnly}
          />
        </Section>

        <Section title={t('consult.section.advice')}>
          <AdvicePicker
            chips={form.adviceChips}
            text={form.adviceText}
            onChangeChips={(v) => setForm((f) => ({ ...f, adviceChips: v }))}
            onChangeText={(v) => setForm((f) => ({ ...f, adviceText: v }))}
            disabled={readOnly}
          />
        </Section>

        <Section title={t('consult.section.followUp')}>
          <FollowUpPicker value={form.followUpInDays} onChange={(v) => setForm((f) => ({ ...f, followUpInDays: v }))} disabled={readOnly} />
        </Section>
      </div>

      {/* ── RIGHT: actions + preview ── */}
      <div className="flex flex-col gap-3 lg:sticky lg:top-2 lg:self-start">
        {finalized ? (
          <FinalizedCard prescriptionNumber={finState.prescriptionNumber ?? draftQ.data.prescriptionNumber} onDone={() => navigate({ to: '/bookings' })} />
        ) : (
          <ActionsBar
            saveState={saveState}
            hasContent={hasContent}
            finalizing={finalizing}
            alerts={finState.phase === 'blocked' ? finState.alerts : []}
            finalizeAction={finalizeAction}
          />
        )}

        <RxPreview
          doctorName={doctorName}
          doctorQual={doctorQual}
          clinicName={clinicName}
          clinicLocation={null}
          patientName={patientName}
          patientMeta={patientMeta || '—'}
          dateLabel={shortDate(new Date().toISOString())}
          form={form}
        />
        <p className="flex items-center justify-center gap-1.5 text-center text-[12px] text-muted">
          <Send size={13} className="text-whatsapp" aria-hidden="true" />
          {t('consult.whatsappNote')}
        </p>
      </div>
    </div>
  );
}

// ── Actions bar (Print / Save PDF / Finalize) + inline drug alerts ─────────────
function ActionsBar({
  saveState,
  hasContent,
  finalizing,
  alerts,
  finalizeAction,
}: {
  saveState: 'idle' | 'saving' | 'saved' | 'error';
  hasContent: boolean;
  finalizing: boolean;
  alerts: DrugAlert[];
  finalizeAction: (formData: FormData) => void;
}) {
  const { t } = useTranslation();
  const blocked = alerts.length > 0;
  return (
    <Card className="flex flex-col gap-3 p-3">
      <div className="flex items-center justify-between gap-2">
        <SaveIndicator state={saveState} />
        <div className="flex items-center gap-1.5">
          <Button variant="ghost" size="sm" type="button" onClick={() => window.print()}>
            <Printer size={14} aria-hidden="true" />
            {t('consult.print')}
          </Button>
          <Button variant="ghost" size="sm" type="button" onClick={() => window.print()}>
            <FileText size={14} aria-hidden="true" />
            {t('consult.savePdf')}
          </Button>
        </div>
      </div>

      {blocked ? (
        <div className="flex flex-col gap-2 rounded-[var(--radius)] border border-danger/40 bg-danger-soft p-3">
          <p className="flex items-center gap-1.5 text-[12px] font-semibold text-danger">
            <AlertTriangle size={14} aria-hidden="true" />
            {t('consult.alerts.blockedTitle')}
          </p>
          <ul className="flex flex-col gap-1.5">
            {alerts.map((a) => (
              <li key={a.alertId}>
                <AlertRow alert={a} />
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <form action={finalizeAction} className="flex flex-col gap-2">
        {blocked ? (
          <label className="flex flex-col gap-1">
            <span className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('consult.alerts.overrideReason')}</span>
            <textarea
              name="overrideReason"
              required
              minLength={5}
              rows={2}
              placeholder={t('consult.alerts.overridePlaceholder')}
              className="w-full rounded-[var(--radius-sm)] border border-line bg-surface px-2.5 py-2 text-[13px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
            />
          </label>
        ) : null}
        <Button variant="primary" size="md" type="submit" disabled={!hasContent || finalizing} className="w-full">
          {finalizing ? <Loader2 size={15} className="animate-spin" aria-hidden="true" /> : <Send size={15} aria-hidden="true" />}
          {blocked ? t('consult.alerts.overrideFinalize') : t('consult.finalize')}
        </Button>
      </form>
    </Card>
  );
}

function AlertRow({ alert }: { alert: DrugAlert }) {
  const { t } = useTranslation();
  const tone =
    alert.severity === 'critical' || alert.severity === 'high'
      ? 'bg-danger text-bg'
      : alert.severity === 'moderate'
        ? 'bg-warn-soft text-warn'
        : 'bg-surface-sunk text-muted';
  return (
    <div className="flex items-start gap-2">
      <span className={`mt-0.5 inline-flex shrink-0 items-center rounded-full px-1.5 py-0.5 text-[10px] font-semibold uppercase ${tone}`}>
        {t(`consult.alerts.severity.${alert.severity}`)}
      </span>
      <p className="text-[12px] text-ink">
        <span className="font-semibold">{alert.medicationName}</span> — {alert.description}
      </p>
    </div>
  );
}

function SaveIndicator({ state }: { state: 'idle' | 'saving' | 'saved' | 'error' }) {
  const { t } = useTranslation();
  if (state === 'saving') {
    return (
      <span className="inline-flex items-center gap-1.5 text-[12px] text-muted">
        <Loader2 size={13} className="animate-spin" aria-hidden="true" />
        {t('consult.saving')}
      </span>
    );
  }
  if (state === 'saved') {
    return (
      <span className="inline-flex items-center gap-1.5 text-[12px] text-primary">
        <Check size={13} aria-hidden="true" />
        {t('consult.saved')}
      </span>
    );
  }
  if (state === 'error') {
    return (
      <span className="inline-flex items-center gap-1.5 text-[12px] text-danger">
        <AlertTriangle size={13} aria-hidden="true" />
        {t('consult.saveError')}
      </span>
    );
  }
  return <span className="text-[12px] text-muted-2">{t('consult.autosaveHint')}</span>;
}

function FinalizedCard({ prescriptionNumber, onDone }: { prescriptionNumber: string | null; onDone: () => void }) {
  const { t } = useTranslation();
  return (
    <Card className="flex flex-col items-center gap-3 p-5 text-center">
      <span aria-hidden="true" className="flex h-12 w-12 items-center justify-center rounded-full bg-primary-soft text-primary">
        <CircleCheck size={24} />
      </span>
      <div>
        <h2 className="text-base font-semibold text-ink">{t('consult.finalized.title')}</h2>
        <p className="mt-1 text-[13px] text-muted">{t('consult.finalized.body')}</p>
      </div>
      {prescriptionNumber ? (
        <span className="mono rounded-[var(--radius-sm)] bg-surface-sunk px-3 py-1.5 text-[13px] font-semibold text-ink">{prescriptionNumber}</span>
      ) : null}
      <div className="flex w-full items-center gap-2">
        <Button variant="ghost" size="md" type="button" className="flex-1" onClick={() => window.print()}>
          <Printer size={15} aria-hidden="true" />
          {t('consult.print')}
        </Button>
        <Button variant="primary" size="md" type="button" className="flex-1" onClick={onDone}>
          {t('consult.finalized.done')}
        </Button>
      </div>
    </Card>
  );
}

// ── Section wrapper + skeleton ─────────────────────────────────────────────────
function Section({ title, subtitle, count, children }: { title: string; subtitle?: string; count?: number; children: React.ReactNode }) {
  return (
    <Card className="p-4">
      <div className="mb-3 flex items-baseline justify-between gap-2">
        <h2 className="text-[13px] font-semibold uppercase tracking-wide text-muted">{title}</h2>
        {count != null ? <span className="mono text-[12px] text-muted-2">{count}</span> : subtitle ? <span className="text-[11px] text-muted-2">{subtitle}</span> : null}
      </div>
      {children}
    </Card>
  );
}

function ComposerSkeleton() {
  return (
    <div className="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(340px,420px)]" role="status" aria-busy="true">
      <div className="flex flex-col gap-4">
        <Skeleton className="h-20 w-full rounded-[var(--radius)]" />
        <Skeleton className="h-14 w-full rounded-[var(--radius)]" />
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-28 w-full rounded-[var(--radius)]" />
        ))}
      </div>
      <div className="flex flex-col gap-3">
        <Skeleton className="h-16 w-full rounded-[var(--radius)]" />
        <Skeleton className="h-96 w-full rounded-[var(--radius)]" />
      </div>
    </div>
  );
}
