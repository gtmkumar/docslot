// Team & roles unified console (/team). Six top-level tabs (#80/#81): People ·
// Roles & permissions · Invites · Audit log · Security · API & integrations —
// built on Radix Tabs. Every tab AND every action gates on a real permission key
// via the in-memory can(): tabs on tenant.users.read / tenant.audit.read /
// tenant.settings.read / platform.api_clients.manage; invite → tenant.users.create;
// create role → platform.roles.manage. Backend-driven nav is untouched; this screen
// never branches on role names.
//
// Phase A honesty: People/Roles/API render live surfaces (existing tab bodies +
// the reused Developer portal). Invites/Audit/Security show design-token empty
// states — their real backends land in later issues (#89/#86/#91), so we render a
// proper "coming soon" state rather than fake data. Export + Bulk import are D4
// (#95): rendered for visual parity but disabled (non-functional) stubs.

import type { ReactNode } from 'react';
import * as Tabs from '@radix-ui/react-tabs';
import { useTranslation } from 'react-i18next';
import { Download, MailPlus, Upload, UserPlus } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { UsersTab } from './components/UsersTab';
import { RolesPermissionsTab } from './components/RolesPermissionsTab';
import { ApiIntegrationsTab } from './components/ApiIntegrationsTab';
import { AuditLogTab } from './components/AuditLogTab';
import { SessionsTab } from './components/SessionsTab';
import { useRoles, useTenantUsers } from './api';

const tabTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

const contentClass = 'pt-5 focus-visible:outline-none';

export function TeamScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);

  // Per-tab / per-action permission gates (in-memory Set lookups — never a network
  // call, never a role-name branch).
  const canReadUsers = can('tenant.users.read');
  const canCreateUsers = can('tenant.users.create');
  const canManageRoles = can('platform.roles.manage');
  const canReadAudit = can('tenant.audit.read');
  const canReadSettings = can('tenant.settings.read');
  const canApi = can('platform.api_clients.manage');

  // Header stats: only what is derivable TODAY. Active users come from the loaded
  // users list; role counts from the roles list (custom = !isSystem). Pending
  // invites (#89) and branches (#90) have no data yet — omitted, not fabricated.
  const { data: users } = useTenantUsers();
  const { data: roles } = useRoles();
  const activeCount = users?.filter((u) => u.isActive).length;
  const roleCount = roles?.length;
  const customCount = roles?.filter((r) => !r.isSystem).length;
  const hasStats = activeCount !== undefined || roleCount !== undefined;

  // Visible tabs, in mockup order. Each carries its own permission gate; the first
  // visible tab becomes the default so the screen never opens on a hidden tab.
  const tabs = (
    [
      canReadUsers ? { value: 'people', label: t('team.tabPeople') } : null,
      canReadUsers ? { value: 'roles', label: t('team.tabRolesPerms') } : null,
      canReadUsers ? { value: 'invites', label: t('team.tabInvites') } : null,
      canReadAudit ? { value: 'audit', label: t('team.tabAudit') } : null,
      canReadSettings ? { value: 'security', label: t('team.tabSecurity') } : null,
      canApi ? { value: 'api', label: t('team.tabApi') } : null,
    ] as ({ value: string; label: string } | null)[]
  ).filter((x): x is { value: string; label: string } => x !== null);

  const defaultTab = tabs[0]?.value;

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <header className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0">
          <h1
            id="screen-heading"
            tabIndex={-1}
            className="text-2xl font-semibold tracking-tight text-ink outline-none"
          >
            {t('team.title')}
          </h1>
          {hasStats ? (
            <p className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[13px] text-muted">
              {activeCount !== undefined ? <span>{t('team.statActive', { count: activeCount })}</span> : null}
              {activeCount !== undefined && roleCount !== undefined ? (
                <span aria-hidden="true" className="text-muted-2">
                  ·
                </span>
              ) : null}
              {roleCount !== undefined ? (
                <span>{t('team.statRoles', { count: roleCount, custom: customCount ?? 0 })}</span>
              ) : null}
            </p>
          ) : null}
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {canCreateUsers ? (
            <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'inviteUser' })}>
              <UserPlus size={15} aria-hidden="true" />
              {t('team.invitePeople')}
            </Button>
          ) : null}
          {/* D4 (#95) stubs — visual parity only, deliberately non-functional. */}
          <Button variant="ghost" size="sm" disabled title={t('team.comingSoon')} aria-disabled="true">
            <Download size={15} aria-hidden="true" />
            {t('team.exportLabel')}
          </Button>
          <Button variant="ghost" size="sm" disabled title={t('team.comingSoon')} aria-disabled="true">
            <Upload size={15} aria-hidden="true" />
            {t('team.bulkImport')}
          </Button>
        </div>
      </header>

      {defaultTab ? (
        <Tabs.Root defaultValue={defaultTab}>
          <div className="flex items-center justify-between gap-2 border-b border-line">
            <Tabs.List className="flex min-w-0 flex-1 gap-1 overflow-x-auto" aria-label={t('team.title')}>
              {tabs.map((tab) => (
                <Tabs.Trigger key={tab.value} value={tab.value} className={tabTrigger}>
                  {tab.label}
                </Tabs.Trigger>
              ))}
            </Tabs.List>

            {/* Per-tab primary action: Create role lives with the Roles tab so the
                right permission gates its action (existing pattern). */}
            {canReadUsers && canManageRoles ? (
              <div className="flex items-center gap-2 pb-2">
                <Tabs.Content value="roles" forceMount asChild>
                  <span className="data-[state=inactive]:hidden">
                    <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'createRole' })}>
                      {t('team.createRole')}
                    </Button>
                  </span>
                </Tabs.Content>
              </div>
            ) : null}
          </div>

          {canReadUsers ? (
            <>
              <Tabs.Content value="people" className={contentClass}>
                <UsersTab />
              </Tabs.Content>
              <Tabs.Content value="roles" className={contentClass}>
                <RolesPermissionsTab />
              </Tabs.Content>
              <Tabs.Content value="invites" className={contentClass}>
                <TabEmpty
                  icon={<MailPlus size={28} aria-hidden="true" />}
                  title={t('team.invitesEmpty.title')}
                  body={t('team.invitesEmpty.body')}
                />
              </Tabs.Content>
            </>
          ) : null}

          {canReadAudit ? (
            <Tabs.Content value="audit" className={contentClass}>
              <AuditLogTab />
            </Tabs.Content>
          ) : null}

          {canReadSettings ? (
            <Tabs.Content value="security" className={contentClass}>
              <SessionsTab />
            </Tabs.Content>
          ) : null}

          {canApi ? (
            <Tabs.Content value="api" className={contentClass}>
              <ApiIntegrationsTab />
            </Tabs.Content>
          ) : null}
        </Tabs.Root>
      ) : (
        <Card>
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} />
        </Card>
      )}
    </section>
  );
}

function TabEmpty({ icon, title, body }: { icon: ReactNode; title: string; body: string }) {
  return (
    <Card>
      <EmptyState icon={icon} title={title} description={body} />
    </Card>
  );
}
