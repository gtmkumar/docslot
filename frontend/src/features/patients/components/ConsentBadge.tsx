// Consent status badge (token-driven). granted=teal, requested/expired=warn,
// denied/revoked=danger. Always text — never color alone.

import { useTranslation } from 'react-i18next';
import { ShieldCheck, ShieldX } from 'lucide-react';
import type { ConsentStatus } from '@/lib/mock/contracts';

const TONE: Record<ConsentStatus, string> = {
  granted: 'bg-primary-soft text-primary',
  requested: 'bg-warn-soft text-warn',
  expired: 'bg-warn-soft text-warn',
  denied: 'bg-danger-soft text-danger',
  revoked: 'bg-danger-soft text-danger',
};

export function ConsentBadge({ status }: { status: ConsentStatus }) {
  const { t } = useTranslation();
  const Icon = status === 'granted' ? ShieldCheck : ShieldX;
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[12px] font-medium ${TONE[status]}`}>
      <Icon size={13} aria-hidden="true" />
      {t(`clinical.consent.${status}`)}
    </span>
  );
}
