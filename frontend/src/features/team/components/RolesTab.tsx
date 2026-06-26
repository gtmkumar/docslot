// Roles list (Team & Roles). System + custom roles; row opens the read-only
// role-view panel (system roles can't be edited). Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { ChevronRight, Shield, ShieldCheck } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { useUI } from '@/stores/ui';
import { useRoles } from '../api';

export function RolesTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useRoles();
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
  }

  if (data.length === 0) {
    return (
      <Card>
        <EmptyState title={t('team.emptyRolesTitle')} description={t('team.emptyRolesBody')} />
      </Card>
    );
  }

  return (
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
