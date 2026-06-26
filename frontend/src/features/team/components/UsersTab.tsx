// Users list (Team & Roles). Skeleton + empty + error. Each row opens the
// manage-user slide-over. PHI: no raw phone — the list DTO carries maskedPhone
// only (we don't even render it here; email + roles + status suffice).

import { useTranslation } from 'react-i18next';
import { ChevronRight } from 'lucide-react';
import { Avatar } from '@/components/ui/Avatar';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { dateTime } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useTenantUsers } from '../api';
import type { UserListItem } from '@/lib/mock/contracts';

export function UsersTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useTenantUsers();
  const openPanel = useUI((s) => s.openPanel);

  // Managers open the manage-access panel; read-only viewers open the
  // effective-access viewer. Either way the row is interactive.
  const canManage = can('tenant.roles.assign') || can('platform.overrides.grant');
  const canViewAccess = can('tenant.users.read');
  const interactive = canManage || canViewAccess;

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
              <Skeleton className="h-10 w-10 rounded-full" />
              <div className="flex flex-1 flex-col gap-2">
                <Skeleton className="h-3 w-40" />
                <Skeleton className="h-3 w-24" />
              </div>
              <Skeleton className="h-5 w-16" />
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
    <Card>
      <ul className="flex flex-col">
        {data.map((u) => (
          <UserRow
            key={u.userId}
            user={u}
            interactive={interactive}
            onOpen={() =>
              openPanel(
                canManage
                  ? { type: 'manageUser', userId: u.userId }
                  : { type: 'effectiveAccess', userId: u.userId },
              )
            }
          />
        ))}
      </ul>
    </Card>
  );
}

function UserRow({
  user,
  interactive,
  onOpen,
}: {
  user: UserListItem;
  interactive: boolean;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  const inner = (
    <>
      <Avatar name={user.fullName} size="md" />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-sm font-medium text-ink">{user.fullName}</span>
          {user.mfaEnabled ? (
            <span className="rounded-full bg-info-soft px-1.5 py-0.5 text-[10px] font-medium text-info">
              {t('team.mfaOn')}
            </span>
          ) : null}
        </div>
        <p className="truncate text-[12px] text-muted">{user.email}</p>
      </div>

      <div className="hidden flex-1 flex-wrap gap-1.5 sm:flex">
        {user.roles.map((r) => (
          <span
            key={r.userTenantRoleId}
            className="inline-flex items-center gap-1 rounded-full bg-surface-sunk px-2 py-0.5 text-[11px] text-ink"
          >
            {r.name}
            {r.isPrimary ? <span className="text-[9px] uppercase text-muted-2">{t('team.primary')}</span> : null}
          </span>
        ))}
      </div>

      <div className="hidden w-28 shrink-0 text-right md:block">
        <span
          className={[
            'inline-flex items-center gap-1 text-[12px]',
            user.isActive ? 'text-primary' : 'text-muted-2',
          ].join(' ')}
        >
          <span className={`h-1.5 w-1.5 rounded-full ${user.isActive ? 'bg-primary' : 'bg-muted-2'}`} aria-hidden="true" />
          {user.isActive ? t('team.statusActive') : t('team.statusInactive')}
        </span>
        <p className="mono mt-0.5 text-[11px] text-muted-2">
          {user.lastLoginAt ? dateTime(user.lastLoginAt) : t('team.neverLoggedIn')}
        </p>
      </div>

      {interactive ? <ChevronRight size={16} className="shrink-0 text-muted-2" aria-hidden="true" /> : null}
    </>
  );

  const rowClass = 'flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left last:border-0';
  return (
    <li>
      {interactive ? (
        <button
          type="button"
          onClick={onOpen}
          className={`${rowClass} transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary`}
        >
          {inner}
        </button>
      ) : (
        <div className={rowClass}>{inner}</div>
      )}
    </li>
  );
}
