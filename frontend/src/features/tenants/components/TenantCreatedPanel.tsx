// One-time owner-invite reveal after tenant onboarding. Shows the clinic that was
// created and the FULL accept link (/accept-invite?token=…) with a copy button —
// the plaintext token is surfaced EXACTLY ONCE (only its hash is persisted; resend
// lives in the tenant's own Invites tab once someone is inside).
//
// SECURITY: the token arrives via the in-store panel payload (NOT the URL, NOT any
// query cache) — the `tenantCreated` panel type is excluded from URL sync. On close
// the payload is dropped; there is no path to re-fetch it.

import { useState } from 'react';
import { Check, Copy, MailPlus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { relativeTime } from '@/lib/format';
import type { CreateTenantResult } from '@/lib/mock/contracts';

export function TenantCreatedPanel({
  result,
  open,
  onClose,
}: {
  result: CreateTenantResult;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);

  const inviteLink = `${window.location.origin}/accept-invite?token=${encodeURIComponent(result.inviteToken)}`;

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(inviteLink);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard may be unavailable (insecure context) — the field stays selectable.
      setCopied(false);
    }
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('tenants.created.eyebrow')}
      title={t('tenants.created.title', { name: result.displayName })}
      description={t('tenants.created.ready', { email: result.adminEmail })}
      footer={
        <Button variant="primary" size="md" onClick={onClose}>
          {t('tenants.created.done')}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        <p className="text-[13px] text-muted">{t('tenants.created.ready', { email: result.adminEmail })}</p>

        {/* No live delivery is wired (invitation notifier is offline by default) — the
            admin copies the link and shares it themselves. Honest copy; no fake "sent ✓". */}
        <div className="flex items-start gap-2 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2.5 text-[12px] text-warn">
          <MailPlus size={16} className="mt-0.5 shrink-0" aria-hidden="true" />
          <span>{t('tenants.created.dispatchNote', { email: result.adminEmail })}</span>
        </div>

        <div>
          <label
            htmlFor="tenant-invite-link"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2"
          >
            {t('tenants.created.linkLabel')}
          </label>
          <div className="flex items-stretch gap-2">
            <input
              id="tenant-invite-link"
              readOnly
              value={inviteLink}
              onFocus={(e) => e.currentTarget.select()}
              className="mono w-full rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-[12px] text-ink outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
            />
            <Button
              variant="ghost"
              size="md"
              onClick={() => void onCopy()}
              aria-label={t('tenants.created.copy')}
            >
              {copied ? <Check size={15} aria-hidden="true" /> : <Copy size={15} aria-hidden="true" />}
              {copied ? t('tenants.created.copied') : t('tenants.created.copy')}
            </Button>
          </div>
          <p className="mt-2 text-[12px] text-muted">
            {t('tenants.created.expiresNote', { time: relativeTime(result.inviteExpiresAt) })}
          </p>
        </div>
      </div>
    </SlideOver>
  );
}
