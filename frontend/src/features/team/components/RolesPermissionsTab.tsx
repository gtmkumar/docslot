// Roles & permissions — the mockup's inline master-detail with three sub-tabs
// (#83): Roles & privileges | Per-user overrides | Effective access.
//
//  - Roles & privileges: role list (left) + the always-on privilege matrix pane
//    (right), reusing the shared RoleMatrixView (converted from a slide-over). The
//    catalog toolbar (+ Module / + Permission) rides above it, gated on
//    platform.permissions.manage.
//  - Per-user overrides: a tenant-wide overrides list has no backend yet (#85), so
//    a design-token empty-state points at where per-user editing already lives
//    (People → Manage access → overrides).
//  - Effective access: a person picker that opens the existing, reused
//    effective-access viewer (EffectiveAccessPanel).
//
// Per-role member counts (#84) are not available yet — intentionally omitted.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Boxes, ChevronRight, Eye, KeyRound, Shield, ShieldCheck } from 'lucide-react';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import * as Tabs from '@radix-ui/react-tabs';
import { useRoles, useTenantUsers } from '../api';
import { RoleMatrixView } from './RoleMatrixView';

const subTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function RolesPermissionsTab() {
  const { t } = useTranslation();
  return (
    <Tabs.Root defaultValue="privileges">
      <Tabs.List className="mb-4 flex gap-1 border-b border-line" aria-label={t('team.tabRolesPerms')}>
        <Tabs.Trigger value="privileges" className={subTrigger}>
          {t('team.subRolesPrivileges')}
        </Tabs.Trigger>
        <Tabs.Trigger value="overrides" className={subTrigger}>
          {t('team.subOverrides')}
        </Tabs.Trigger>
        <Tabs.Trigger value="effective" className={subTrigger}>
          {t('team.subEffective')}
        </Tabs.Trigger>
      </Tabs.List>

      <Tabs.Content value="privileges" className="focus-visible:outline-none">
        <RolesPrivilegesSubTab />
      </Tabs.Content>
      <Tabs.Content value="overrides" className="focus-visible:outline-none">
        <OverridesSubTab />
      </Tabs.Content>
      <Tabs.Content value="effective" className="focus-visible:outline-none">
        <EffectiveAccessSubTab />
      </Tabs.Content>
    </Tabs.Root>
  );
}

