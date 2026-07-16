// Public "Reset password" page (/reset-password?token=…). The token IS the
// authorization — no session required. The user sets a new password; on success we send
// them to /login with a success toast. Invalid/expired/used tokens all yield ONE generic
// inline message (no token-state enumeration).
//
// Mirrors AcceptInviteScreen: the token is captured ONCE into state, then stripped from
// the URL (shrinks browser-history/referer exposure of the one-time capability token).
// Standalone full-screen layout; bilingual; tokens only; react-hook-form + zod with the
// house dependency-free resolver. Client floor is 8 chars (the server enforces the real one).

import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { FieldShell, TextInput } from '@/components/ui/Field';
import { useResetPassword } from './api';

const schema = z
  .object({
    password: z.string().min(8, 'password'),
    confirmPassword: z.string(),
  })
  .refine((v) => v.password === v.confirmPassword, { path: ['confirmPassword'], message: 'confirm' });
type ResetForm = z.infer<typeof schema>;

export function ResetPasswordScreen() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search = useSearch({ strict: false }) as { token?: string };
  // Capture the token ONCE, then strip it from the URL (mirrors AcceptInviteScreen). A
  // refresh after that loses the form — re-opening the reset link recovers it (single-use
  // on submit).
  const [token] = useState(() => search.token ?? '');
  useEffect(() => {
    if (search.token) window.history.replaceState(window.history.state, '', '/reset-password');
  }, [search.token]);
  const reset = useResetPassword();
  const [formError, setFormError] = useState<string | null>(null);

  const { register, handleSubmit, formState } = useForm<ResetForm>({
    defaultValues: { password: '', confirmPassword: '' },
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
      await reset.mutateAsync({ token, newPassword: values.password });
      toast.success(t('resetPassword.success'));
      await navigate({ to: '/login', search: { redirect: undefined } });
    } catch {
      // Invalid/expired/used token (or any failure) → the single generic message; the
      // server never distinguishes token states, and neither do we.
      setFormError(t('resetPassword.invalid'));
    }
  });

  const errKey = (k: keyof ResetForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`resetPassword.validation.${m}`) : undefined;
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
        <p className="max-w-sm text-2xl font-semibold leading-snug text-bg/90">{t('resetPassword.subtitle')}</p>
        <p className="text-[12px] text-bg/50">© DocSlot</p>
      </aside>

      <main className="flex flex-1 items-center justify-center p-6">
        <div className="w-full max-w-sm">
          <h1 className="text-2xl font-semibold tracking-tight">{t('resetPassword.title')}</h1>
          <p className="mt-1 text-[13px] text-muted">{t('resetPassword.subtitle')}</p>

          {!token ? (
            <p role="alert" className="mt-6 rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[13px] text-danger">
              {t('resetPassword.missingToken')}
            </p>
          ) : (
            <form onSubmit={onSubmit} className="mt-6 flex flex-col gap-4" noValidate>
              <FieldShell label={t('resetPassword.password')} htmlFor="reset-password" error={errKey('password')}>
                <TextInput
                  id="reset-password"
                  type="password"
                  autoComplete="new-password"
                  autoFocus
                  placeholder={t('resetPassword.passwordPlaceholder')}
                  aria-invalid={Boolean(formState.errors.password)}
                  {...register('password')}
                />
              </FieldShell>

              <FieldShell
                label={t('resetPassword.confirmPassword')}
                htmlFor="reset-confirm"
                error={errKey('confirmPassword')}
              >
                <TextInput
                  id="reset-confirm"
                  type="password"
                  autoComplete="new-password"
                  placeholder={t('resetPassword.passwordPlaceholder')}
                  aria-invalid={Boolean(formState.errors.confirmPassword)}
                  {...register('confirmPassword')}
                />
              </FieldShell>

              {formError ? (
                <p role="alert" className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[13px] text-danger">
                  {formError}
                </p>
              ) : null}

              <Button type="submit" variant="primary" size="md" className="w-full" disabled={reset.isPending}>
                {reset.isPending ? t('resetPassword.submitting') : t('resetPassword.submit')}
              </Button>
            </form>
          )}
        </div>
      </main>
    </div>
  );
}
