// Create-appointment slide-over — full stepper (image copy 5/6/7/8).
//   Patient → Slot → Confirm
// Patient step: react-hook-form + zod (custom resolver, no extra dep).
// Slot step: department chips → practitioner list (fee + next) → IST slot grid.
// Confirm step: booking summary + WhatsApp confirmation toggle → create.
//
// The final create runs through useCreateBooking → idempotencyKey() on the POST
// (no double-booking on retry). Footer Back/Continue/Confirm reflects the step.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { inr, istSlot } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { DEPARTMENTS } from '@/lib/data';
import { Stepper } from './Stepper';
import { patientStepSchema, type PatientStep } from '../schema';
import { usePractitioners, useSlots, useCreateBooking } from '../api';
import { toUserError } from '@/lib/backend';
import type { Practitioner, Slot } from '@/lib/mock/contracts';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';

type Step = 0 | 1 | 2;

export function NewBookingPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const [step, setStep] = useState<Step>(0);
  const [deptId, setDeptId] = useState<string>(DEPARTMENTS[0].id);
  const [doctor, setDoctor] = useState<Practitioner | null>(null);
  // The full Slot is held (not just the time) so the live create can send the
  // chosen slot's GUID (slotId). In mock mode slotId is undefined and we fall back
  // to the time string (the mock create ignores the draft).
  const [slot, setSlot] = useState<Slot | null>(null);
  const [sendWhatsapp, setSendWhatsapp] = useState(true);
  const create = useCreateBooking();

  const form = useForm<PatientStep>({
    defaultValues: { phone: '', name: '', age: '', sex: 'F', lang: 'en', reason: '' },
    // Dependency-free zod resolver.
    resolver: async (values) => {
      const parsed = patientStepSchema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const issue of parsed.error.issues) {
        const key = String(issue.path[0]);
        if (!errors[key]) errors[key] = { type: 'zod', message: issue.message };
      }
      return { values: {}, errors };
    },
  });

  const goPatientNext = form.handleSubmit(() => setStep(1));

  const onConfirm = async () => {
    if (!doctor || !slot) return;
    const patient = form.getValues();
    try {
      const result = await create.mutateAsync({
        ...patient,
        doctorId: doctor.id,
        // Live: the slot GUID is required by POST /bookings. Mock: no slotId, so
        // the time string flows through (the mock create ignores the draft).
        slot: slot.slotId ?? slot.time,
        // Stable key per confirm — a retried create maps to the same key (no double-book).
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('newBooking.created', { token: result.token }));
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const footer = (
    <>
      {step > 0 ? (
        <Button variant="ghost" size="md" type="button" onClick={() => setStep((s) => (s - 1) as Step)}>
          {t('common.back')}
        </Button>
      ) : (
        <Button variant="ghost" size="md" type="button" onClick={onClose}>
          {t('common.cancel')}
        </Button>
      )}
      {step === 0 ? (
        <Button variant="primary" size="md" type="button" onClick={() => void goPatientNext()}>
          {t('common.continue')}
        </Button>
      ) : step === 1 ? (
        <Button variant="primary" size="md" type="button" disabled={!doctor || !slot} onClick={() => setStep(2)}>
          {t('common.continue')}
        </Button>
      ) : (
        <Button variant="primary" size="md" type="button" disabled={create.isPending} onClick={() => void onConfirm()}>
          {t('newBooking.confirmBooking')}
        </Button>
      )}
    </>
  );

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('newBooking.eyebrow')}
      title={t('newBooking.title')}
      headerExtra={
        <Stepper
          active={step}
          steps={[
            { label: t('newBooking.stepPatient') },
            { label: t('newBooking.stepSlot') },
            { label: t('newBooking.stepConfirm') },
          ]}
        />
      }
      footer={footer}
    >
      {step === 0 ? (
        <PatientStepBody form={form} />
      ) : step === 1 ? (
        <SlotStepBody
          deptId={deptId}
          onDept={(id) => {
            setDeptId(id);
            setDoctor(null);
            setSlot(null);
          }}
          doctor={doctor}
          onDoctor={(d) => {
            setDoctor(d);
            setSlot(null);
          }}
          slot={slot}
          onSlot={setSlot}
        />
      ) : (
        <ConfirmStepBody
          patient={form.getValues()}
          doctor={doctor}
          slot={slot}
          sendWhatsapp={sendWhatsapp}
          onToggleWhatsapp={setSendWhatsapp}
        />
      )}
    </SlideOver>
  );
}

