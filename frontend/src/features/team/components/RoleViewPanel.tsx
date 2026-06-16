// Role view slide-over (read-only). Shows a role's granted permissions; system
// roles are explicitly read-only. Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { ShieldAlert } from 'lucide-react';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { useRolePermissions, usePermissionRegistry, useRoles } from '../api';

export function RoleViewPanel({ roleId, open, onClose }: { roleId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: roles } = useRoles();
  const { data: grantedKeys, isLoading, isError, refetch } = useRolePermissions(roleId);
  const { data: registry } = usePermissionRegistry();
  const role = roles?.find((r) => r.roleId === roleId);

  const defFor = (key: string) => registry?.find((p) => p.permissionKey === key);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.roleView.eyebrow')}
      title={role?.name ?? t('team.roleView.eyebrow')}
    >
      <div className="flex flex-col gap-4">
        <div className="flex items-center gap-2">
          <span className="mono text-[12px] text-muted">{role?.roleKey}</span>
          {role ? (
            <span
              className={[
                'rounded-full px-2 py-0.5 text-[11px]',
                role.isSystem ? 'bg-surface-sunk text-muted' : 'bg-primary-soft text-primary',
              ].join(' ')}
            >
              {role.isSystem ? t('team.systemRole') : t('team.customRole')}
            </span>
          ) : null}
        </div>

        {role?.isSystem ? <p className="text-[12px] text-muted">{t('team.roleView.readOnly')}</p> : null}

        <section>
          <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
            {t('team.roleView.permissionsHeading')}
          </h3>

          {isError ? (
            <EmptyState title={t('error.genericTitle')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
          ) : isLoading || !grantedKeys ? (
            <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
              {Array.from({ length: 6 }).map((_, i) => (
                <Skeleton key={i} className="h-7 w-full" />
              ))}
            </div>
          ) : grantedKeys.length === 0 ? (
            <EmptyState title={t('team.emptyRolesTitle')} />
          ) : (
            <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
              {grantedKeys.map((key) => {
                const def = defFor(key);
                return (
                  <li key={key} className="flex items-center gap-2 px-3 py-1.5">
                    <span className="mono flex-1 truncate text-[12px] text-ink">{key}</span>
                    {def?.isDangerous ? (
                      <span className="inline-flex items-center gap-1 rounded-full bg-warn-soft px-2 py-0.5 text-[10px] font-medium text-warn">
                        <ShieldAlert size={11} aria-hidden="true" />
                        {t('team.roleView.dangerous')}
                      </span>
                    ) : null}
                  </li>
                );
              })}
            </ul>
          )}
        </section>
      </div>
    </SlideOver>
  );
}
