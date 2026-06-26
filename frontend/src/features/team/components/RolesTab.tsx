// Roles list (Team & Roles). System + custom roles; row opens the read-only
// role-view panel (system roles can't be edited). Skeleton + empty + error.

import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Boxes, ChevronRight, KeyRound, Shield, ShieldCheck } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useRoles } from '../api';

export function RolesTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useRoles();
  const openPanel = useUI((s) => s.openPanel);

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

  let body: ReactNode;
  if (isError) {
    body = (
      <Card>
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      </Card>
    );
  } else if (isLoading || !data) {
    body = (
      <Card>
        <ul className="flex flex-col" role="status" aria-busy="true">
          {Array.from({ length: 4 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
              <Skeleton className="h-8 w-8 rounded-[var(--radius-sm)]" />
              <div className="flex flex-1 flex-col gap-2">
                <Skeleton className="h-3 w-32" />
                <Skeleton className="h-3 w-20" />
              </div>
            </li>
          ))}
        </ul>
      </Card>
    );
  } else if (data.length === 0) {
    body = (
      <Card>
        <EmptyState title={t('team.emptyRolesTitle')} description={t('team.emptyRolesBody')} />
      </Card>
    );
  } else {
    body = (
      <Card>
        <ul className="flex flex-col">
          {data.map((role) => (
            <li key={role.roleId}>
              <button
                type="button"
                onClick={() => openPanel({ type: 'roleMatrix', roleId: role.roleId })}
                className="flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left transition-colors last:border-0 hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <span
                  aria-hidden="true"
                  className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] bg-surface-sunk text-muted"
                >
                  {role.isSystem ? <ShieldCheck size={16} /> : <Shield size={16} />}
                </span>
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate text-sm font-medium text-ink">{role.name}</span>
                    <span className="mono text-[11px] text-muted-2">{role.roleKey}</span>
                  </div>
                </div>
                <span
                  className={[
                    'rounded-full px-2 py-0.5 text-[11px]',
                    role.isSystem ? 'bg-surface-sunk text-muted' : 'bg-primary-soft text-primary',
                  ].join(' ')}
                >
                  {role.isSystem ? t('team.systemRole') : t('team.customRole')}
                </span>
                <ChevronRight size={16} className="shrink-0 text-muted-2" aria-hidden="true" />
              </button>
            </li>
          ))}
        </ul>
      </Card>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      {catalogToolbar}
      {body}
    </div>
  );
}
