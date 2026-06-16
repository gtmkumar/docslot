// Add doctor slide-over — functional form shell (react-hook-form + zod via a
// dependency-free resolver). Key fields: name, specialization/department,
// qualification, fee, room. Submit toasts and closes (the create mutation lands
// when the doctors backend endpoint exists).

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { DEPARTMENTS } from '@/lib/data';
import { USE_REAL_API, toUserError } from '@/lib/backend';
import { idempotencyKey } from '@/lib/api-client';
import { useAddDoctor } from '../api';

const schema = z.object({
  name: z.string().trim().min(1, 'required'),
  deptId: z.string().min(1),
  qual: z.string().trim().optional().default(''),
  fee: z.string().trim().optional().default(''),
  phone: z.string().trim().optional().default(''),
  room: z.string().trim().optional().default(''),
});
type DoctorForm = z.infer<typeof schema>;

export function AddDoctorPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const addDoctor = useAddDoctor();
  const { register, handleSubmit, formState } = useForm<DoctorForm>({
    defaultValues: { name: '', deptId: DEPARTMENTS[0].id, qual: '', fee: '', phone: '', room: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    // Map the panel fields onto CreateDoctorRequest. The panel's deptId is a mock
    // token (NOT a backend department GUID), so we send departmentId:null and carry
    // the department NAME as specialization — the directory derives the dept tab
    // from the specialization/name, so the new card lands under the right group.
    // The `room` field has no backend column and is dropped.
    const deptName = DEPARTMENTS.find((d) => d.id === values.deptId)?.name ?? null;
    const feeNum = values.fee ? Number.parseInt(values.fee, 10) : NaN;
    try {
      await addDoctor.mutateAsync({
        fullName: values.name,
        departmentId: null,
        specialization: deptName,
        qualifications: values.qual ? [values.qual] : [],
        consultationFee: Number.isFinite(feeNum) ? feeNum : null,
        phone: values.phone || null,
        idempotencyKey: idempotencyKey(),
      });
      // Mock mode keeps the prototype's "Doctor added" copy; live mode shares it
      // (the save is real). USE_REAL_API only differentiates the data path, not UX.
      toast.success(USE_REAL_API ? t('addDoctor.savedLive', { name: values.name }) : t('addDoctor.saved'));
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
  });

  const formId = 'add-doctor-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('addDoctor.eyebrow')}
      title={t('addDoctor.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={addDoctor.isPending}>
            {t('addDoctor.save')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('addDoctor.name')} htmlFor="ad-name" error={formState.errors.name ? t('newBooking.validation.name') : undefined}>
          <TextInput id="ad-name" placeholder={t('addDoctor.namePlaceholder')} autoFocus {...register('name')} aria-invalid={Boolean(formState.errors.name)} />
        </FieldShell>

        <div>
          <label htmlFor="ad-dept" className={labelClass}>
            {t('addDoctor.department')}
          </label>
          <Select id="ad-dept" {...register('deptId')}>
            {DEPARTMENTS.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </Select>
        </div>

        <FieldShell label={t('addDoctor.qualification')} htmlFor="ad-qual">
          <TextInput id="ad-qual" placeholder={t('addDoctor.qualificationPlaceholder')} {...register('qual')} />
        </FieldShell>

        <div className="grid grid-cols-2 gap-3">
          <FieldShell label={t('addDoctor.fee')} htmlFor="ad-fee">
            <TextInput id="ad-fee" type="number" min={0} className="mono" {...register('fee')} />
          </FieldShell>
          <FieldShell label={t('addDoctor.room')} htmlFor="ad-room">
            <TextInput id="ad-room" placeholder={t('addDoctor.roomPlaceholder')} {...register('room')} />
          </FieldShell>
        </div>

        <FieldShell label={t('addDoctor.phone')} htmlFor="ad-phone">
          <TextInput id="ad-phone" type="tel" inputMode="tel" placeholder="+91 98765 43210" className="mono" {...register('phone')} />
        </FieldShell>
      </form>
    </SlideOver>
  );
}
