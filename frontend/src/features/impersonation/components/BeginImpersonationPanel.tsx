// Begin-impersonation slide-over (issue #3). A super_admin picks a target tenant,
// gives a required reason (logged server-side), optionally flags break-glass, and
// submits. On success the EXISTING beginImpersonation helper stores the new
// impersonation token + linkage, and the claim-driven ImpersonationBanner appears
// automatically (its Exit button already works).
//
// The slide-over is the house CRUD modality (URL-addressable via ?panel=, focus-
// trapped, Esc/overlay close — all owned by SlideOver + SlideOverHost). Design
// tokens only; bilingual via react-i18next; react-hook-form + zod like the other
// form panels. No hand-written memo (React Compiler is on).

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { ShieldAlert } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextArea, labelClass } from '@/components/ui/Field';
import { toUserError } from '@/lib/backend';
import { useBeginImpersonation, useTenants } from '../api';

const schema = z.object({
  targetTenantId: z.string().trim().min(1, 'tenant'),
  reason: z.string().trim().min(1, 'reason'),
  breakGlass: z.boolean().default(false),
});
type BeginForm = z.infer<typeof schema>;

export function BeginImpersonationPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  // Only fetch the tenant list while the panel is open.
  const tenantsQuery = useTenants(open);
  const beginMutation = useBeginImpersonation();

  const { register, handleSubmit, formState } = useForm<BeginForm>({
    defaultValues: { targetTenantId: '', reason: '', breakGlass: false },
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
      await beginMutation.mutateAsync({
        targetTenantId: values.targetTenantId,
        reason: values.reason,
        breakGlass: values.breakGlass,
      });
      toast.success(t('impersonation.begin.started'));
      onClose();
    } catch (e) {
      // Surface the API's message (e.g. 403 if the actor lacks the permission).
      toast.error(toUserError(e));
    }
  });

  const errKey = (k: keyof BeginForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`impersonation.begin.validation.${m}`) : undefined;
  };

  const tenants = tenantsQuery.data ?? [];
  const noTenants = tenantsQuery.isSuccess && tenants.length === 0;
  const formId = 'begin-impersonation-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('impersonation.begin.eyebrow')}
      title={t('impersonation.begin.title')}
      description={t('impersonation.begin.warning')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="primary"
            size="md"
            type="submit"
            form={formId}
            disabled={beginMutation.isPending || tenantsQuery.isLoading || noTenants}
          >
            {beginMutation.isPending ? t('impersonation.begin.starting') : t('impersonation.begin.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        {/* Warning — acting as another tenant is a sensitive, logged action. */}
        <div className="flex items-start gap-2.5 rounded-[var(--radius)] border border-warn/40 bg-warn-soft px-3 py-3 text-warn">
          <ShieldAlert size={16} aria-hidden="true" className="mt-0.5 shrink-0" />
          <p className="text-[12px]">{t('impersonation.begin.warning')}</p>
        </div>

        <FieldShell label={t('impersonation.begin.tenant')} htmlFor="imp-tenant" error={errKey('targetTenantId')}>
          {/* Tenant picker states: loading / error / empty / ready. */}
          {tenantsQuery.isLoading ? (
            <div
              className="h-10 animate-pulse rounded-[var(--radius-sm)] bg-surface-sunk"
              role="status"
              aria-label={t('common.loading')}
            />
          ) : tenantsQuery.isError ? (
            <div className="flex items-center justify-between gap-2 rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-[12px] text-muted">
              <span>{t('impersonation.begin.tenantsError')}</span>
              <button
                type="button"
                onClick={() => void tenantsQuery.refetch()}
                className="font-medium text-primary underline hover:no-underline focus-visible:outline-none"
              >
                {t('common.retry')}
              </button>
            </div>
          ) : noTenants ? (
            <p className="rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-[12px] text-muted">
              {t('impersonation.begin.tenantsEmpty')}
            </p>
          ) : (
            <Select
              id="imp-tenant"
              autoFocus
              defaultValue=""
              {...register('targetTenantId')}
              aria-invalid={Boolean(formState.errors.targetTenantId)}
            >
              <option value="" disabled>
                {t('impersonation.begin.tenantPlaceholder')}
              </option>
              {tenants.map((tenant) => (
                <option key={tenant.tenantId} value={tenant.tenantId}>
                  {tenant.displayName} · {tenant.tenantCode}
                </option>
              ))}
            </Select>
          )}
        </FieldShell>

        <FieldShell label={t('impersonation.begin.reason')} htmlFor="imp-reason" error={errKey('reason')}>
          <TextArea
            id="imp-reason"
            rows={3}
            placeholder={t('impersonation.begin.reasonPlaceholder')}
            {...register('reason')}
            aria-invalid={Boolean(formState.errors.reason)}
          />
        </FieldShell>

        {/* Optional break-glass — emergency access; maps to the breakGlass field. */}
        <label
          htmlFor="imp-breakglass"
          className="flex cursor-pointer items-start gap-2.5 rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2.5"
        >
          <input
            id="imp-breakglass"
            type="checkbox"
            className="mt-0.5 h-4 w-4 shrink-0 accent-[var(--warn)]"
            {...register('breakGlass')}
          />
          <span className="min-w-0">
            <span className={labelClass}>{t('impersonation.begin.breakGlass')}</span>
            <span className="mt-0.5 block text-[12px] text-muted">{t('impersonation.begin.breakGlassHint')}</span>
          </span>
        </label>
      </form>
    </SlideOver>
  );
}
