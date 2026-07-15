// Booking rules section: the 7 tenant-default appointment fields (auto-confirm,
// overbooking, slot duration, cutoff, booking window, reminder lead, no-show grace).
// react-hook-form + zod (the house manual-resolver style). Editing gates on
// tenant.settings.update — without it every control is disabled and the Save bar is
// hidden. Save PATCHes { appointmentSettings } with the FULL object; server 422s
// surface via toUserError. Zero hex — tokens only.

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { CalendarCog, Info } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Toggle } from '@/components/ui/Toggle';
import { Select, TextInput, labelClass } from '@/components/ui/Field';
import { toUserError } from '@/lib/backend';
import type { AppointmentSettings, Settings } from '@/lib/mock/contracts';
import { useUpdateSettings } from '../api';
import { SectionCard } from './SectionCard';

const SLOT_OPTIONS = [5, 10, 15, 20, 30, 45, 60] as const;

// Messages are i18n key SUFFIXES under settings.bookingRules.validation.* — the manual
// resolver maps issue.message → that key (mirrors AddPatientPanel's house pattern).
const schema = z.object({
  slotDurationMinutes: z
    .number({ invalid_type_error: 'slotDuration' })
    .int('slotDuration')
    .refine((n) => (SLOT_OPTIONS as readonly number[]).includes(n), { message: 'slotDuration' }),
  bookingCutoffHours: z.number({ invalid_type_error: 'cutoff' }).int('cutoff').min(0, 'cutoff'),
  autoConfirm: z.boolean(),
  maxAdvanceDays: z.number({ invalid_type_error: 'maxAdvance' }).int('maxAdvance').min(1, 'maxAdvance'),
  allowOverbooking: z.boolean(),
  reminderHoursBefore: z.number({ invalid_type_error: 'reminder' }).int('reminder').min(0, 'reminder'),
  noShowGraceMinutes: z
    .number({ invalid_type_error: 'grace' })
    .int('grace')
    .min(0, 'grace')
    .max(240, 'grace'),
});
type BookingRulesForm = z.infer<typeof schema>;

