// Add patient slide-over — functional form shell. Key fields: phone (global
// identity in DocSlot — patients are cross-tenant by phone), name, age,
// preferred language, optional guardian (for minors). Submit toasts and closes.

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useAddPatient } from '../api';

const schema = z.object({
  phone: z
    .string()
    .trim()
    .regex(/^\+?[0-9\s-]{8,16}$/, 'phone'),
  name: z.string().trim().min(1, 'name'),
  age: z.string().trim().optional().default(''),
  lang: z.enum(['en', 'hi']).default('en'),
  guardian: z.string().trim().optional().default(''),
});
type PatientForm = z.infer<typeof schema>;

export function AddPatientPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const addPatient = useAddPatient();
  const { register, handleSubmit, formState } = useForm<PatientForm>({
    defaultValues: { phone: '', name: '', age: '', lang: 'en', guardian: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    try {
      // Stable Idempotency-Key per submit so a retry maps to the same key and the
      // server de-dupes the registration (no duplicate cross-tenant patient).
      await addPatient.mutateAsync({
        phone: values.phone,
        name: values.name,
        age: values.age,
        lang: values.lang,
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('addPatient.saved'));
      onClose();
    } catch (e) {
      // Surface the API's responseMessage (or a generic message) — don't close.
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof PatientForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`newBooking.validation.${m}`) : undefined;
  };

  const formId = 'add-patient-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('addPatient.eyebrow')}
      title={t('addPatient.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={addPatient.isPending}>
            {t('addPatient.save')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('addPatient.phone')} htmlFor="ap-phone" error={errKey('phone')}>
          <TextInput id="ap-phone" type="tel" inputMode="tel" autoFocus placeholder="+91 98765 43210" className="mono" {...register('phone')} aria-invalid={Boolean(formState.errors.phone)} />
        </FieldShell>

        <FieldShell label={t('addPatient.name')} htmlFor="ap-name" error={errKey('name')}>
          <TextInput id="ap-name" placeholder={t('addPatient.namePlaceholder')} {...register('name')} aria-invalid={Boolean(formState.errors.name)} />
        </FieldShell>

        <div className="grid grid-cols-2 gap-3">
          <FieldShell label={t('addPatient.age')} htmlFor="ap-age">
            <TextInput id="ap-age" type="number" min={0} max={120} className="mono" {...register('age')} />
          </FieldShell>
          <div>
            <label htmlFor="ap-lang" className={labelClass}>
              {t('addPatient.language')}
            </label>
            <Select id="ap-lang" {...register('lang')}>
              <option value="en">EN</option>
              <option value="hi">हिंदी</option>
            </Select>
          </div>
        </div>

        <FieldShell label={t('addPatient.guardian')} htmlFor="ap-guardian" optional={t('common.optional')}>
          <TextInput id="ap-guardian" placeholder={t('addPatient.guardianPlaceholder')} {...register('guardian')} />
        </FieldShell>
      </form>
    </SlideOver>
  );
}
