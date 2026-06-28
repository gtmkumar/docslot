// Edit-user-profile slide-over (the PRIMARY CRUD modality — not a centered modal).
// Whitelisted profile fields only: fullName, phone, preferredLanguage. Email is the
// user's identity and is shown READ-ONLY. The stored phone is masked (PHI), so the
// phone input opens blank with a "leave blank to keep" hint — we never present the
// masked value as if it were editable; typing REPLACES the stored number.
//
// react-hook-form + a dependency-free zod resolver (mirrors InviteUserPanel). The
// PUT carries a stable Idempotency-Key generated once per submit. Gated upstream by
// tenant.users.update; editing your own profile is allowed (benign).

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useTenantUsers, useUpdateUser } from '../api';

const schema = z.object({
  fullName: z.string().trim().min(1, 'name'),
  phone: z.string().trim().optional().default(''),
  preferredLanguage: z.enum(['en', 'hi']).default('en'),
});
type EditForm = z.infer<typeof schema>;

export function EditUserPanel({ userId, open, onClose }: { userId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: users } = useTenantUsers();
  const user = users?.find((u) => u.userId === userId);
  const updateUser = useUpdateUser();

  const { register, handleSubmit, formState } = useForm<EditForm>({
    // Pre-fill name + language from the loaded row. Phone is intentionally blank:
    // the stored value is masked (PHI) and must not be re-presented as editable.
    defaultValues: { fullName: user?.fullName ?? '', phone: '', preferredLanguage: 'en' },
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
      await updateUser.mutateAsync({
        userId,
        fullName: values.fullName,
        // Blank phone → null = "keep the stored (masked) number"; a typed value replaces it.
        phone: values.phone ? values.phone : null,
        preferredLanguage: values.preferredLanguage,
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('team.edit.saved'));
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof EditForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`team.validation.${m}`) : undefined;
  };

  const formId = 'edit-user-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.edit.eyebrow')}
      title={user?.fullName ?? t('team.edit.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={updateUser.isPending}>
            {t('team.edit.save')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        {/* Email — identity, read-only. Shown as a static field, not an input. */}
        <div>
          <span className={labelClass}>{t('team.invite.email')}</span>
          <p className="mono rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-sm text-muted">
            {user?.email ?? '—'}
          </p>
          <p className="mt-1 text-[11px] text-muted-2">{t('team.edit.emailReadonly')}</p>
        </div>

        <FieldShell label={t('team.edit.fullName')} htmlFor="eu-name" error={errKey('fullName')}>
          <TextInput
            id="eu-name"
            autoFocus
            placeholder={t('team.invite.fullNamePlaceholder')}
            {...register('fullName')}
            aria-invalid={Boolean(formState.errors.fullName)}
          />
        </FieldShell>

        <FieldShell label={t('team.edit.phone')} htmlFor="eu-phone" optional={t('team.invite.phoneOptional')}>
          <TextInput
            id="eu-phone"
            type="tel"
            inputMode="tel"
            className="mono"
            placeholder="+91 98765 43210"
            {...register('phone')}
          />
          <p className="mt-1 text-[11px] text-muted-2">{t('team.edit.phoneKeepHint')}</p>
        </FieldShell>

        <div>
          <label htmlFor="eu-lang" className={labelClass}>
            {t('team.edit.language')}
          </label>
          <Select id="eu-lang" {...register('preferredLanguage')}>
            <option value="en">English</option>
            <option value="hi">हिन्दी</option>
          </Select>
        </div>
      </form>
    </SlideOver>
  );
}
