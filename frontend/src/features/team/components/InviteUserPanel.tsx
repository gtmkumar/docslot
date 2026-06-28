// Invite/Add user slide-over. email + full name + optional phone + initial role.
// react-hook-form + zod (dependency-free resolver). POST carries a stable
// Idempotency-Key. Gated upstream by tenant.users.create.
//
// The initial role is REQUIRED: a role-less user gets no tenant membership and so
// never appears in the Users list. We offer only tenant-scoped roles (platform
// roles are a cross-tenant, super-only concern the DB would reject anyway).

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useCreateUser, useRoles } from '../api';

const schema = z.object({
  email: z.string().trim().email('email'),
  fullName: z.string().trim().min(1, 'name'),
  phone: z.string().trim().optional().default(''),
  // Required — a role-less user has no membership and won't surface in the list.
  initialRoleId: z.string().min(1, 'role'),
});
type InviteForm = z.infer<typeof schema>;

export function InviteUserPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: roles } = useRoles();
  const createUser = useCreateUser();

  // Only tenant-scoped roles are assignable from this tenant-admin surface.
  const assignableRoles = roles?.filter((r) => r.scope === 'tenant') ?? [];

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
    try {
      const result = await createUser.mutateAsync({
        email: values.email,
        fullName: values.fullName,
        phone: values.phone || null,
        preferredLanguage: 'en',
        initialRoleId: values.initialRoleId,
        idempotencyKey: idempotencyKey(),
      });
      // Distinguish a fresh invite from a link to an existing global identity.
      toast.success(
        result.alreadyExisted
          ? t('team.toast.linked', { email: values.email })
          : t('team.toast.invited', { email: values.email }),
      );
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
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
          <Select id="iu-role" {...register('initialRoleId')} aria-invalid={Boolean(formState.errors.initialRoleId)}>
            <option value="" disabled hidden>
              {t('team.invite.selectRole')}
            </option>
            {assignableRoles.map((r) => (
              <option key={r.roleId} value={r.roleId}>
                {r.name}
              </option>
            ))}
          </Select>
          {errKey('initialRoleId') ? (
            <p role="alert" className="mt-1 text-[12px] text-danger">
              {errKey('initialRoleId')}
            </p>
          ) : null}
        </div>

        <p className="text-[12px] text-muted">{t('team.invite.firstLoginNote')}</p>
      </form>
    </SlideOver>
  );
}
