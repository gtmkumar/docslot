// One-time admin password-reset link reveal. Shown after a successful
// POST /tenants/{id}/users/{userId}/reset-password — the live resetLink is surfaced
// EXACTLY ONCE, with a copy button (mirrors InvitationTokenPanel).
//
// Email delivery is offline for now, so the admin copies the link and hands it to the
// user directly. The link is a live credential: it arrives via the in-store panel payload
// (NOT the URL, NOT any query cache), this panel renders it, and on close the payload is
// dropped — there is no path to re-fetch it (a fresh reset mints a new one). The panel is
// controlled by local state in ManageUserPanel, never a URL search param.

import { useState } from 'react';
import { Check, Copy, ShieldAlert } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { relativeTime } from '@/lib/format';
import type { AdminResetPasswordResult } from '@/lib/mock/contracts';

interface AdminResetPasswordPanelProps {
  result: AdminResetPasswordResult;
  userName: string;
  open: boolean;
  onClose: () => void;
}

export function AdminResetPasswordPanel({ result, userName, open, onClose }: AdminResetPasswordPanelProps) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(result.resetLink);
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
      eyebrow={t('team.resetPassword.eyebrow')}
      title={t('team.resetPassword.title')}
      description={t('team.resetPassword.ready', { name: userName })}
      footer={
        <Button variant="primary" size="md" onClick={onClose}>
          {t('team.resetPassword.done')}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        <p className="text-[13px] text-muted">{t('team.resetPassword.ready', { name: userName })}</p>

        {/* Delivery is offline — the link is not emailed. The admin copies it and hands
            it to the user directly. No fake "sent ✓". */}
        <div className="flex items-start gap-2 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2.5 text-[12px] text-warn">
          <ShieldAlert size={16} className="mt-0.5 shrink-0" aria-hidden="true" />
          <span>{t('team.resetPassword.dispatchNote')}</span>
        </div>

        <div>
          <label
            htmlFor="reset-link"
            className="mb-1 block text-[11px] font-semibold uppercase tracking-wider text-muted-2"
          >
            {t('team.resetPassword.label')}
          </label>
          <div className="flex items-stretch gap-2">
            <input
              id="reset-link"
              readOnly
              value={result.resetLink}
              onFocus={(e) => e.currentTarget.select()}
              className="mono w-full rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-[12px] text-ink outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
            />
            <Button
              variant="ghost"
              size="md"
              onClick={() => void onCopy()}
              aria-label={t('team.resetPassword.copy')}
            >
              {copied ? <Check size={15} aria-hidden="true" /> : <Copy size={15} aria-hidden="true" />}
              {copied ? t('team.resetPassword.copied') : t('team.resetPassword.copy')}
            </Button>
          </div>
          <p className="mt-2 text-[12px] text-muted">
            {t('team.resetPassword.expiresNote', { time: relativeTime(result.expiresAt) })}
          </p>
        </div>
      </div>
    </SlideOver>
  );
}
