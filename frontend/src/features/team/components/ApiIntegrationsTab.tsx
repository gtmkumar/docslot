// "API & integrations" tab of the unified Team console (#81). It COMPOSES the
// existing Developer-portal sub-tab bodies (Clients / Scopes / Webhooks / Logs)
// from `@/features/developers` — reused, not re-implemented — inside a nested
// Radix Tabs, so all the list logic (queries, panels, states) lives in one place
// and the standalone `/developers` screen keeps working unchanged.
//
// Cross-feature import is intentional here: the epic (#80) folds the developer
// portal into the single Team & roles console, and the orchestrator directed
// reuse of these bodies rather than duplication. Management actions gate on
// `platform.api_clients.manage` (the only platform_api key in the seed), exactly
// as the standalone screen does.

import * as Tabs from '@radix-ui/react-tabs';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { ClientsTab } from '@/features/developers/components/ClientsTab';
import { ScopesTab } from '@/features/developers/components/ScopesTab';
import { WebhooksTab } from '@/features/developers/components/WebhooksTab';
import { LogsTab } from '@/features/developers/components/LogsTab';

const subTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function ApiIntegrationsTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const canManage = can('platform.api_clients.manage');

  return (
    <Tabs.Root defaultValue="clients">
      <div className="flex items-center justify-between gap-2 border-b border-line">
        <Tabs.List className="flex min-w-0 flex-1 gap-1 overflow-x-auto" aria-label={t('team.tabApi')}>
          <Tabs.Trigger value="clients" className={subTrigger}>
            {t('developers.tabClients')}
          </Tabs.Trigger>
          <Tabs.Trigger value="scopes" className={subTrigger}>
            {t('developers.tabScopes')}
          </Tabs.Trigger>
          <Tabs.Trigger value="webhooks" className={subTrigger}>
            {t('developers.tabWebhooks')}
          </Tabs.Trigger>
          <Tabs.Trigger value="logs" className={subTrigger}>
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
  );
}
