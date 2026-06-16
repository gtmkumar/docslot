// Invite/Add user slide-over. email + full name + optional phone + initial role.
// react-hook-form + zod (dependency-free resolver). POST carries a stable
// Idempotency-Key. Gated upstream by tenant.users.create.

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useCreateUser, useRoles } from '../api';

const schema = z.object({
  email: z.string().trim().email('email'),
  fullName: z.string().trim().min(1, 'name'),
  phone: z.string().trim().optional().default(''),
  initialRoleId: z.string().optional().default(''),
});
type InviteForm = z.infer<typeof schema>;

export function InviteUserPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: roles } = useRoles();
  const createUser = useCreateUser();

  const { register, handleSubmit, formState } = useForm<InviteForm>({
    defaultValues: { email: '', fullName: '', phone: '', initialRoleId: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    await createUser.mutateAsync({
      email: values.email,
      fullName: values.fullName,
      phone: values.phone || null,
      preferredLanguage: 'en',
      initialRoleId: values.initialRoleId || null,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('team.invite.sent'));
    onClose();
  });

  const errKey = (k: keyof InviteForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`team.validation.${m}`) : undefined;
  };

  const formId = 'invite-user-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.invite.eyebrow')}
      title={t('team.invite.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={createUser.isPending}>
            {t('team.invite.send')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('team.invite.email')} htmlFor="iu-email" error={errKey('email')}>
          <TextInput id="iu-email" type="email" autoFocus placeholder="you@clinic.in" {...register('email')} aria-invalid={Boolean(formState.errors.email)} />
        </FieldShell>

        <FieldShell label={t('team.invite.fullName')} htmlFor="iu-name" error={errKey('fullName')}>
          <TextInput id="iu-name" placeholder={t('team.invite.fullNamePlaceholder')} {...register('fullName')} aria-invalid={Boolean(formState.errors.fullName)} />
        </FieldShell>

        <FieldShell label={t('team.invite.phone')} htmlFor="iu-phone" optional={t('team.invite.phoneOptional')}>
          <TextInput id="iu-phone" type="tel" inputMode="tel" className="mono" placeholder="+91 98765 43210" {...register('phone')} />
        </FieldShell>

        <div>
          <label htmlFor="iu-role" className={labelClass}>
            {t('team.invite.initialRole')}
          </label>
          <Select id="iu-role" {...register('initialRoleId')}>
            <option value="">{t('team.invite.noRole')}</option>
            {roles?.map((r) => (
              <option key={r.roleId} value={r.roleId}>
                {r.name}
              </option>
            ))}
          </Select>
        </div>
      </form>
    </SlideOver>
  );
}
