// Care Partners console (/care-partners) — staff/admin view. Five tabs: Care
// Partners | Attribution ledger | Commission rules | Payouts | Disputes. Built on
// Radix Tabs. Customer-facing terminology is "Care Partner" everywhere (MCI 6.4).
// Every action gates on a real commission.* key; payout approve and execute are
// separate gated actions. The broker self-service portal is a SEPARATE later
// deliverable (not built here).

import * as Tabs from '@radix-ui/react-tabs';
import { useTranslation } from 'react-i18next';
import { PartnersTab } from './components/PartnersTab';
import { AttributionsTab } from './components/AttributionsTab';
import { RulesTab } from './components/RulesTab';
import { PayoutsTab } from './components/PayoutsTab';
import { DisputesTab } from './components/DisputesTab';

const tabTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function CarePartnersScreen() {
  const { t } = useTranslation();

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <header>
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('commission.title')}
        </h1>
        <p className="mt-1 text-[13px] text-muted">{t('commission.subtitle')}</p>
      </header>

      <Tabs.Root defaultValue="partners">
        <div className="border-b border-line">
          <Tabs.List className="flex gap-1 overflow-x-auto" aria-label={t('commission.title')}>
            <Tabs.Trigger value="partners" className={tabTrigger}>
              {t('commission.tabPartners')}
            </Tabs.Trigger>
            <Tabs.Trigger value="attributions" className={tabTrigger}>
              {t('commission.tabAttributions')}
            </Tabs.Trigger>
            <Tabs.Trigger value="rules" className={tabTrigger}>
              {t('commission.tabRules')}
            </Tabs.Trigger>
            <Tabs.Trigger value="payouts" className={tabTrigger}>
              {t('commission.tabPayouts')}
            </Tabs.Trigger>
            <Tabs.Trigger value="disputes" className={tabTrigger}>
              {t('commission.tabDisputes')}
            </Tabs.Trigger>
          </Tabs.List>
        </div>

        <Tabs.Content value="partners" className="pt-5 focus-visible:outline-none">
          <PartnersTab />
        </Tabs.Content>
        <Tabs.Content value="attributions" className="pt-5 focus-visible:outline-none">
          <AttributionsTab />
        </Tabs.Content>
        <Tabs.Content value="rules" className="pt-5 focus-visible:outline-none">
          <RulesTab />
        </Tabs.Content>
        <Tabs.Content value="payouts" className="pt-5 focus-visible:outline-none">
          <PayoutsTab />
        </Tabs.Content>
        <Tabs.Content value="disputes" className="pt-5 focus-visible:outline-none">
          <DisputesTab />
        </Tabs.Content>
      </Tabs.Root>
    </section>
  );
}
