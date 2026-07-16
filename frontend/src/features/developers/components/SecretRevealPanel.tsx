// One-time secret reveal (REACT_SKILL: secrets shown once). Displays the
// plaintext client secret / webhook signing secret EXACTLY ONCE with a copy
// button and a prominent "won't be shown again" warning.
//
// SECURITY: the secret arrives via the in-store panel payload (NOT the URL, NOT
// any query cache). This panel renders it, lets the user copy it, and on close
// the payload is dropped from the store — there is no path to re-fetch it. The
// `clientSecret` panel type is excluded from URL sync for this reason.

import { useState } from 'react';
import { Check, Copy, ShieldAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import type { ApiClientSecretResult, CreateWebhookResult } from '@/lib/mock/contracts';

interface SecretRevealPanelProps {
  result: ApiClientSecretResult | CreateWebhookResult;
  kind: 'client' | 'webhook';
  intent: 'created' | 'rotated';
  open: boolean;
  onClose: () => void;
}

export function SecretRevealPanel({ result, kind, intent, open, onClose }: SecretRevealPanelProps) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);

  const secret = kind === 'client' ? (result as ApiClientSecretResult).clientSecret : (result as CreateWebhookResult).signingSecret;
  const labelKey = kind === 'client' ? 'developers.secret.clientSecret' : 'developers.secret.signingSecret';
  // Title says what just HAPPENED: registration vs rotation vs webhook creation.
  const titleKey =
    intent === 'rotated'
      ? 'developers.secret.titleRotate'
      : kind === 'client'
        ? 'developers.secret.titleNew'
        : 'developers.secret.titleWebhookNew';

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(secret);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard may be unavailable (insecure context) — the field is selectable.
      setCopied(false);
    }
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('developers.secret.eyebrow')}
      title={t(titleKey)}
      description={t('developers.secret.warning')}
      footer={
        <Button variant="primary" size="md" onClick={onClose}>
          {t('developers.secret.done')}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        {kind === 'client' ? (
          <div>
            <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              {t('developers.secret.clientSecret')}
            </p>
          </div>
        ) : null}

        <div className="flex items-start gap-2 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2.5 text-[12px] text-warn">
          <ShieldAlert size={16} className="mt-0.5 shrink-0" aria-hidden="true" />
          <span>{t('developers.secret.warning')}</span>
        </div>

        <div>
          <label htmlFor="secret-value" className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t(labelKey)}
          </label>
          <div className="flex items-stretch gap-2">
            <input
              id="secret-value"
              readOnly
              value={secret}
              onFocus={(e) => e.currentTarget.select()}
              className="mono w-full rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-[12px] text-ink outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
            />
            <Button variant="ghost" size="md" onClick={() => void onCopy()} aria-label={t('developers.secret.copy')}>
              {copied ? <Check size={15} aria-hidden="true" /> : <Copy size={15} aria-hidden="true" />}
              {copied ? t('developers.secret.copied') : t('developers.secret.copy')}
            </Button>
          </div>
        </div>
      </div>
    </SlideOver>
  );
}
