// Duplicate-role slide-over. Clones a built-in (or any) role + its grants into a
// new tenant-scoped, EDITABLE role, then opens the new role's matrix so the admin
// can immediately tailor it. newRoleKey is lower_snake_case (validated). POST
// carries a stable Idempotency-Key. Gated upstream by tenant.roles.assign.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useUI } from '@/stores/ui';
import { useDuplicateRole, useRoleMatrix } from '../api';

const schema = z.object({
  newName: z.string().trim().min(1, 'name'),
  newRoleKey: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/, 'key'),
  description: z.string().trim().max(280).optional(),
});
type DupForm = z.infer<typeof schema>;

export function DuplicateRolePanel({ roleId, open, onClose }: { roleId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const openPanel = useUI((s) => s.openPanel);
  const { data: source } = useRoleMatrix(roleId);
  const duplicate = useDuplicateRole();
  const [serverError, setServerError] = useState<string | null>(null);

  const { register, handleSubmit, formState } = useForm<DupForm>({
    defaultValues: { newName: '', newRoleKey: '', description: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    setServerError(null);
    try {
      const result = await duplicate.mutateAsync({
        sourceRoleId: roleId,
        newRoleKey: values.newRoleKey,
        newName: values.newName,
        description: values.description?.trim() ? values.description.trim() : null,
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('team.duplicate.created'));
      // Navigate straight to the new role's (editable) matrix.
      openPanel({ type: 'roleMatrix', roleId: result.roleId });
    } catch (e) {
      setServerError(toUserError(e));
    }
  });

  const errKey = (k: keyof DupForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`team.validation.${m}`) : undefined;
  };

  const formId = 'duplicate-role-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.duplicate.eyebrow')}
      title={t('team.duplicate.title')}
      description={t('team.duplicate.description')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={duplicate.isPending}>
            {t('team.duplicate.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        {source ? (
          <p className="rounded-[var(--radius-sm)] border border-line bg-bg-2 px-3 py-2 text-[12px] text-muted">
            {t('team.duplicate.cloningFrom')} <span className="mono text-ink">{source.roleKey}</span>
            {' · '}
            {t('team.matrix.grantedOf', { granted: source.grantedCount, total: source.totalCount })}
          </p>
        ) : null}

        <FieldShell label={t('team.duplicate.name')} htmlFor="dup-name" error={errKey('newName')}>
          <TextInput
            id="dup-name"
            autoFocus
            placeholder={t('team.duplicate.namePlaceholder')}
            {...register('newName')}
            aria-invalid={Boolean(formState.errors.newName)}
          />
        </FieldShell>

        <FieldShell label={t('team.duplicate.key')} htmlFor="dup-key" error={errKey('newRoleKey')}>
          <TextInput
            id="dup-key"
            className="mono"
            placeholder={t('team.duplicate.keyPlaceholder')}
            {...register('newRoleKey')}
            aria-invalid={Boolean(formState.errors.newRoleKey)}
          />
        </FieldShell>

        <FieldShell label={t('team.duplicate.descLabel')} htmlFor="dup-desc" optional={t('team.duplicate.optional')}>
          <TextArea id="dup-desc" rows={2} placeholder={t('team.duplicate.descPlaceholder')} {...register('description')} />
        </FieldShell>

        {serverError ? (
          <p role="alert" className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[12px] text-danger">
            {serverError}
          </p>
        ) : null}
      </form>
    </SlideOver>
  );
}