export function BookingRulesSection({ settings, canUpdate }: { settings: Settings; canUpdate: boolean }) {
  const { t } = useTranslation();
  const update = useUpdateSettings();
  const defaults: BookingRulesForm = settings.appointmentSettings;

  const { register, handleSubmit, watch, setValue, reset, formState } = useForm<BookingRulesForm>({
    defaultValues: defaults,
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const issue of parsed.error.issues) {
        errors[String(issue.path[0])] ??= { type: 'zod', message: issue.message };
      }
      return { values: {}, errors };
    },
  });

  const autoConfirm = watch('autoConfirm');
  const allowOverbooking = watch('allowOverbooking');

  const onSubmit = handleSubmit(async (values) => {
    try {
      const saved = await update.mutateAsync({ appointmentSettings: values as AppointmentSettings });
      // Re-seed from the server's returned section so the form reflects any normalization
      // and the dirty state clears.
      reset(saved.appointmentSettings);
      toast.success(t('settings.bookingRules.saved'));
    } catch (e) {
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof BookingRulesForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`settings.bookingRules.validation.${m}`) : undefined;
  };

  const dirty = formState.isDirty;
  const formId = 'booking-rules-form';

  return (
    <SectionCard
      anchorId="booking-rules"
      icon={<CalendarCog size={16} aria-hidden="true" />}
      title={t('settings.bookingRules.title')}
      caption={t('settings.bookingRules.caption')}
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        {/* Toggles */}
        <ToggleRow
          id="auto-confirm"
          checked={autoConfirm}
          disabled={!canUpdate}
          onChange={(v) => setValue('autoConfirm', v, { shouldDirty: true })}
          title={t('settings.bookingRules.autoConfirm')}
          description={t('settings.bookingRules.autoConfirmSub')}
        />
        <ToggleRow
          id="allow-overbooking"
          checked={allowOverbooking}
          disabled={!canUpdate}
          onChange={(v) => setValue('allowOverbooking', v, { shouldDirty: true })}
          title={t('settings.bookingRules.allowOverbooking')}
          description={t('settings.bookingRules.allowOverbookingSub')}
        />

        {/* Numeric + select fields */}
        <div className="grid gap-4 border-t border-line pt-4 sm:grid-cols-2">
          <div>
            <label htmlFor="slot-duration" className={labelClass}>
              {t('settings.bookingRules.slotDuration')}
            </label>
            <Select
              id="slot-duration"
              disabled={!canUpdate}
              aria-invalid={Boolean(formState.errors.slotDurationMinutes)}
              {...register('slotDurationMinutes', { valueAsNumber: true })}
            >
              {SLOT_OPTIONS.map((n) => (
                <option key={n} value={n}>
                  {t('settings.bookingRules.slotDurationValue', { value: n })}
                </option>
              ))}
            </Select>
            <FieldHint sub={t('settings.bookingRules.slotDurationSub')} error={errKey('slotDurationMinutes')} />
          </div>

          <NumberField
            id="booking-cutoff"
            label={t('settings.bookingRules.cutoff')}
            unit={t('settings.bookingRules.cutoffUnit')}
            sub={t('settings.bookingRules.cutoffSub')}
            min={0}
            disabled={!canUpdate}
            error={errKey('bookingCutoffHours')}
            invalid={Boolean(formState.errors.bookingCutoffHours)}
            register={register('bookingCutoffHours', { valueAsNumber: true })}
          />

          <NumberField
            id="max-advance"
            label={t('settings.bookingRules.maxAdvance')}
            unit={t('settings.bookingRules.maxAdvanceUnit')}
            sub={t('settings.bookingRules.maxAdvanceSub')}
            min={1}
            disabled={!canUpdate}
            error={errKey('maxAdvanceDays')}
            invalid={Boolean(formState.errors.maxAdvanceDays)}
            register={register('maxAdvanceDays', { valueAsNumber: true })}
          />

          <NumberField
            id="reminder-hours"
            label={t('settings.bookingRules.reminder')}
            unit={t('settings.bookingRules.reminderUnit')}
            sub={t('settings.bookingRules.reminderSub')}
            min={0}
            disabled={!canUpdate}
            error={errKey('reminderHoursBefore')}
            invalid={Boolean(formState.errors.reminderHoursBefore)}
            register={register('reminderHoursBefore', { valueAsNumber: true })}
          />

          <NumberField
            id="grace-minutes"
            label={t('settings.bookingRules.grace')}
            unit={t('settings.bookingRules.graceUnit')}
            sub={t('settings.bookingRules.graceSub')}
            min={0}
            max={240}
            disabled={!canUpdate}
            error={errKey('noShowGraceMinutes')}
            invalid={Boolean(formState.errors.noShowGraceMinutes)}
            register={register('noShowGraceMinutes', { valueAsNumber: true })}
          />
        </div>

        {canUpdate && dirty ? (
          <div className="flex items-center justify-between gap-3 border-t border-line pt-4">
            <span className="flex items-center gap-1.5 text-[12px] text-muted">
              <Info size={13} aria-hidden="true" />
              {t('team.security.unsaved')}
            </span>
            <div className="flex shrink-0 gap-2">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={update.isPending}
                onClick={() => reset(defaults)}
              >
                {t('settings.discard')}
              </Button>
              <Button type="submit" form={formId} variant="primary" size="sm" disabled={update.isPending}>
                {t('settings.bookingRules.save')}
              </Button>
            </div>
          </div>
        ) : null}
      </form>
    </SectionCard>
  );
}

function NumberField({
  id,
  label,
  unit,
  sub,
  min,
  max,
  disabled,
  error,
  invalid,
  register,
}: {
  id: string;
  label: string;
  unit: string;
  sub: string;
  min: number;
  max?: number;
  disabled: boolean;
  error?: string;
  invalid: boolean;
  register: ReturnType<ReturnType<typeof useForm<BookingRulesForm>>['register']>;
}) {
  return (
    <div>
      <label htmlFor={id} className={labelClass}>
        {label}
      </label>
      <div className="flex items-center gap-2">
        <TextInput
          id={id}
          type="number"
          min={min}
          max={max}
          disabled={disabled}
          className="mono w-24"
          aria-invalid={invalid}
          {...register}
        />
        <span className="text-[12px] text-muted-2">{unit}</span>
      </div>
      <FieldHint sub={sub} error={error} />
    </div>
  );
}

function FieldHint({ sub, error }: { sub: string; error?: string }) {
  return (
    <p className={`mt-1 text-[11px] ${error ? 'text-danger' : 'text-muted-2'}`} role={error ? 'alert' : undefined}>
      {error ?? sub}
    </p>
  );
}

function ToggleRow({
  id,
  checked,
  disabled,
  onChange,
  title,
  description,
}: {
  id: string;
  checked: boolean;
  disabled: boolean;
  onChange: (next: boolean) => void;
  title: string;
  description: string;
}) {
  const descId = `${id}-desc`;
  return (
    <div className="flex items-start justify-between gap-4">
      <div className="min-w-0">
        <label htmlFor={id} className="block text-[13px] font-medium text-ink">
          {title}
        </label>
        <p id={descId} className="mt-0.5 text-[12px] text-muted">
          {description}
        </p>
      </div>
      <Toggle id={id} checked={checked} disabled={disabled} onChange={onChange} label={title} describedBy={descId} />
    </div>
  );
}
