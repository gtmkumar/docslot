// WhatsApp Cloud API section — READ-ONLY. Shows the connection status chip, the phone
// number ID and the fixed webhook URL (both mono + copy), and the verified-at date. The
// access token is NEVER present (the DTO omits it) — nothing here reveals a secret. A
// muted note states greetings/templates are platform-managed today. Zero hex — tokens only.

import { useTranslation } from 'react-i18next';
import { Check, Copy, Info, MessageCircle } from 'lucide-react';
import { toast } from 'sonner';
import type { WhatsappSettings } from '@/lib/mock/contracts';
import { dateTime } from '@/lib/format';
import { SectionCard } from './SectionCard';

// The webhook path is a fixed platform route (same for every tenant), not tenant config.
const WEBHOOK_PATH = '/api/v1/whatsapp/webhook';

export function WhatsappSection({ whatsapp }: { whatsapp: WhatsappSettings }) {
  const { t } = useTranslation();

  const statusChip = whatsapp.connected ? (
    <span className="inline-flex items-center gap-1 rounded-full bg-primary-soft px-2 py-0.5 text-[12px] font-medium text-primary">
      <Check size={12} aria-hidden="true" />
      {t('settings.whatsapp.connected')}
    </span>
  ) : (
    <span className="inline-flex items-center gap-1 rounded-full bg-warn-soft px-2 py-0.5 text-[12px] font-medium text-warn">
      <Info size={12} aria-hidden="true" />
      {t('settings.whatsapp.notConnected')}
    </span>
  );

  return (
    <SectionCard
      anchorId="whatsapp"
      icon={<MessageCircle size={16} aria-hidden="true" />}
      title={t('settings.whatsapp.title')}
      caption={t('settings.whatsapp.caption')}
      action={statusChip}
    >
      <dl className="flex flex-col divide-y divide-line">
        <CopyRow
          label={t('settings.whatsapp.phoneNumberId')}
          value={whatsapp.phoneNumberId}
          empty={t('settings.whatsapp.empty')}
        />
        <CopyRow label={t('settings.whatsapp.webhookUrl')} value={WEBHOOK_PATH} empty={t('settings.whatsapp.empty')} />
        <div className="flex items-center justify-between gap-3 py-2.5">
          <dt className="text-[13px] text-muted">{t('settings.whatsapp.verifiedAt')}</dt>
          <dd className="text-[13px] text-ink">
            {whatsapp.verifiedAt ? dateTime(whatsapp.verifiedAt) : t('settings.whatsapp.empty')}
          </dd>
        </div>
      </dl>

      <p className="mt-4 flex items-start gap-1.5 rounded-[var(--radius-sm)] bg-surface-sunk px-3 py-2 text-[12px] text-muted">
        <Info size={13} aria-hidden="true" className="mt-0.5 shrink-0" />
        {t('settings.whatsapp.platformNote')}
      </p>
    </SectionCard>
  );
}

function CopyRow({ label, value, empty }: { label: string; value: string | null; empty: string }) {
  const { t } = useTranslation();
  const onCopy = async () => {
    if (!value) return;
    try {
      await navigator.clipboard.writeText(value);
      toast.success(t('settings.whatsapp.copied'));
    } catch {
      // Clipboard can be blocked (permissions / insecure context) — no-op, no false toast.
    }
  };
  return (
    <div className="flex items-center justify-between gap-3 py-2.5">
      <dt className="text-[13px] text-muted">{label}</dt>
      <dd className="flex min-w-0 items-center gap-2">
        {value ? (
          <>
            <span className="mono truncate text-[13px] text-ink">{value}</span>
            <button
              type="button"
              onClick={() => void onCopy()}
              aria-label={t('settings.whatsapp.copy')}
              className="shrink-0 rounded p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
            >
              <Copy size={14} aria-hidden="true" />
            </button>
          </>
        ) : (
          <span className="text-[13px] text-muted-2">{empty}</span>
        )}
      </dd>
    </div>
  );
}
