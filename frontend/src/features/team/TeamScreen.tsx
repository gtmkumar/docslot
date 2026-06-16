// Team & Roles admin (/team). Two tabs — Users and Roles — built on Radix Tabs.
// Every action gates on a real permission key (in-memory can()): invite →
// tenant.users.create, manage roles → tenant.roles.assign, overrides →
// platform.overrides.grant, create role → platform.roles.manage. Backend-driven
// nav is untouched; this screen never branches on role names.

import * as Tabs from '@radix-ui/react-tabs';
import { useTranslation } from 'react-i18next';
import { UserPlus } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { UsersTab } from './components/UsersTab';
import { RolesTab } from './components/RolesTab';

export function TeamScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);

  const tabTrigger =
    'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
    'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
        {t('team.title')}
      </h1>

      <Tabs.Root defaultValue="users">
        <div className="flex items-center justify-between border-b border-line">
          <Tabs.List className="flex gap-1" aria-label={t('team.title')}>
            <Tabs.Trigger value="users" className={tabTrigger}>
              {t('team.tabUsers')}
            </Tabs.Trigger>
            <Tabs.Trigger value="roles" className={tabTrigger}>
              {t('team.tabRoles')}
            </Tabs.Trigger>
          </Tabs.List>

          {/* Per-tab primary action lives in the tab body to keep the right key
              gating its action; we render both gated buttons here and let CSS
              show the one matching the active tab. */}
          <div className="flex items-center gap-2 pb-2">
            <Tabs.Content value="users" className="m-0" forceMount asChild>
              <span className="data-[state=inactive]:hidden">
                {can('tenant.users.create') ? (
                  <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'inviteUser' })}>
                    <UserPlus size={15} aria-hidden="true" />
                    {t('team.inviteUser')}
                  </Button>
                ) : null}
              </span>
            </Tabs.Content>
            <Tabs.Content value="roles" className="m-0" forceMount asChild>
              <span className="data-[state=inactive]:hidden">
                {can('platform.roles.manage') ? (
                  <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'createRole' })}>
                    {t('team.createRole')}
                  </Button>
                ) : null}
              </span>
            </Tabs.Content>
          </div>
        </div>

        <Tabs.Content value="users" className="pt-5 focus-visible:outline-none">
          <UsersTab />
        </Tabs.Content>
        <Tabs.Content value="roles" className="pt-5 focus-visible:outline-none">
          <RolesTab />
        </Tabs.Content>
      </Tabs.Root>
    </section>
  );
}
