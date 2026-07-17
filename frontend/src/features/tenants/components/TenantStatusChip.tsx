// Tenant lifecycle status chip (REACT_SKILL pattern 12: always icon + text + colour,
// never colour alone). Token-driven, zero hex. `active` reads positive (primary);
// `suspended` is an attention state (warn — terracotta/accent stays reserved for
// genuinely destructive states per the healthcare palette guard). Unknown status
// degrades to a neutral chip with its raw label. Shared by the list (TenantsScreen) and
// the manage slide-over (ManageTenantPanel), which is why it's its own component.

import { CircleCheck, CircleDot, CirclePause } from 'lucide-react';
import { useTranslation } from 'react-i18next';

export function TenantStatusChip({ status }: { status?: string | null }) {
  const { t } = useTranslation();
  const value = status ?? 'unknown';
  const cfg =
    value === 'active'
      ? { icon: CircleCheck, className: 'bg-primary-soft text-primary border-primary-soft', label: t('tenants.status.active') }
      : value === 'suspended'
        ? { icon: CirclePause, className: 'bg-warn-soft text-warn border-warn-soft', label: t('tenants.status.suspended') }
        : { icon: CircleDot, className: 'bg-surface-sunk text-muted border-line', label: value };
  const Icon = cfg.icon;
  return (
    <span
      className={`inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-[12px] font-medium ${cfg.className}`}
    >
      <Icon size={12} aria-hidden="true" />
      {cfg.label}
    </span>
  );
}
