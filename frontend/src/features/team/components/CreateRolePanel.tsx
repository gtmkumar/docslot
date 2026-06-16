// Create custom (tenant-scoped) role slide-over. Name + key + permission picker
// from the registry. POST carries a stable Idempotency-Key. Gated upstream by
// platform.roles.manage.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { ShieldAlert } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextInput } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { useCreateRole, usePermissionRegistry } from '../api';

const schema = z.object({
  name: z.string().trim().min(1, 'name'),
  roleKey: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/, 'key'),
});
type RoleForm = z.infer<typeof schema>;

export function CreateRolePanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: registry, isLoading } = usePermissionRegistry();
  const createRole = useCreateRole();
  const [selected, setSelected] = useState<Set<string>>(new Set());

  const { register, handleSubmit, formState } = useForm<RoleForm>({
    defaultValues: { name: '', roleKey: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const toggle = (key: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const onSubmit = handleSubmit(async (values) => {
    await createRole.mutateAsync({
      name: values.name,
      roleKey: values.roleKey,
      permissionKeys: [...selected],
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('team.createRolePanel.created'));
    onClose();
  });

  const errKey = (k: keyof RoleForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`team.validation.${m}`) : undefined;
  };

  const formId = 'create-role-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.createRolePanel.eyebrow')}
      title={t('team.createRolePanel.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={createRole.isPending}>
            {t('team.createRolePanel.create')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('team.createRolePanel.name')} htmlFor="cr-name" error={errKey('name')}>
          <TextInput id="cr-name" autoFocus placeholder={t('team.createRolePanel.namePlaceholder')} {...register('name')} aria-invalid={Boolean(formState.errors.name)} />
        </FieldShell>

        <FieldShell label={t('team.createRolePanel.key')} htmlFor="cr-key" error={errKey('roleKey')}>
          <TextInput id="cr-key" className="mono" placeholder={t('team.createRolePanel.keyPlaceholder')} {...register('roleKey')} aria-invalid={Boolean(formState.errors.roleKey)} />
        </FieldShell>

        <section>
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('team.createRolePanel.permissions')}
          </h3>
          <p className="mb-2 text-[12px] text-muted">{t('team.createRolePanel.permissionsSub')}</p>

          {isLoading || !registry ? (
            <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
              {Array.from({ length: 6 }).map((_, i) => (
                <Skeleton key={i} className="h-9 w-full" />
              ))}
            </div>
          ) : (
            <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
              {registry.map((p) => (
                <li key={p.permissionKey}>
                  <label className="flex cursor-pointer items-center gap-2.5 px-3 py-2 transition-colors hover:bg-surface-sunk">
                    <input
                      type="checkbox"
                      checked={selected.has(p.permissionKey)}
                      onChange={() => toggle(p.permissionKey)}
                      className="h-4 w-4 accent-[var(--primary)]"
                    />
                    <span className="min-w-0 flex-1">
                      <span className="mono block truncate text-[12px] text-ink">{p.permissionKey}</span>
                      <span className="block truncate text-[11px] text-muted">{p.description}</span>
                    </span>
                    {p.isDangerous ? (
                      <ShieldAlert size={14} className="shrink-0 text-warn" aria-label={t('team.roleView.dangerous')} />
                    ) : null}
                  </label>
                </li>
              ))}
            </ul>
          )}
        </section>
      </form>
    </SlideOver>
  );
}
