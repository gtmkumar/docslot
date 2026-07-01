// People list (Team & Roles console). Toolbar (debounced search + status filter +
// an All-roles dropdown) above a list with four data states: loading skeleton,
// error, tenant-has-no-users empty, and a DISTINCT filtered-empty state. A muted
// "showing X of Y" count sits above the list.
//
// People-tab reskin (#82), UI-only over the live UserListItemDto (no backend
// change): the current user gets a YOU badge (self id from useSession); each role
// renders a colour-coded, token-based badge (deterministic hue per role key — no
// hex); 2FA shows an explicit On/Off pill; and each row's actions live behind a
// `…` kebab (Manage access / Edit profile / View effective access) instead of a
// whole-row button. SCOPE (branch/department) column + All-branches filter are #90
// (no data yet) — intentionally omitted.
//
// PHI: no raw phone — the list DTO carries maskedPhone only (not rendered here).
// Filtering is client-side over the loaded list; the React Compiler memoizes the
// derived list, so we do NOT hand-write useMemo.

import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Eye, Pencil, Search, UserCog } from 'lucide-react';
import { Avatar } from '@/components/ui/Avatar';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { KebabMenu, type KebabItem } from '@/components/ui/KebabMenu';
import { Skeleton } from '@/components/ui/Skeleton';
import { Select, TextInput } from '@/components/ui/Field';
import { dateTime } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useSession } from '@/stores/session';
import { useUI, type Panel } from '@/stores/ui';
import { useTenantUsers } from '../api';
import type { UserListItem } from '@/lib/mock/contracts';

type StatusFilter = 'all' | 'active' | 'inactive';

/** A user is "locked" when lockedUntil is set AND still in the future. */
function isLocked(user: UserListItem): boolean {
  return Boolean(user.lockedUntil) && new Date(user.lockedUntil as string).getTime() > Date.now();
}

// Deterministic, token-only colour per role key (no hex, no status colours reused
// for meaning). Same key → same hue across renders; distinct keys spread across the
// palette so roles are visually separable at a glance.
const ROLE_COLORS = [
  'bg-primary-soft text-primary',
  'bg-info-soft text-info',
  'bg-warn-soft text-warn',
  'bg-accent-soft text-accent',
  'bg-danger-soft text-danger',
  'bg-surface-sunk text-muted',
] as const;

function roleColorClass(roleKey: string): string {
  let h = 0;
  for (let i = 0; i < roleKey.length; i++) h = (h * 31 + roleKey.charCodeAt(i)) >>> 0;
  return ROLE_COLORS[h % ROLE_COLORS.length];
}

interface RoleOption {
  roleKey: string;
  name: string;
}

