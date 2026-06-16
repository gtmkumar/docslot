// Login screen (Slice 01). Standalone full-screen layout (no AppShell chrome):
// brand panel + sign-in card. react-hook-form + zod (dependency-free resolver),
// bilingual, tokens only. Surfaces the mock 401 (invalid) and 423 (lockout)
// messages. On success the router guard redirects to the original target.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { FieldShell, TextInput } from '@/components/ui/Field';
import { DEMO_LOGIN, MockApiError } from '@/lib/mock';
import { useLogin } from './api';
import { loginSchema, type LoginForm } from './schema';

export function LoginScreen() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { redirect?: string };
  const doLogin = useLogin();
  const [formError, setFormError] = useState<string | null>(null);

  const { register, handleSubmit, formState } = useForm<LoginForm>({
    defaultValues: { email: '', password: '' },
    resolver: async (values) => {
      const parsed = loginSchema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    setFormError(null);
    try {
      await doLogin.mutateAsync({ email: values.email, password: values.password });
      await navigate({ to: search.redirect ?? '/' });
    } catch (e) {
      // Map the mock error key to a bilingual message; default to invalid.
      const key = e instanceof MockApiError ? e.messageKey : 'auth.error.invalid';
      setFormError(t(key));
    }
  });

  const errKey = (k: keyof LoginForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`auth.validation.${m}`) : undefined;
  };

  return (
    <div className="flex min-h-screen w-full items-stretch bg-bg text-ink">
      {/* Brand panel — hidden on small screens. */}
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
        <p className="max-w-sm text-2xl font-semibold leading-snug text-bg/90">{t('auth.subtitle')}</p>
        <p className="text-[12px] text-bg/50">© DocSlot</p>
      </aside>

      {/* Sign-in card. */}
      <main className="flex flex-1 items-center justify-center p-6">
        <div className="w-full max-w-sm">
          <h1 className="text-2xl font-semibold tracking-tight">{t('auth.title')}</h1>
          <p className="mt-1 text-[13px] text-muted">{t('auth.subtitle')}</p>

          <form onSubmit={onSubmit} className="mt-6 flex flex-col gap-4" noValidate>
            <FieldShell label={t('auth.email')} htmlFor="login-email" error={errKey('email')}>
              <TextInput
                id="login-email"
                type="email"
                autoComplete="email"
                autoFocus
                placeholder={t('auth.emailPlaceholder')}
                aria-invalid={Boolean(formState.errors.email)}
                {...register('email')}
              />
            </FieldShell>

            <FieldShell label={t('auth.password')} htmlFor="login-password" error={errKey('password')}>
              <TextInput
                id="login-password"
                type="password"
                autoComplete="current-password"
                placeholder={t('auth.passwordPlaceholder')}
                aria-invalid={Boolean(formState.errors.password)}
                {...register('password')}
              />
            </FieldShell>

            {formError ? (
              <p role="alert" className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[13px] text-danger">
                {formError}
              </p>
            ) : null}

            <Button type="submit" variant="primary" size="md" className="w-full" disabled={doLogin.isPending}>
              {doLogin.isPending ? t('auth.signingIn') : t('auth.signIn')}
            </Button>
          </form>

          <p className="mt-4 text-center text-[12px] text-muted-2">
            {t('auth.demoHint', { email: DEMO_LOGIN.email, password: DEMO_LOGIN.password })}
          </p>
        </div>
      </main>
    </div>
  );
}
