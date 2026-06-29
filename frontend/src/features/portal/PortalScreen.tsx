// Care Partner self-service portal (/portal). The partner sees their OWN wallet
// buckets, their referral links (generate via slide-over), and can book on behalf
// of a referred patient (slide-over; triggers a patient consent OTP). All data is
// the partner's own — the server resolves broker_id from the JWT (no id anywhere).
// Customer-facing term is "Care Partner" (MCI 6.4) — never "broker".
//
// This route is rendered from the backend-driven nav like every other screen. If
// the /me/menus tree has no node for it (the live backend hasn't seeded one yet),
// the route still resolves; the nav gap is flagged to the orchestrator rather than
// worked around with a role check.

import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { WalletSummary } from './components/WalletSummary';
import { ReferralLinks } from './components/ReferralLinks';

export function PortalScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
            {t('portal.title')}
          </h1>
          <p className="mt-1 text-[13px] text-muted">{t('portal.subtitle')}</p>
        </div>
        {can('commission.broker.create_booking_self') ? (
          <Button variant="primary" size="md" onClick={() => openPanel({ type: 'bookOnBehalf' })}>
            {t('portal.behalf.cta')}
          </Button>
        ) : null}
      </header>

      <WalletSummary />
      <ReferralLinks />
    </section>
  );
}