export function UsersTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useTenantUsers();
  const openPanel = useUI((s) => s.openPanel);
  const selfId = useSession((s) => s.user?.userId);

  // Toolbar state. `search` is the live input; `debounced` lags it by 200ms so
  // filtering doesn't run on every keystroke. `roleFilter` is a role key or 'all'.
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StatusFilter>('all');
  const [roleFilter, setRoleFilter] = useState<string>('all');
  const debounced = useDebounced(search, 200);

  // Managers act via the kebab (manage access / edit profile); read-only viewers
  // still reach the effective-access viewer.
  const canManage = can('tenant.roles.assign') || can('platform.overrides.grant');
  const canViewAccess = can('tenant.users.read');

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
          roleFilter={roleFilter}
          onRoleFilter={setRoleFilter}
          roleOptions={[]}
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

  // Distinct roles present across the loaded users → the All-roles dropdown options.
  const roleOptions: RoleOption[] = (() => {
    const map = new Map<string, string>();
    for (const u of data) for (const r of u.roles) if (!map.has(r.roleKey)) map.set(r.roleKey, r.name);
    return [...map.entries()]
      .map(([roleKey, name]) => ({ roleKey, name }))
      .sort((a, b) => a.name.localeCompare(b.name));
  })();

  const query = debounced.trim().toLowerCase();
  const filtered = data.filter((u) => {
    const matchesStatus = status === 'all' || (status === 'active' ? u.isActive : !u.isActive);
    const matchesQuery =
      query.length === 0 ||
      u.fullName.toLowerCase().includes(query) ||
      u.email.toLowerCase().includes(query);
    const matchesRole = roleFilter === 'all' || u.roles.some((r) => r.roleKey === roleFilter);
    return matchesStatus && matchesQuery && matchesRole;
  });

  const clearFilters = () => {
    setSearch('');
    setStatus('all');
    setRoleFilter('all');
  };

  return (
    <div className="flex flex-col gap-3">
      <Toolbar
        search={search}
        onSearch={setSearch}
        status={status}
        onStatus={setStatus}
        roleFilter={roleFilter}
        onRoleFilter={setRoleFilter}
        roleOptions={roleOptions}
      />

      <p className="text-[12px] text-muted-2" aria-live="polite">
        {t('team.resultCount', { shown: filtered.length, total: data.length })}
      </p>

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
                isSelf={u.userId === selfId}
                canManage={canManage}
                canViewAccess={canViewAccess}
                openPanel={openPanel}
              />
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

// ── Toolbar: debounced search + All-roles dropdown + 3-segment status filter ────
function Toolbar({
  search,
  onSearch,
  status,
  onStatus,
  roleFilter,
  onRoleFilter,
  roleOptions,
  disabled,
}: {
  search: string;
  onSearch: (v: string) => void;
  status: StatusFilter;
  onStatus: (v: StatusFilter) => void;
  roleFilter: string;
  onRoleFilter: (v: string) => void;
  roleOptions: RoleOption[];
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

      <div className="flex flex-wrap items-center gap-1.5">
        {/* Fixed-width wrapper: Field's Select is w-full, so bound it here rather
            than fight the utility source order with w-auto. */}
        <div className="w-40 shrink-0">
          <Select
            value={roleFilter}
            disabled={disabled}
            onChange={(e) => onRoleFilter(e.target.value)}
            aria-label={t('team.allRoles')}
            className="py-1.5 text-[12px]"
          >
            <option value="all">{t('team.allRoles')}</option>
            {roleOptions.map((r) => (
              <option key={r.roleKey} value={r.roleKey}>
                {r.name}
              </option>
            ))}
          </Select>
        </div>

        <div role="radiogroup" aria-label={t('team.colStatus')} className="flex gap-1.5">
          <StatusToggle active={status === 'all'} onClick={() => onStatus('all')} label={t('team.filterAll')} disabled={disabled} />
          <StatusToggle active={status === 'active'} onClick={() => onStatus('active')} label={t('team.filterActive')} disabled={disabled} />
          <StatusToggle active={status === 'inactive'} onClick={() => onStatus('inactive')} label={t('team.filterInactive')} disabled={disabled} />
        </div>
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
  isSelf,
  canManage,
  canViewAccess,
  openPanel,
}: {
  user: UserListItem;
  isSelf: boolean;
  canManage: boolean;
  canViewAccess: boolean;
  openPanel: (panel: Panel) => void;
}) {
  const { t } = useTranslation();
  const locked = isLocked(user);

  // The kebab wraps the existing manage actions. Managers get Manage access
  // (deactivate / reset / assign-role live inside that panel) + Edit profile +
  // View effective access; read-only viewers get just the effective-access viewer.
  const items: KebabItem[] = [];
  if (canManage) {
    items.push({
      key: 'manage',
      label: t('team.manageAccess'),
      icon: <UserCog size={15} />,
      onSelect: () => openPanel({ type: 'manageUser', userId: user.userId }),
    });
    items.push({
      key: 'edit',
      label: t('team.editProfile'),
      icon: <Pencil size={15} />,
      onSelect: () => openPanel({ type: 'editUser', userId: user.userId }),
    });
  }
  if (canViewAccess) {
    items.push({
      key: 'effective',
      label: t('team.viewAccess'),
      icon: <Eye size={15} />,
      onSelect: () => openPanel({ type: 'effectiveAccess', userId: user.userId }),
    });
  }

  // Inactive rows are dimmed (opacity utility — the established dimming convention,
  // not a hex literal) so the eye lands on active teammates first.
  const dim = user.isActive ? '' : 'opacity-60';

  return (
    <li className={`flex items-center gap-3 border-b border-line px-4 py-3 last:border-0 ${dim}`}>
      <Avatar name={user.fullName} size="md" />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="truncate text-sm font-medium text-ink">{user.fullName}</span>
          {isSelf ? (
            <span className="rounded-full bg-primary-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-primary">
              {t('team.youBadge')}
            </span>
          ) : null}
          <span
            className={[
              'rounded-full px-1.5 py-0.5 text-[10px] font-medium',
              user.mfaEnabled ? 'bg-info-soft text-info' : 'bg-surface-sunk text-muted-2',
            ].join(' ')}
          >
            {user.mfaEnabled ? t('team.twoFaOn') : t('team.twoFaOff')}
          </span>
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
            className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] ${roleColorClass(r.roleKey)}`}
          >
            {r.name}
            {r.isPrimary ? <span className="text-[9px] uppercase opacity-70">{t('team.primary')}</span> : null}
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

      {items.length > 0 ? <KebabMenu label={t('team.rowActions', { name: user.fullName })} items={items} /> : null}
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
