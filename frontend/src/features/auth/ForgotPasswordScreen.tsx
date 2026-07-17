// Public "Forgot password?" page (/forgot-password). No session required — the email
// IS the identity claim. On submit we POST /auth/forgot-password and ALWAYS switch to a
// single generic confirmation ("if that email exists, a link has been sent"), regardless
// of whether the account exists (anti-enumeration — never reveal existence).
//
// Standalone full-screen layout like LoginScreen (no AppShell chrome); bilingual; tokens
// only; react-hook-form + zod with the house dependency-free resolver.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Button } from '@/components/ui/Button';
import { FieldShell, TextInput } from '@/components/ui/Field';
import { useForgotPassword } from './api';

const schema = z.object({
  email: z.string().trim().email('email'),
});
type ForgotForm = z.infer<typeof schema>;

export function ForgotPasswordScreen() {
  const { t } = useTranslation();
  const forgot = useForgotPassword();
  // Once submitted, we show the generic confirmation. We keep the entered email only to
  // personalise the copy — the confirmation is identical for existent/non-existent emails.
  const [sentTo, setSentTo] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const { register, handleSubmit, formState } = useForm<ForgotForm>({
    defaultValues: { email: '' },
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
    try {
      // The response is identical for existent/non-existent emails; we only need it to
      // resolve before flipping to the generic confirmation.
      await forgot.mutateAsync({ email: values.email });
      setSentTo(values.email);
    } catch {
      // A network failure is NOT an enumeration signal — surface a generic retry error.
      setFormError(t('forgotPassword.error'));
    }
  });

  const errKey = (k: keyof ForgotForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`auth.validation.${m}`) : undefined;
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
        <p className="max-w-sm text-2xl font-semibold leading-snug text-bg/90">{t('forgotPassword.subtitle')}</p>
        <p className="text-[12px] text-bg/50">© DocSlot</p>
      </aside>

      <main className="flex flex-1 items-center justify-center p-6">
        <div className="w-full max-w-sm">
          {sentTo ? (
            // Generic confirmation — identical whether or not the email exists.
            <>
              <h1 className="text-2xl font-semibold tracking-tight">{t('forgotPassword.sentTitle')}</h1>
              <p className="mt-2 text-[13px] text-muted">{t('forgotPassword.sentBody')}</p>
              <Link
                to="/login"
                search={{ redirect: undefined }}
                className="mt-6 inline-flex text-[13px] font-medium text-primary underline-offset-2 transition-colors hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                {t('forgotPassword.backToLogin')}
              </Link>
            </>
          ) : (
            <>
              <h1 className="text-2xl font-semibold tracking-tight">{t('forgotPassword.title')}</h1>
              <p className="mt-1 text-[13px] text-muted">{t('forgotPassword.subtitle')}</p>

              <form onSubmit={onSubmit} className="mt-6 flex flex-col gap-4" noValidate>
                <FieldShell label={t('auth.email')} htmlFor="forgot-email" error={errKey('email')}>
                  <TextInput
                    id="forgot-email"
                    type="email"
                    autoComplete="email"
                    autoFocus
                    placeholder={t('auth.emailPlaceholder')}
                    aria-invalid={Boolean(formState.errors.email)}
                    {...register('email')}
                  />
                </FieldShell>

                {formError ? (
                  <p role="alert" className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[13px] text-danger">
                    {formError}
                  </p>
                ) : null}

                <Button type="submit" variant="primary" size="md" className="w-full" disabled={forgot.isPending}>
                  {forgot.isPending ? t('forgotPassword.submitting') : t('forgotPassword.submit')}
                </Button>
              </form>

              <Link
                to="/login"
                search={{ redirect: undefined }}
                className="mt-4 inline-flex text-[13px] font-medium text-primary underline-offset-2 transition-colors hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                {t('forgotPassword.backToLogin')}
              </Link>
            </>
          )}
        </div>
      </main>
    </div>
  );
}
