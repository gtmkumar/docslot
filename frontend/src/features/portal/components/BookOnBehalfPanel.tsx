// Book-on-behalf slide-over (Care Partner self-service). The partner books for a
// referred patient: pick a doctor + slot, enter the patient's details. The result
// is a BEHALF booking that triggers a PATIENT CONSENT OTP — the patient must
// approve via WhatsApp before the booking is confirmed (DPDP). That is surfaced
// prominently in the copy. Gated upstream by commission.broker.create_booking_self.
// POST carries a stable Idempotency-Key. Slot times are explicit Asia/Kolkata.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { MessageCircle } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { istSlot } from '@/lib/format';
import { useCreatePortalBooking, usePortalPractitioners, usePortalSlots } from '../api';
import type { BrokerGender } from '@/lib/mock/contracts';

const GENDERS: BrokerGender[] = ['male', 'female', 'other'];

const schema = z.object({
  patientPhone: z.string().trim().regex(/^\+?[0-9\s-]{8,16}$/, 'phone'),
  patientName: z.string().trim().optional().default(''),
  patientAge: z.string().trim().optional().default(''),
  chiefComplaint: z.string().trim().optional().default(''),
});
type BehalfForm = z.infer<typeof schema>;

const ageNum = (s: string): number | null => {
  const n = Number(s.trim());
  return s.trim() === '' || Number.isNaN(n) ? null : n;
};

export function BookOnBehalfPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreatePortalBooking();
  const practitioners = usePortalPractitioners();

  const [doctorId, setDoctorId] = useState<string>('');
  const [slotKey, setSlotKey] = useState<string>('');
  const [gender, setGender] = useState<'' | BrokerGender>('');
  const slots = usePortalSlots(doctorId || null);

  const { register, handleSubmit, formState } = useForm<BehalfForm>({
    defaultValues: { patientPhone: '', patientName: '', patientAge: '', chiefComplaint: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    if (!doctorId || !slotKey) {
      toast.error(t('portal.behalf.pickSlot'));
      return;
    }
    // try/catch so a server rejection surfaces an error toast (and leaves the panel open to retry)
    // instead of an unhandled rejection with no feedback (#55).
    try {
      const result = await create.mutateAsync({
        patientPhone: values.patientPhone,
        patientName: values.patientName.trim() || null,
        patientAge: ageNum(values.patientAge),
        patientGender: gender || null,
        // Live: the slot GUID; mock: the time string (the mock create ignores it).
        slotId: slotKey,
        doctorId,
        departmentId: null,
        chiefComplaint: values.chiefComplaint.trim() || null,
        idempotencyKey: idempotencyKey(),
      });
      // The status is 'awaiting_patient_consent' — reinforce the OTP step in the toast.
      toast.success(t('portal.behalf.created', { ref: result.bookingNumber ?? '' }));
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof BehalfForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`commission.validation.${m}`) : undefined;
  };

  const formId = 'book-on-behalf-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('portal.behalf.eyebrow')}
      title={t('portal.behalf.title')}
      description={t('portal.behalf.consentNote')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={create.isPending}>
            {t('portal.behalf.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        {/* Consent OTP banner — the patient must approve via WhatsApp. */}
        <div className="flex items-start gap-2 rounded-[var(--radius-sm)] bg-whatsapp-soft px-3 py-2.5 text-[12px] text-whatsapp-ink">
          <MessageCircle size={15} aria-hidden="true" className="mt-0.5 shrink-0" />
          <span>{t('portal.behalf.consentNote')}</span>
        </div>

        {/* Doctor */}
        <div>
          <label htmlFor="bb-doctor" className={labelClass}>
            {t('portal.behalf.doctor')}
          </label>
          {practitioners.isLoading ? (
            <Skeleton className="h-10 w-full" />
          ) : practitioners.isError || !practitioners.data ? (
            <p className="text-[12px] text-danger">{t('error.genericTitle')}</p>
          ) : (
            <Select
              id="bb-doctor"
              value={doctorId}
              onChange={(e) => {
                setDoctorId(e.target.value);
                setSlotKey('');
              }}
            >
              <option value="">{t('portal.behalf.doctorPlaceholder')}</option>
              {practitioners.data.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name} · {p.spec}
                </option>
              ))}
            </Select>
          )}
        </div>

        {/* Slot — depends on the chosen doctor. Times are explicit Asia/Kolkata. */}
        {doctorId ? (
          <div>
            <label htmlFor="bb-slot" className={labelClass}>
              {t('portal.behalf.slot')}
            </label>
            {slots.isLoading ? (
              <Skeleton className="h-10 w-full" />
            ) : slots.isError || !slots.data ? (
              <p className="text-[12px] text-danger">{t('error.genericTitle')}</p>
            ) : slots.data.filter((s) => s.state === 'open' || s.state === 'tight').length === 0 ? (
              <p className="text-[12px] text-muted">{t('portal.behalf.noSlots')}</p>
            ) : (
              <Select id="bb-slot" value={slotKey} onChange={(e) => setSlotKey(e.target.value)}>
                <option value="">{t('portal.behalf.slotPlaceholder')}</option>
                {slots.data
                  .filter((s) => s.state === 'open' || s.state === 'tight')
                  .map((s) => (
                    <option key={s.slotId ?? s.time} value={s.slotId ?? s.time}>
                      {istSlot(s.time)}
                    </option>
                  ))}
              </Select>
            )}
          </div>
        ) : null}

        <FieldShell label={t('portal.behalf.patientPhone')} htmlFor="bb-phone" error={errKey('patientPhone')}>
          <TextInput id="bb-phone" type="tel" inputMode="tel" className="mono" placeholder="+91 98765 43210" {...register('patientPhone')} aria-invalid={Boolean(formState.errors.patientPhone)} />
        </FieldShell>

        <FieldShell label={t('portal.behalf.patientName')} htmlFor="bb-name" optional={t('portal.behalf.optional')}>
          <TextInput id="bb-name" {...register('patientName')} />
        </FieldShell>

        <div className="grid grid-cols-2 gap-3">
          <FieldShell label={t('portal.behalf.patientAge')} htmlFor="bb-age" optional={t('portal.behalf.optional')}>
            <TextInput id="bb-age" type="number" min={0} max={120} className="mono" {...register('patientAge')} />
          </FieldShell>
          <div>
            <label htmlFor="bb-gender" className={labelClass}>
              {t('portal.behalf.patientGender')}
            </label>
            <Select id="bb-gender" value={gender} onChange={(e) => setGender(e.target.value as '' | BrokerGender)}>
              <option value="">{t('portal.behalf.genderPlaceholder')}</option>
              {GENDERS.map((g) => (
                <option key={g} value={g}>
                  {t(`portal.behalf.gender.${g}`)}
                </option>
              ))}
            </Select>
          </div>
        </div>

        <FieldShell label={t('portal.behalf.chiefComplaint')} htmlFor="bb-complaint" optional={t('portal.behalf.optional')}>
          <TextArea id="bb-complaint" rows={3} {...register('chiefComplaint')} />
        </FieldShell>
      </form>
    </SlideOver>
  );
}
