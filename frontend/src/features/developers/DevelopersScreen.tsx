// Developer / API Platform portal (/developers). Four tabs: API clients,
// Scopes, Webhooks, Request logs. Built on Radix Tabs. Management actions gate
// on `platform.api_clients.manage` (the only platform_api permission key in the
// SQL seed — finer-grained keys flagged to the orchestrator). Backend-driven nav
// is untouched; this screen never branches on role names.

import * as Tabs from '@radix-ui/react-tabs';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { ClientsTab } from './components/ClientsTab';
import { ScopesTab } from './components/ScopesTab';
import { WebhooksTab } from './components/WebhooksTab';
import { LogsTab } from './components/LogsTab';

const tabTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function DevelopersScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const canManage = can('platform.api_clients.manage');

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <header>
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('developers.title')}
        </h1>
        <p className="mt-1 text-[13px] text-muted">{t('developers.subtitle')}</p>
      </header>

      <Tabs.Root defaultValue="clients">
        <div className="flex items-center justify-between gap-2 border-b border-line">
          <Tabs.List className="flex min-w-0 flex-1 gap-1 overflow-x-auto" aria-label={t('developers.title')}>
            <Tabs.Trigger value="clients" className={tabTrigger}>
              {t('developers.tabClients')}
            </Tabs.Trigger>
            <Tabs.Trigger value="scopes" className={tabTrigger}>
              {t('developers.tabScopes')}
            </Tabs.Trigger>
            <Tabs.Trigger value="webhooks" className={tabTrigger}>
              {t('developers.tabWebhooks')}
            </Tabs.Trigger>
            <Tabs.Trigger value="logs" className={tabTrigger}>
              {t('developers.tabLogs')}
            </Tabs.Trigger>
          </Tabs.List>

          <div className="flex items-center gap-2 pb-2">
            <Tabs.Content value="clients" forceMount asChild>
              <span className="data-[state=inactive]:hidden">
                {canManage ? (
                  <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'registerClient' })}>
                    {t('developers.registerClient')}
                  </Button>
                ) : null}
              </span>
            </Tabs.Content>
            <Tabs.Content value="webhooks" forceMount asChild>
              <span className="data-[state=inactive]:hidden">
                {canManage ? (
                  <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'createWebhook' })}>
                    {t('developers.createWebhook')}
                  </Button>
                ) : null}
              </span>
            </Tabs.Content>
          </div>
        </div>

        <Tabs.Content value="clients" className="pt-5 focus-visible:outline-none">
          <ClientsTab canManage={canManage} />
        </Tabs.Content>
        <Tabs.Content value="scopes" className="pt-5 focus-visible:outline-none">
          <ScopesTab />
        </Tabs.Content>
        <Tabs.Content value="webhooks" className="pt-5 focus-visible:outline-none">
          <WebhooksTab canManage={canManage} />
        </Tabs.Content>
        <Tabs.Content value="logs" className="pt-5 focus-visible:outline-none">
          <LogsTab />
        </Tabs.Content>
      </Tabs.Root>
    </section>
  );
}