// ── Roles & privileges: role list (left) + matrix pane (right) ────────────────
function RolesPrivilegesSubTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data: roles, isLoading, isError, refetch } = useRoles();
  const openPanel = useUI((s) => s.openPanel);
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

  // Catalog plane (platform-governed): define new modules / permissions. Gated on
  // platform.permissions.manage — invisible to a tenant owner, who only governs the
  // assignment plane (granting existing permissions to roles).
  const canManageCatalog = can('platform.permissions.manage');
  const catalogToolbar = canManageCatalog ? (
    <div className="flex flex-wrap items-center gap-2">
      <span className="mr-auto text-[12px] text-muted">{t('team.catalog.toolbarLabel')}</span>
      <Button variant="subtle" size="sm" onClick={() => openPanel({ type: 'createModule' })}>
        <Boxes size={14} aria-hidden="true" />
        {t('team.catalog.addModule')}
      </Button>
      <Button variant="subtle" size="sm" onClick={() => openPanel({ type: 'createPermission' })}>
        <KeyRound size={14} aria-hidden="true" />
        {t('team.catalog.addPermission')}
      </Button>
    </div>
  ) : null;

  if (isError) {
    return (
      <Card>
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      </Card>
    );
  }

  if (isLoading || !roles) {
    return (
      <div className="flex flex-col gap-3">
        {catalogToolbar}
        <div className="grid gap-4 md:grid-cols-[minmax(200px,260px)_1fr]">
          <Card className="p-2" aria-busy="true">
            {Array.from({ length: 5 }).map((_, i) => (
              <Skeleton key={i} className="mb-2 h-10 w-full last:mb-0" />
            ))}
          </Card>
          <Card className="p-4" aria-busy="true">
            <Skeleton className="mb-3 h-4 w-40" />
            <div className="grid grid-cols-2 gap-2">
              {Array.from({ length: 6 }).map((_, i) => (
                <Skeleton key={i} className="h-10 w-full" />
              ))}
            </div>
          </Card>
        </div>
      </div>
    );
  }

  if (roles.length === 0) {
    return (
      <div className="flex flex-col gap-3">
        {catalogToolbar}
        <Card>
          <EmptyState title={t('team.emptyRolesTitle')} description={t('team.emptyRolesBody')} />
        </Card>
      </div>
    );
  }

  // Default-select the first role without an effect (Compiler-friendly): the
  // effective selection falls back to roles[0] until the user picks another.
  const effectiveSelected = selectedId ?? roles[0].roleId;

  return (
    <div className="flex flex-col gap-3">
      {catalogToolbar}
      <div className="grid gap-4 md:grid-cols-[minmax(200px,260px)_1fr]">
        <Card className="overflow-hidden md:self-start">
          <ul className="flex flex-col" aria-label={t('team.rolesListLabel')}>
            {roles.map((role) => {
              const selected = role.roleId === effectiveSelected;
              return (
                <li key={role.roleId}>
                  <button
                    type="button"
                    aria-current={selected ? 'true' : undefined}
                    onClick={() => setSelectedId(role.roleId)}
                    className={[
                      'flex w-full items-center gap-2.5 border-b border-line px-3 py-2.5 text-left transition-colors last:border-0',
                      'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary',
                      selected ? 'bg-primary-soft' : 'hover:bg-surface-sunk',
                    ].join(' ')}
                  >
                    <span
                      aria-hidden="true"
                      className={[
                        'flex h-7 w-7 shrink-0 items-center justify-center rounded-[var(--radius-sm)]',
                        selected ? 'bg-primary text-bg' : 'bg-surface-sunk text-muted',
                      ].join(' ')}
                    >
                      {role.isSystem ? <ShieldCheck size={15} /> : <Shield size={15} />}
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[13px] font-medium text-ink">{role.name}</span>
                      <span className="mono block truncate text-[10px] text-muted-2">{role.roleKey}</span>
                    </span>
                    <span
                      className={[
                        'shrink-0 rounded-full px-1.5 py-0.5 text-[10px]',
                        role.isSystem ? 'bg-surface-sunk text-muted' : 'bg-primary-soft text-primary',
                      ].join(' ')}
                    >
                      {role.isSystem ? t('team.systemRole') : t('team.customRole')}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        </Card>

        <Card className="p-4">
          <RoleMatrixView
            roleId={effectiveSelected}
            onDuplicate={(id) => openPanel({ type: 'duplicateRole', roleId: id })}
          />
        </Card>
      </div>
    </div>
  );
}

// ── Per-user overrides: tenant-wide list awaits #85 ───────────────────────────
function OverridesSubTab() {
  const { t } = useTranslation();
  return (
    <Card>
      <EmptyState
        icon={<KeyRound size={28} aria-hidden="true" />}
        title={t('team.overridesTenant.title')}
        description={t('team.overridesTenant.body')}
      />
    </Card>
  );
}

// ── Effective access: person picker → the reused effective-access viewer ───────
function EffectiveAccessSubTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useTenantUsers();
  const openPanel = useUI((s) => s.openPanel);

  if (isError) {
    return (
      <Card>
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      </Card>
    );
  }

  if (isLoading || !data) {
    return (
      <Card>
        <ul className="flex flex-col" role="status" aria-busy="true">
          {Array.from({ length: 4 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
              <Skeleton className="h-9 w-9 rounded-full" />
              <div className="flex flex-1 flex-col gap-2">
                <Skeleton className="h-3 w-40" />
                <Skeleton className="h-3 w-24" />
              </div>
            </li>
          ))}
        </ul>
      </Card>
    );
  }

  if (data.length === 0) {
    return (
      <Card>
        <EmptyState title={t('team.emptyUsersTitle')} description={t('team.emptyUsersBody')} />
      </Card>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <p className="text-[12px] text-muted-2">{t('team.effectivePrompt')}</p>
      <Card>
        <ul className="flex flex-col">
          {data.map((u) => (
            <li key={u.userId}>
              <button
                type="button"
                onClick={() => openPanel({ type: 'effectiveAccess', userId: u.userId })}
                className="flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left transition-colors last:border-0 hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <Avatar name={u.fullName} size="md" />
                <div className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium text-ink">{u.fullName}</span>
                  <span className="block truncate text-[12px] text-muted">{u.email}</span>
                </div>
                <Eye size={15} className="shrink-0 text-muted-2" aria-hidden="true" />
                <ChevronRight size={16} className="shrink-0 text-muted-2" aria-hidden="true" />
              </button>
            </li>
          ))}
        </ul>
      </Card>
    </div>
  );
}
