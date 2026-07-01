// One-time invitation-token reveal (#89). Shown after a successful create OR
// resend — the plaintext token is surfaced EXACTLY ONCE, with a copy button and a
// note that automated email/WhatsApp delivery lands in #93 (so the admin hands off
// the link manually for now).
//
// SECURITY: the token arrives via the in-store panel payload (NOT the URL, NOT any
// query cache). This panel renders it, lets the admin copy it, and on close the
// payload is dropped — there is no path to re-fetch it (resend mints a fresh one).
// The `invitationToken` panel type is excluded from URL sync for this reason.

import { useState } from 'react';
import { Check, Copy, MailPlus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { relativeTime } from '@/lib/format';
import type { InvitationTokenResult } from '@/lib/mock/contracts';

interface InvitationTokenPanelProps {
  result: InvitationTokenResult;
  email: string;
  open: boolean;
  onClose: () => void;
}

export function InvitationTokenPanel({ result, email, open, onClose }: InvitationTokenPanelProps) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(result.token);
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
      eyebrow={t('team.invites.token.eyebrow')}
      title={t('team.invites.token.title')}
      description={t('team.invites.token.ready', { email })}
      footer={
        <Button variant="primary" size="md" onClick={onClose}>
          {t('team.invites.token.done')}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        <p className="text-[13px] text-muted">{t('team.invites.token.ready', { email })}</p>

        {/* #93 hand-off note — automated delivery isn't wired yet, so the admin copies
            the link and sends it themselves for now. */}
        <div className="flex items-start gap-2 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2.5 text-[12px] text-warn">
          <MailPlus size={16} className="mt-0.5 shrink-0" aria-hidden="true" />
          <span>{t('team.invites.token.handoffNote')}</span>
        </div>

        <div>
          <label
            htmlFor="invitation-token"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2"
          >
            {t('team.invites.token.label')}
          </label>
          <div className="flex items-stretch gap-2">
            <input
              id="invitation-token"
              readOnly
              value={result.token}
              onFocus={(e) => e.currentTarget.select()}
              className="mono w-full rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-[12px] text-ink outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
            />
            <Button
              variant="ghost"
              size="md"
              onClick={() => void onCopy()}
              aria-label={t('team.invites.token.copy')}
            >
              {copied ? <Check size={15} aria-hidden="true" /> : <Copy size={15} aria-hidden="true" />}
              {copied ? t('team.invites.token.copied') : t('team.invites.token.copy')}
            </Button>
          </div>
          <p className="mt-2 text-[12px] text-muted">
            {t('team.invites.token.expiresNote', { time: relativeTime(result.expiresAt) })}
          </p>
        </div>
      </div>
    </SlideOver>
  );
}
