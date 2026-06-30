// Create-permission slide-over (CATALOG plane — platform-governed). Defines a new
// permission in the registry: a module picker (resource = the chosen module's
// resourceKey), an action (lower_snake), an auto-previewed permissionKey the admin
// may edit, a scope (platform|tenant|self), a description, and an isDangerous
// toggle. POST carries a stable Idempotency-Key. Gated upstream by
// platform.permissions.manage (the DB re-checks; 403/409 → toUserError toast).
//
// IMPORTANT correctness caveat shown to the admin: a new permission is INERT until
// application code checks it. Creating it makes it grantable in the role matrix but
// enforces NOTHING on its own — it appears as a (revocable) cell under its module.
//
// On success: toast, close, and invalidate the modules + role-matrix queries so the
// new permission appears as a matrix cell.

import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Info, ShieldAlert } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextArea, TextInput } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useCreatePermission, useModules } from '../api';

const SCOPES = ['platform', 'tenant', 'self'] as const;

const schema = z.object({
  resource: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/, 'resource'),
  action: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*$/, 'action'),
  permissionKey: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$/, 'permissionKey'),
  scope: z.enum(SCOPES),
  description: z.string().trim().min(1, 'description'),
  isDangerous: z.boolean(),
});
type PermForm = z.infer<typeof schema>;

export function CreatePermissionPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: modules, isLoading } = useModules();
  const create = useCreatePermission();
  const [serverError, setServerError] = useState<string | null>(null);
  // Tracks whether the admin has hand-edited the key. Until then the key tracks the
  // auto preview `<resource>.<action>`; once edited, we stop overwriting it.
  const [keyTouched, setKeyTouched] = useState(false);

  const { register, handleSubmit, setValue, formState, control } = useForm<PermForm>({
    defaultValues: { resource: '', action: '', permissionKey: '', scope: 'tenant', description: '', isDangerous: false },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  // useWatch (hook), not watch() (method): keeps these reactive under React Compiler,
  // which otherwise memoizes the watch() return so the key preview + danger banner go
  // stale (same root cause as the triage Run button, issue #49).
  const resource = useWatch({ control, name: 'resource' });
  const action = useWatch({ control, name: 'action' });
  const isDangerous = useWatch({ control, name: 'isDangerous' });

  // Keep the previewed key in sync with resource+action until the admin edits it.
  const syncKey = (nextResource: string, nextAction: string) => {
    if (keyTouched) return;
    const auto = nextResource && nextAction ? `${nextResource}.${nextAction}` : '';
    setValue('permissionKey', auto, { shouldValidate: false });
  };

  const onSubmit = handleSubmit(async (values) => {
    setServerError(null);
    try {
      await create.mutateAsync({
        permissionKey: values.permissionKey,
        resource: values.resource,
        action: values.action,
        scope: values.scope,
        description: values.description,
        isDangerous: values.isDangerous,
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('team.catalog.permission.created'));
      onClose();
    } catch (e) {
      setServerError(toUserError(e));
    }
  });

  const errKey = (k: keyof PermForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`team.catalog.validation.${m}`) : undefined;
  };

  const resourceReg = register('resource');
  const actionReg = register('action');
  const keyReg = register('permissionKey');

  const formId = 'create-permission-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.catalog.permission.eyebrow')}
      title={t('team.catalog.permission.title')}
      description={t('team.catalog.permission.description')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={create.isPending}>
            {t('team.catalog.permission.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        {/* Module picker → resource = the chosen module's resourceKey. */}
        <FieldShell label={t('team.catalog.permission.module')} htmlFor="perm-module" error={errKey('resource')}>
          {isLoading || !modules ? (
            <Skeleton className="h-9 w-full" />
          ) : (
            <Select
              id="perm-module"
              {...resourceReg}
              aria-invalid={Boolean(formState.errors.resource)}
              onChange={(e) => {
                resourceReg.onChange(e);
                syncKey(e.target.value, action);
              }}
            >
              <option value="">{t('team.catalog.permission.selectModule')}</option>
              {modules.map((m) => (
                <option key={m.resourceKey} value={m.resourceKey}>
                  {m.name} ({m.resourceKey})
                </option>
              ))}
            </Select>
          )}
        </FieldShell>

        <FieldShell label={t('team.catalog.permission.action')} htmlFor="perm-action" error={errKey('action')}>
          <TextInput
            id="perm-action"
            className="mono"
            placeholder={t('team.catalog.permission.actionPlaceholder')}
            {...actionReg}
            aria-invalid={Boolean(formState.errors.action)}
            onChange={(e) => {
              actionReg.onChange(e);
              syncKey(resource, e.target.value);
            }}
          />
        </FieldShell>

        <FieldShell label={t('team.catalog.permission.key')} htmlFor="perm-key" error={errKey('permissionKey')}>
          <TextInput
            id="perm-key"
            className="mono"
            placeholder={t('team.catalog.permission.keyPlaceholder')}
            {...keyReg}
            aria-invalid={Boolean(formState.errors.permissionKey)}
            onChange={(e) => {
              setKeyTouched(true);
              void keyReg.onChange(e);
            }}
          />
          <p className="mt-1 text-[11px] text-muted">{t('team.catalog.permission.keyHint')}</p>
        </FieldShell>

        <FieldShell label={t('team.catalog.permission.scope')} htmlFor="perm-scope" error={errKey('scope')}>
          <Select id="perm-scope" {...register('scope')} aria-invalid={Boolean(formState.errors.scope)}>
            {SCOPES.map((s) => (
              <option key={s} value={s}>
                {t(`team.catalog.permission.scopeOption.${s}`)}
              </option>
            ))}
          </Select>
        </FieldShell>

        <FieldShell label={t('team.catalog.permission.descLabel')} htmlFor="perm-desc" error={errKey('description')}>
          <TextArea
            id="perm-desc"
            rows={2}
            placeholder={t('team.catalog.permission.descPlaceholder')}
            {...register('description')}
            aria-invalid={Boolean(formState.errors.description)}
          />
        </FieldShell>

        <label className="flex items-start gap-2.5 rounded-[var(--radius-sm)] border border-line px-3 py-2.5 transition-colors hover:bg-surface-sunk">
          <input type="checkbox" {...register('isDangerous')} className="mt-0.5 h-4 w-4 accent-[var(--primary)]" />
          <span className="min-w-0 flex-1">
            <span className="flex items-center gap-1.5 text-[13px] font-medium text-ink">
              {isDangerous ? <ShieldAlert size={14} className="shrink-0 text-warn" aria-hidden="true" /> : null}
              {t('team.catalog.permission.dangerous')}
            </span>
            <span className="block text-[11px] text-muted">{t('team.catalog.permission.dangerousSub')}</span>
          </span>
        </label>

        {/* Correctness caveat: a new permission is inert until application code checks it. */}
        <p className="flex items-start gap-1.5 rounded-[var(--radius-sm)] border border-info-soft bg-info-soft px-3 py-2 text-[12px] text-info">
          <Info size={14} className="mt-px shrink-0" aria-hidden="true" />
          {t('team.catalog.permission.inertNote')}
        </p>

        {serverError ? (
          <p role="alert" className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[12px] text-danger">
            {serverError}
          </p>
        ) : null}
      </form>
    </SlideOver>
  );
}
