// Public invitation-redemption page (/accept-invite?token=…). The token IS the
// authorization — no session required. The invitee sets their display name + their
// OWN password (never chosen by an admin), POSTs /invitations/accept, and is sent
// to /login on success. Invalid/expired/used tokens all yield the server's single
// generic message (no token-state enumeration).
//
// Standalone full-screen layout like LoginScreen (no AppShell chrome); bilingual;
// tokens only; react-hook-form + zod with the house dependency-free resolver.

import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { FieldShell, TextInput } from '@/components/ui/Field';
import { acceptInvitation, toUserError } from '@/lib/backend';

const schema = z
  .object({
    displayName: z.string().trim().min(1, 'displayName').max(200),
    password: z.string().min(10, 'password'),
    confirmPassword: z.string(),
  })
  .refine((v) => v.password === v.confirmPassword, { path: ['confirmPassword'], message: 'confirm' });
type AcceptForm = z.infer<typeof schema>;

export function AcceptInviteScreen() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { token?: string };
  // Capture the token ONCE into state, then strip it from the URL (auditor hardening:
  // shrinks browser-history/referer exposure of the capability token). A refresh after
  // that loses the form — re-opening the invite link recovers it (single-use on submit).
  const [token] = useState(() => search.token ?? '');
  useEffect(() => {
    if (search.token) window.history.replaceState(window.history.state, '', '/accept-invite');
  }, [search.token]);
  const [submitting, setSubmitting] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  const { register, handleSubmit, formState } = useForm<AcceptForm>({
    defaultValues: { displayName: '', password: '', confirmPassword: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    setFormError(null);
    setSubmitting(true);
    try {
      await acceptInvitation({ token, displayName: values.displayName, password: values.password });
      toast.success(t('acceptInvite.success'));
      await navigate({ to: '/login', search: { redirect: undefined } });
    } catch (e) {
      setFormError(toUserError(e));
    } finally {
      setSubmitting(false);
    }
  });

  const errKey = (k: keyof AcceptForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`acceptInvite.validation.${m}`) : undefined;
  };

  return (
    <div className="flex min-h-screen w-full items-stretch bg-bg text-ink">
      {/* Brand panel — hidden on small screens (mirrors LoginScreen). */}
      <aside className="hidden flex-1 flex-col justify-between bg-ink p-10 text-bg lg:flex">
        <div className="flex items-center gap-2.5">
          <span
            aria-hidden="true"
            className="flex h-9 w-9 items-center justify-center rounded-[var(--radius-sm)] bg-primary text-bg"
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
              <path d="M5 12h4l2-7 4 14 2-7h2" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </span>
          <div className="leading-tight">
            <p className="text-base font-semibold">{t('auth.brand')}</p>
            <p className="text-[10px] font-semibold uppercase tracking-wider text-bg/60">{t('auth.eyebrow')}</p>
          </div>
        </div>
        <p className="max-w-sm text-2xl font-semibold leading-snug text-bg/90">{t('acceptInvite.subtitle')}</p>
        <p className="text-[12px] text-bg/50">© DocSlot</p>
      </aside>

      <main className="flex flex-1 items-center justify-center p-6">
        <div className="w-full max-w-sm">
          <h1 className="text-2xl font-semibold tracking-tight">{t('acceptInvite.title')}</h1>
          <p className="mt-1 text-[13px] text-muted">{t('acceptInvite.subtitle')}</p>

          {!token ? (
            <p role="alert" className="mt-6 rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[13px] text-danger">
              {t('acceptInvite.missingToken')}
            </p>
          ) : (
            <form onSubmit={onSubmit} className="mt-6 flex flex-col gap-4" noValidate>
              <FieldShell label={t('acceptInvite.displayName')} htmlFor="accept-name" error={errKey('displayName')}>
                <TextInput
                  id="accept-name"
                  autoComplete="name"
                  autoFocus
                  placeholder={t('acceptInvite.displayNamePlaceholder')}
                  aria-invalid={Boolean(formState.errors.displayName)}
                  {...register('displayName')}
                />
              </FieldShell>

              <FieldShell label={t('acceptInvite.password')} htmlFor="accept-password" error={errKey('password')}>
                <TextInput
                  id="accept-password"
                  type="password"
                  autoComplete="new-password"
                  placeholder={t('acceptInvite.passwordPlaceholder')}
                  aria-invalid={Boolean(formState.errors.password)}
                  {...register('password')}
                />
              </FieldShell>

              <FieldShell
                label={t('acceptInvite.confirmPassword')}
                htmlFor="accept-confirm"
                error={errKey('confirmPassword')}
              >
                <TextInput
                  id="accept-confirm"
                  type="password"
                  autoComplete="new-password"
                  placeholder={t('acceptInvite.passwordPlaceholder')}
                  aria-invalid={Boolean(formState.errors.confirmPassword)}
                  {...register('confirmPassword')}
                />
              </FieldShell>

              {formError ? (
                <p role="alert" className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[13px] text-danger">
                  {formError}
                </p>
              ) : null}

              <Button type="submit" variant="primary" size="md" className="w-full" disabled={submitting}>
                {submitting ? t('acceptInvite.submitting') : t('acceptInvite.submit')}
              </Button>
            </form>
          )}
        </div>
      </main>
    </div>
  );
}
