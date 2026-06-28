// Users list (Team & Roles). Toolbar (debounced search + status filter) above a
// list with four data states: loading skeleton, error, tenant-has-no-users empty,
// and a DISTINCT filtered-empty state. Each row opens the manage-user slide-over.
//
// PHI: no raw phone — the list DTO carries maskedPhone only (we don't render it
// here; email + roles + status + security chips suffice). Filtering is client-side
// over the loaded list (case-insensitive name/email match); the React Compiler
// memoizes the derived list, so we do NOT hand-write useMemo.

import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronRight, Search } from 'lucide-react';
import { Avatar } from '@/components/ui/Avatar';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { TextInput } from '@/components/ui/Field';
import { dateTime } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useTenantUsers } from '../api';
import type { UserListItem } from '@/lib/mock/contracts';

type StatusFilter = 'all' | 'active' | 'inactive';

/** A user is "locked" when lockedUntil is set AND still in the future. */
function isLocked(user: UserListItem): boolean {
  return Boolean(user.lockedUntil) && new Date(user.lockedUntil as string).getTime() > Date.now();
}

export function UsersTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useTenantUsers();
  const openPanel = useUI((s) => s.openPanel);

  // Toolbar state. `search` is the live input; `debounced` lags it by 200ms so
  // filtering doesn't run on every keystroke. The React Compiler memoizes the
  // derived `filtered` list below — no manual useMemo.
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StatusFilter>('all');
  const debounced = useDebounced(search, 200);

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
      <div className="flex flex-col gap-3">
        <Toolbar
          search={search}
          onSearch={setSearch}
          status={status}
          onStatus={setStatus}
          disabled
        />
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
      </div>
    );
  }

  // Tenant has no users at all — distinct from "filtered out everything".
  if (data.length === 0) {
    return (
      <Card>
        <EmptyState title={t('team.emptyUsersTitle')} description={t('team.emptyUsersBody')} />
      </Card>
    );
  }

  const query = debounced.trim().toLowerCase();
  const filtered = data.filter((u) => {
    const matchesStatus = status === 'all' || (status === 'active' ? u.isActive : !u.isActive);
    const matchesQuery =
      query.length === 0 ||
      u.fullName.toLowerCase().includes(query) ||
      u.email.toLowerCase().includes(query);
    return matchesStatus && matchesQuery;
  });

  const clearFilters = () => {
    setSearch('');
    setStatus('all');
  };

  return (
    <div className="flex flex-col gap-3">
      <Toolbar search={search} onSearch={setSearch} status={status} onStatus={setStatus} />

      {filtered.length === 0 ? (
        <Card>
          <EmptyState
            title={t('team.emptyFilteredTitle')}
            description={t('team.emptyFilteredBody')}
            actionLabel={t('team.clearFilters')}
            onAction={clearFilters}
          />
        </Card>
      ) : (
        <Card>
          <ul className="flex flex-col">
            {filtered.map((u) => (
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
      )}
    </div>
  );
}

// ── Toolbar: debounced search + 3-segment status filter ──────────────────────
function Toolbar({
  search,
  onSearch,
  status,
  onStatus,
  disabled,
}: {
  search: string;
  onSearch: (v: string) => void;
  status: StatusFilter;
  onStatus: (v: StatusFilter) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
      <div className="relative sm:max-w-xs sm:flex-1">
        <Search
          size={15}
          aria-hidden="true"
          className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-2"
        />
        <TextInput
          type="search"
          value={search}
          disabled={disabled}
          onChange={(e) => onSearch(e.target.value)}
          placeholder={t('team.searchPlaceholder')}
          aria-label={t('team.searchPlaceholder')}
          className="pl-9"
        />
      </div>

      <div role="radiogroup" aria-label={t('team.colStatus')} className="flex gap-1.5">
        <StatusToggle active={status === 'all'} onClick={() => onStatus('all')} label={t('team.filterAll')} disabled={disabled} />
        <StatusToggle active={status === 'active'} onClick={() => onStatus('active')} label={t('team.filterActive')} disabled={disabled} />
        <StatusToggle active={status === 'inactive'} onClick={() => onStatus('inactive')} label={t('team.filterInactive')} disabled={disabled} />
      </div>
    </div>
  );
}

// Mirrors ManageUserPanel's ExpiryToggle: token-driven, role="radio" segment.
function StatusToggle({
  active,
  onClick,
  label,
  disabled,
}: {
  active: boolean;
  onClick: () => void;
  label: string;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      disabled={disabled}
      onClick={onClick}
      className={[
        'rounded-[var(--radius-sm)] border px-3 py-1.5 text-[12px] transition-colors disabled:opacity-50',
        active ? 'border-primary bg-primary text-bg' : 'border-line text-ink hover:bg-surface-sunk',
      ].join(' ')}
    >
      {label}
    </button>
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
  const locked = isLocked(user);
  const inner = (
    <>
      <Avatar name={user.fullName} size="md" />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="truncate text-sm font-medium text-ink">{user.fullName}</span>
          {user.mfaEnabled ? (
            <span className="rounded-full bg-info-soft px-1.5 py-0.5 text-[10px] font-medium text-info">
              {t('team.mfaOn')}
            </span>
          ) : null}
          {locked ? (
            <span className="rounded-full bg-warn-soft px-1.5 py-0.5 text-[10px] font-medium text-warn">
              {t('team.lockedChip')}
            </span>
          ) : null}
          {user.mustChangePassword ? (
            <span className="rounded-full bg-surface-sunk px-1.5 py-0.5 text-[10px] font-medium text-muted">
              {t('team.resetPendingChip')}
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

  // Inactive rows are dimmed (opacity utility — the established dimming convention,
  // not a hex literal) so the eye lands on active teammates first.
  const dim = user.isActive ? '' : 'opacity-60';
  const rowClass = `flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left last:border-0 ${dim}`;
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

// Small local debounce hook — keeps the filter off the keystroke hot path. The
// effect resyncs whenever `value` changes; cleanup cancels a pending timer.
function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const id = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(id);
  }, [value, delayMs]);
  return debounced;
}
