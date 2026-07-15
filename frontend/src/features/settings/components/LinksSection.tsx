// Links section — navigational cards to the settings surfaces that live on their own
// screens: Team & roles (always) and Care Partners commission rules (only when the
// caller holds commission.rules.read — an in-memory can() check, never a role branch).
// These also back the seeded nav children /settings/users → /team and /settings/commission
// → /care-partners (handled as router redirects). Zero hex — tokens only.

import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { ChevronRight, Handshake, ShieldCheck } from 'lucide-react';
import { usePermissions } from '@/lib/permissions';
import { SectionCard } from './SectionCard';

export function LinksSection() {
  const { t } = useTranslation();
  const { can } = usePermissions();

  return (
    <SectionCard
      anchorId="team"
      icon={<ShieldCheck size={16} aria-hidden="true" />}
      title={t('settings.links.title')}
      caption={t('settings.links.caption')}
    >
      <div className="flex flex-col gap-2">
        <LinkCard
          to="/team"
          icon={<ShieldCheck size={16} aria-hidden="true" />}
          title={t('settings.links.team')}
          description={t('settings.links.teamSub')}
        />
        {can('commission.rules.read') ? (
          <LinkCard
            to="/care-partners"
            icon={<Handshake size={16} aria-hidden="true" />}
            title={t('settings.links.carePartners')}
            description={t('settings.links.carePartnersSub')}
          />
        ) : null}
      </div>
    </SectionCard>
  );
}

function LinkCard({
  to,
  icon,
  title,
  description,
}: {
  to: '/team' | '/care-partners';
  icon: React.ReactNode;
  title: string;
  description: string;
}) {
  return (
    <Link
      to={to}
      className="flex items-center gap-3 rounded-[var(--radius-sm)] border border-line px-3 py-2.5 transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
    >
      <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-surface-sunk text-muted">
        {icon}
      </span>
      <span className="min-w-0 flex-1">
        <span className="block text-[13px] font-medium text-ink">{title}</span>
        <span className="mt-0.5 block text-[12px] text-muted">{description}</span>
      </span>
      <ChevronRight size={16} aria-hidden="true" className="shrink-0 text-muted-2" />
    </Link>
  );
}
