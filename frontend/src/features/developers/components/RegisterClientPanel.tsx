// Register API client slide-over. Manual-approval workflow: the client is
// created inactive/unverified. On success we DON'T close to nothing — we swap the
// panel to the one-time secret reveal (carrying the plaintext secret in-store).
// POST carries a stable Idempotency-Key.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { useUI } from '@/stores/ui';
import { useRegisterClient, useScopes } from '../api';

const schema = z.object({
  clientName: z.string().trim().min(1, 'name'),
  clientCode: z
    .string()
    .trim()
    .regex(/^[a-z][a-z0-9-]*$/, 'code'),
  clientType: z.enum(['first_party', 'partner', 'public']),
  ownerEmail: z.string().trim().email('email'),
  ownerOrganization: z.string().trim().optional().default(''),
  purpose: z.string().trim().min(1, 'purpose'),
});
type RegisterForm = z.infer<typeof schema>;

export function RegisterClientPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: scopes, isLoading: scopesLoading } = useScopes();
  const register_ = useRegisterClient();
  const openPanel = useUI((s) => s.openPanel);
  const [selectedScopes, setSelectedScopes] = useState<Set<string>>(new Set());

  const { register, handleSubmit, formState } = useForm<RegisterForm>({
    defaultValues: { clientName: '', clientCode: '', clientType: 'partner', ownerEmail: '', ownerOrganization: '', purpose: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const toggleScope = (key: string) =>
    setSelectedScopes((s) => {
      const next = new Set(s);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const onSubmit = handleSubmit(async (values) => {
    const result = await register_.mutateAsync({
      clientCode: values.clientCode,
      clientName: values.clientName,
      clientType: values.clientType,
      ownerEmail: values.ownerEmail,
      ownerOrganization: values.ownerOrganization || null,
      ownerTenantId: null,
      purpose: values.purpose,
      idempotencyKey: idempotencyKey(),
    });
    // Swap to the one-time secret reveal (the result carries the plaintext once).
    openPanel({ type: 'clientSecret', result, kind: 'client' });
  });

  const errKey = (k: keyof RegisterForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`developers.validation.${m}`) : undefined;
  };

  const formId = 'register-client-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('developers.register.eyebrow')}
      title={t('developers.register.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={register_.isPending}>
            {t('developers.register.register')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <p className="rounded-[var(--radius-sm)] bg-info-soft px-3 py-2 text-[12px] text-info">
          {t('developers.register.manualApproval')}
        </p>

        <FieldShell label={t('developers.register.clientName')} htmlFor="rc-name" error={errKey('clientName')}>
          <TextInput id="rc-name" autoFocus placeholder={t('developers.register.clientNamePlaceholder')} {...register('clientName')} aria-invalid={Boolean(formState.errors.clientName)} />
        </FieldShell>

        <FieldShell label={t('developers.register.clientCode')} htmlFor="rc-code" error={errKey('clientCode')}>
          <TextInput id="rc-code" className="mono" placeholder={t('developers.register.clientCodePlaceholder')} {...register('clientCode')} aria-invalid={Boolean(formState.errors.clientCode)} />
        </FieldShell>

        <div>
          <label htmlFor="rc-type" className={labelClass}>
            {t('developers.register.clientType')}
          </label>
          <Select id="rc-type" {...register('clientType')}>
            <option value="first_party">{t('developers.clientType.first_party')}</option>
            <option value="partner">{t('developers.clientType.partner')}</option>
            <option value="public">{t('developers.clientType.public')}</option>
          </Select>
        </div>

        <FieldShell label={t('developers.register.ownerEmail')} htmlFor="rc-email" error={errKey('ownerEmail')}>
          <TextInput id="rc-email" type="email" placeholder="dev@partner.in" {...register('ownerEmail')} aria-invalid={Boolean(formState.errors.ownerEmail)} />
        </FieldShell>

        <FieldShell label={t('developers.register.ownerOrg')} htmlFor="rc-org" optional={t('developers.register.ownerOrgOptional')}>
          <TextInput id="rc-org" {...register('ownerOrganization')} />
        </FieldShell>

        <FieldShell label={t('developers.register.purpose')} htmlFor="rc-purpose" error={errKey('purpose')}>
          <TextArea id="rc-purpose" rows={2} placeholder={t('developers.register.purposePlaceholder')} {...register('purpose')} aria-invalid={Boolean(formState.errors.purpose)} />
        </FieldShell>

        <section>
          <span className={labelClass}>{t('developers.register.requestedScopes')}</span>
          {scopesLoading || !scopes ? (
            <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
              {Array.from({ length: 5 }).map((_, i) => (
                <Skeleton key={i} className="h-9 w-full" />
              ))}
            </div>
          ) : (
            <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
              {scopes.map((s) => (
                <li key={s.scopeKey}>
                  <label className="flex cursor-pointer items-center gap-2.5 px-3 py-2 transition-colors hover:bg-surface-sunk">
                    <input
                      type="checkbox"
                      checked={selectedScopes.has(s.scopeKey)}
                      onChange={() => toggleScope(s.scopeKey)}
                      className="h-4 w-4 accent-[var(--primary)]"
                    />
                    <span className="min-w-0 flex-1">
                      <span className="mono block truncate text-[12px] text-ink">{s.scopeKey}</span>
                      <span className="block truncate text-[11px] text-muted">{s.description}</span>
                    </span>
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