// ── Step 1: Patient ──────────────────────────────────────────────────────────
function PatientStepBody({ form }: { form: ReturnType<typeof useForm<PatientStep>> }) {
  const { t } = useTranslation();
  const { register, formState, watch, setValue } = form;
  const errKey = (k: keyof PatientStep) => {
    const m = formState.errors[k]?.message;
    return m ? t(`newBooking.validation.${m}`) : undefined;
  };
  const sex = watch('sex');
  const lang = watch('lang');

  return (
    <form className="flex flex-col gap-4" onSubmit={(e) => e.preventDefault()}>
      <FieldShell label={t('newBooking.phoneNumber')} htmlFor="nb-phone" error={errKey('phone')}>
        <TextInput id="nb-phone" type="tel" inputMode="tel" autoFocus placeholder="+91 98765 43210" className="mono" {...register('phone')} aria-invalid={Boolean(formState.errors.phone)} />
        <p className="mt-1 text-[12px] text-muted">{t('newBooking.phoneHint')}</p>
      </FieldShell>

      <FieldShell label={t('newBooking.patientName')} htmlFor="nb-name" error={errKey('name')}>
        <TextInput id="nb-name" type="text" placeholder={t('newBooking.patientNamePlaceholder')} {...register('name')} aria-invalid={Boolean(formState.errors.name)} />
      </FieldShell>

      <div className="grid grid-cols-[80px_1fr] gap-3">
        <FieldShell label={t('newBooking.age')} htmlFor="nb-age">
          <TextInput id="nb-age" type="number" min={0} max={120} className="mono" {...register('age')} />
        </FieldShell>
        <div>
          <span className={labelClass}>{t('newBooking.sex')}</span>
          <div role="radiogroup" aria-label={t('newBooking.sex')} className="flex gap-2">
            <Segmented checked={sex === 'F'} onClick={() => setValue('sex', 'F')} label={t('newBooking.sexFemale')} />
            <Segmented checked={sex === 'M'} onClick={() => setValue('sex', 'M')} label={t('newBooking.sexMale')} />
            <Segmented checked={sex === 'O'} onClick={() => setValue('sex', 'O')} label={t('newBooking.sexOther')} />
          </div>
        </div>
      </div>

      <div>
        <span className={labelClass}>{t('newBooking.language')}</span>
        <div role="radiogroup" aria-label={t('newBooking.language')} className="flex gap-2">
          <Segmented checked={lang === 'en'} onClick={() => setValue('lang', 'en')} label="EN" />
          <Segmented checked={lang === 'hi'} onClick={() => setValue('lang', 'hi')} label="हिंदी" deva />
        </div>
      </div>

      <FieldShell label={t('newBooking.reason')} htmlFor="nb-reason" optional={t('common.optional')}>
        <TextArea id="nb-reason" rows={3} placeholder={t('newBooking.reasonPlaceholder')} {...register('reason')} />
      </FieldShell>
    </form>
  );
}

