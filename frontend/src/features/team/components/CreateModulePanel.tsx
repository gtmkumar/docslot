// Create-module slide-over (CATALOG plane — platform-governed). Defines a new
// matrix section (a licensable resource group). resourceKey is lower_snake
// (validated); name + optional description + optional displayOrder. POST carries a
// stable Idempotency-Key. Gated upstream by platform.permissions.manage (the DB
// re-checks; a 403/409 surfaces via toUserError). On success: toast, close, and
// invalidate the modules + role-matrix queries so the new section appears.

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
import { useCreateModule } from '../api';

const schema = z.object({
  resourceKey: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/, 'resourceKey'),
  name: z.string().trim().min(1, 'name'),
  description: z.string().trim().max(280).optional(),
  // Kept as a string in the form; coerced to a number (or omitted) on submit.
  displayOrder: z
    .string()
    .trim()
    .regex(/^\d*$/, 'displayOrder')
    .optional(),
});
type ModuleForm = z.infer<typeof schema>;

export function CreateModulePanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreateModule();
  const [serverError, setServerError] = useState<string | null>(null);

  const { register, handleSubmit, formState } = useForm<ModuleForm>({
    defaultValues: { resourceKey: '', name: '', description: '', displayOrder: '' },
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
    const order = values.displayOrder?.trim() ? Number.parseInt(values.displayOrder, 10) : undefined;
    try {
      await create.mutateAsync({
        resourceKey: values.resourceKey,
        name: values.name,
        description: values.description?.trim() ? values.description.trim() : null,
        displayOrder: Number.isFinite(order) ? order : undefined,
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('team.catalog.module.created'));
      onClose();
    } catch (e) {
      setServerError(toUserError(e));
    }
  });

  const errKey = (k: keyof ModuleForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`team.catalog.validation.${m}`) : undefined;
  };

  const formId = 'create-module-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.catalog.module.eyebrow')}
      title={t('team.catalog.module.title')}
      description={t('team.catalog.module.description')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={create.isPending}>
            {t('team.catalog.module.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('team.catalog.module.name')} htmlFor="mod-name" error={errKey('name')}>
          <TextInput
            id="mod-name"
            autoFocus
            placeholder={t('team.catalog.module.namePlaceholder')}
            {...register('name')}
            aria-invalid={Boolean(formState.errors.name)}
          />
        </FieldShell>

        <FieldShell label={t('team.catalog.module.key')} htmlFor="mod-key" error={errKey('resourceKey')}>
          <TextInput
            id="mod-key"
            className="mono"
            placeholder={t('team.catalog.module.keyPlaceholder')}
            {...register('resourceKey')}
            aria-invalid={Boolean(formState.errors.resourceKey)}
          />
        </FieldShell>

        <FieldShell
          label={t('team.catalog.module.descLabel')}
          htmlFor="mod-desc"
          optional={t('team.catalog.optional')}
          error={errKey('description')}
        >
          <TextArea id="mod-desc" rows={2} placeholder={t('team.catalog.module.descPlaceholder')} {...register('description')} />
        </FieldShell>

        <FieldShell
          label={t('team.catalog.module.order')}
          htmlFor="mod-order"
          optional={t('team.catalog.optional')}
          error={errKey('displayOrder')}
        >
          <TextInput
            id="mod-order"
            inputMode="numeric"
            placeholder={t('team.catalog.module.orderPlaceholder')}
            {...register('displayOrder')}
            aria-invalid={Boolean(formState.errors.displayOrder)}
          />
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
