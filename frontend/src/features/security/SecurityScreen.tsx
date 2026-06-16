// Security & Compliance console (/security) — super_admin / DPO scope. Five
// tabs: Audit integrity | DPDP rights | Breach register | Review queue |
// Encryption keys. Built on Radix Tabs. Every action gates on the real slice-05
// permission keys (in-memory can()); destructive actions are visually marked
// sensitive/irreversible inside their panels. Never branches on role names.

import * as Tabs from '@radix-ui/react-tabs';
import { useTranslation } from 'react-i18next';
import { AuditTab } from './components/AuditTab';
import { DpdpTab } from './components/DpdpTab';
import { BreachesTab } from './components/BreachesTab';
import { ReviewTab } from './components/ReviewTab';
import { KeysTab } from './components/KeysTab';

const tabTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function SecurityScreen() {
  const { t } = useTranslation();

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <header>
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('security.title')}
        </h1>
        <p className="mt-1 text-[13px] text-muted">{t('security.subtitle')}</p>
      </header>

      <Tabs.Root defaultValue="audit">
        <div className="border-b border-line">
          <Tabs.List className="flex gap-1 overflow-x-auto" aria-label={t('security.title')}>
            <Tabs.Trigger value="audit" className={tabTrigger}>
              {t('security.tabAudit')}
            </Tabs.Trigger>
            <Tabs.Trigger value="dpdp" className={tabTrigger}>
              {t('security.tabDpdp')}
            </Tabs.Trigger>
            <Tabs.Trigger value="breaches" className={tabTrigger}>
              {t('security.tabBreaches')}
            </Tabs.Trigger>
            <Tabs.Trigger value="review" className={tabTrigger}>
              {t('security.tabReview')}
            </Tabs.Trigger>
            <Tabs.Trigger value="keys" className={tabTrigger}>
              {t('security.tabKeys')}
            </Tabs.Trigger>
          </Tabs.List>
        </div>

        <Tabs.Content value="audit" className="pt-5 focus-visible:outline-none">
          <AuditTab />
        </Tabs.Content>
        <Tabs.Content value="dpdp" className="pt-5 focus-visible:outline-none">
          <DpdpTab />
        </Tabs.Content>
        <Tabs.Content value="breaches" className="pt-5 focus-visible:outline-none">
          <BreachesTab />
        </Tabs.Content>
        <Tabs.Content value="review" className="pt-5 focus-visible:outline-none">
          <ReviewTab />
        </Tabs.Content>
        <Tabs.Content value="keys" className="pt-5 focus-visible:outline-none">
          <KeysTab />
        </Tabs.Content>
      </Tabs.Root>
    </section>
  );
}