// ── Step 2: Slot ─────────────────────────────────────────────────────────────
function SlotStepBody({
  deptId,
  onDept,
  doctor,
  onDoctor,
  slot,
  onSlot,
}: {
  deptId: string;
  onDept: (id: string) => void;
  doctor: Practitioner | null;
  onDoctor: (d: Practitioner) => void;
  slot: Slot | null;
  onSlot: (s: Slot) => void;
}) {
  const { t } = useTranslation();
  const { data: practitioners, isLoading: pLoading, isError: pError, refetch: pRefetch } = usePractitioners(deptId);
  const { data: slots, isLoading: sLoading, isError: sError, refetch: sRefetch } = useSlots(doctor?.id);
  const dept = DEPARTMENTS.find((d) => d.id === deptId);

  return (
    <div className="flex flex-col gap-5">
      <section>
        <span className={labelClass}>{t('newBooking.department')}</span>
        <div className="flex flex-wrap gap-2">
          {DEPARTMENTS.map((d) => (
            <button
              key={d.id}
              type="button"
              onClick={() => onDept(d.id)}
              aria-pressed={deptId === d.id}
              className={[
                'rounded-full border px-3 py-1.5 text-[13px] transition-colors',
                deptId === d.id ? 'border-primary bg-primary text-bg' : 'border-line bg-surface text-ink hover:bg-surface-sunk',
              ].join(' ')}
            >
              {d.name}
            </button>
          ))}
        </div>
      </section>

      <section>
        <div className="mb-2 flex items-center justify-between">
          <span className={labelClass}>{t('newBooking.practitioner')}</span>
          {practitioners ? (
            <span className="text-[11px] text-muted-2">
              {t('newBooking.practitionerCount', { count: practitioners.length, dept: dept?.name })}
            </span>
          ) : null}
        </div>
        {pError ? (
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void pRefetch()}
          />
        ) : pLoading || !practitioners ? (
          <div className="flex flex-col gap-2" role="status" aria-busy="true">
            <Skeleton className="h-14 w-full" />
            <Skeleton className="h-14 w-full" />
          </div>
        ) : practitioners.length === 0 ? (
          <p className="text-[12px] text-muted">{t('newBooking.noPractitioners')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {practitioners.map((p) => (
              <li key={p.id}>
                <button
                  type="button"
                  onClick={() => onDoctor(p)}
                  aria-pressed={doctor?.id === p.id}
                  className={[
                    'flex w-full items-center gap-3 rounded-[var(--radius-sm)] border px-3 py-2.5 text-left transition-colors',
                    doctor?.id === p.id ? 'border-primary bg-primary-soft' : 'border-line hover:bg-surface-sunk',
                  ].join(' ')}
                >
                  <span className="flex h-9 w-9 items-center justify-center rounded-full bg-surface-sunk text-[12px] font-semibold text-ink">
                    {p.initials}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[13px] font-medium text-ink">{p.name}</span>
                    <span className="block text-[11px] text-muted">
                      {p.spec} · {t('newBooking.room')} {p.room}
                    </span>
                  </span>
                  <span className="shrink-0 text-right">
                    <span className="mono block text-[13px] text-ink">{inr(p.fee)}</span>
                    <span className="block text-[11px] text-muted">{t('newBooking.nextAvailable', { time: istSlot(p.next) })}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      {doctor ? (
        <section>
          <span className={labelClass}>{t('newBooking.availableSlots')}</span>
          {sError ? (
            <EmptyState
              title={t('error.genericTitle')}
              description={t('error.genericBody')}
              actionLabel={t('common.retry')}
              onAction={() => void sRefetch()}
            />
          ) : sLoading || !slots ? (
            <div className="grid grid-cols-4 gap-2" role="status" aria-busy="true">
              {Array.from({ length: 8 }).map((_, i) => (
                <Skeleton key={i} className="h-12 w-full" />
              ))}
            </div>
          ) : slots.length === 0 ? (
            <p className="text-[12px] text-muted">{t('newBooking.noSlots')}</p>
          ) : (
            <SlotGrid slots={slots} selected={slot} onSelect={onSlot} />
          )}
        </section>
      ) : (
        <p className="text-[12px] text-muted">{t('newBooking.pickSlotHint')}</p>
      )}
    </div>
  );
}

function SlotGrid({ slots, selected, onSelect }: { slots: Slot[]; selected: Slot | null; onSelect: (s: Slot) => void }) {
  return (
    <div className="grid grid-cols-4 gap-2">
      {slots.map((s) => {
        const disabled = s.state === 'full' || s.state === 'blocked';
        const active = selected ? selected.time === s.time && selected.slotId === s.slotId : false;
        return (
          <button
            key={s.slotId ?? s.time}
            type="button"
            disabled={disabled}
            onClick={() => onSelect(s)}
            aria-pressed={active}
            className={[
              'mono rounded-[var(--radius-sm)] border px-1 py-2 text-[12px] transition-colors',
              active
                ? 'border-primary bg-primary text-bg'
                : disabled
                  ? 'cursor-not-allowed border-line bg-surface-sunk text-muted-2 line-through'
                  : 'border-line bg-surface text-ink hover:bg-surface-sunk',
            ].join(' ')}
          >
            {s.time}
          </button>
        );
      })}
    </div>
  );
}

// ── Step 3: Confirm ──────────────────────────────────────────────────────────
function ConfirmStepBody({
  patient,
  doctor,
  slot,
  sendWhatsapp,
  onToggleWhatsapp,
}: {
  patient: PatientStep;
  doctor: Practitioner | null;
  slot: Slot | null;
  sendWhatsapp: boolean;
  onToggleWhatsapp: (v: boolean) => void;
}) {
  const { t } = useTranslation();
  const rows: { label: string; value: string; mono?: boolean }[] = [
    { label: t('newBooking.department'), value: doctor?.spec ?? '—' },
    { label: t('newBooking.practitioner'), value: doctor?.name ?? '—' },
    { label: t('newBooking.stepSlot'), value: slot ? `${t('common.today')} ${istSlot(slot.time)}` : '—', mono: true },
    { label: t('newBooking.fee'), value: doctor ? inr(doctor.fee) : '—', mono: true },
    { label: t('newBooking.room'), value: doctor?.room ?? '—', mono: true },
  ];

  return (
    <div className="flex flex-col gap-4">
      <section className="rounded-[var(--radius)] border border-line p-3">
        <div className="mb-2 flex items-center gap-3 border-b border-line pb-2">
          <span className="flex h-9 w-9 items-center justify-center rounded-full bg-primary-soft text-[13px] font-semibold text-primary">
            {patient.name ? patient.name.slice(0, 2).toUpperCase() : '··'}
          </span>
          <div>
            <p className="text-[13px] font-medium text-ink">{patient.name || '—'}</p>
            <p className="mono text-[11px] text-muted">{patient.phone}</p>
          </div>
        </div>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-3">
          {rows.map((r) => (
            <div key={r.label}>
              <dt className="text-[11px] uppercase tracking-wider text-muted-2">{r.label}</dt>
              <dd className={`mt-0.5 text-[13px] text-ink ${r.mono ? 'mono' : ''}`}>{r.value}</dd>
            </div>
          ))}
          {patient.reason ? (
            <div className="col-span-2">
              <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('newBooking.note')}</dt>
              <dd className={`mt-0.5 text-[13px] text-ink ${patient.reason.match(/[ऀ-ॿ]/) ? 'deva' : ''}`}>{patient.reason}</dd>
            </div>
          ) : null}
        </dl>
      </section>

      <label className="flex items-center gap-3 rounded-[var(--radius-sm)] bg-whatsapp-soft px-3 py-2.5">
        <input
          type="checkbox"
          checked={sendWhatsapp}
          onChange={(e) => onToggleWhatsapp(e.target.checked)}
          className="h-4 w-4 accent-[var(--whatsapp)]"
        />
        <span>
          <span className="block text-[13px] font-medium text-whatsapp-ink">{t('newBooking.sendWhatsappConfirmation')}</span>
          <span className="block text-[11px] text-whatsapp-ink/80">{t('newBooking.sendWhatsappHint')}</span>
        </span>
      </label>

      <p className="text-[12px] text-muted">{t('newBooking.tokenReserved', { token: '' })}</p>
    </div>
  );
}

function Segmented({ checked, onClick, label, deva }: { checked: boolean; onClick: () => void; label: string; deva?: boolean }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={checked}
      onClick={onClick}
      className={[
        'flex-1 rounded-[var(--radius-sm)] border px-2 py-2 text-[13px] transition-colors',
        checked ? 'border-primary bg-primary-soft text-primary' : 'border-line text-ink hover:bg-surface-sunk',
        deva ? 'deva' : '',
      ].join(' ')}
    >
      {label}
    </button>
  );
}
