// Left sidebar (matches image.png): logo + RECEPTION DESK eyebrow, org switcher,
// the backend-driven WORKSPACE nav, and the bottom block (WhatsApp LIVE pill,
// Settings, profile). The nav tree is rendered from useMenus() — NOT hardcoded,
// NEVER role-branched. Active route = teal. Badges come from the batched
// useBadges() poll keyed by badgeSource.

import { Link, useNavigate, useRouterState } from '@tanstack/react-router';
import { LogOut, Settings as SettingsIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { Skeleton } from '@/components/ui/Skeleton';
import { iconForKey } from '@/components/ui/icons';
import { useBadges, useMenus } from '@/features/navigation/api';
import { useLogout } from '@/features/auth/api';
import { ORGS } from '@/lib/data';
import { USE_REAL_API } from '@/lib/backend/flag';
import { useSession } from '@/stores/session';
import { useUI } from '@/stores/ui';
import type { MenuNode } from '@/lib/mock/contracts';

export function Sidebar({ onNavigate }: { onNavigate?: () => void } = {}) {
  const { t, i18n } = useTranslation();
  const { data: menus, isLoading, isError } = useMenus();
  const { data: badges } = useBadges();
  const orgId = useUI((s) => s.orgId);
  const setOrg = useUI((s) => s.setOrg);
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const isHindi = i18n.language === 'hi';
  const navigate = useNavigate();
  const user = useSession((s) => s.user);
  const tenantId = useSession((s) => s.tenantId);
  // The real active tenant's display name (session source of truth). Used by the org switcher in
  // real-API mode instead of the mock ORGS list. (#59)
  const activeTenantName = user?.tenants.find((tn) => tn.tenantId === tenantId)?.displayName;
  // The signed-in user's active-tenant role names, joined for the profile chip.
  // Empty + no active tenant (live mode) → platform admin; empty otherwise → the
  // generic bilingual fallback (keeps hi working). Display only.
  const roleNames = (user?.roles ?? []).map((r) => r.name).filter(Boolean);
  const roleLabel =
    roleNames.length > 0
      ? roleNames.join(' · ')
      : USE_REAL_API && !tenantId
        ? t('app.profileRolePlatform')
        : t('app.profileRole');
  const doLogout = useLogout();

  const onSignOut = async () => {
    await doLogout.mutateAsync();
    await navigate({ to: '/login', search: { redirect: undefined } });
  };

  // Fills its container — width/positioning is owned by AppShell (static rail on
  // desktop, slide-in drawer on mobile). onNavigate closes the mobile drawer.
  return (
    <aside className="flex h-full w-full flex-col border-r border-line bg-surface">
      {/* Brand */}
      <div className="flex items-center gap-2.5 px-4 py-4">
        <span
          aria-hidden="true"
          className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] bg-primary text-bg"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
            <path d="M5 12h4l2-7 4 14 2-7h2" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </span>
        <div className="leading-tight">
          <p className="text-sm font-semibold text-ink">DocSlot</p>
          <p className="text-[10px] font-semibold uppercase tracking-wider text-muted-2">{t('app.eyebrow')}</p>
        </div>
      </div>

      {/* Org switcher — REAL mode shows the session's active tenant (read-only): tenant-switching
          needs a token re-mint flow that isn't wired yet AND the gateway strips X-Tenant-Id, so a
          dropdown of fake/mock orgs disconnected from the JWT tenant would mislead. MOCK mode keeps
          the demo org picker. (#59) */}
      <div className="px-3 pb-3">
        <label htmlFor="org-switcher" className="sr-only">
          {t('topbar.breadcrumbReception')}
        </label>
        {USE_REAL_API ? (
          <div
            id="org-switcher"
            className="w-full truncate rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-2.5 py-2 text-[13px] font-medium text-ink"
            title={activeTenantName ?? undefined}
          >
            {activeTenantName ?? '—'}
          </div>
        ) : (
          <select
            id="org-switcher"
            value={orgId}
            onChange={(e) => setOrg(e.target.value)}
            className="w-full rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-2.5 py-2 text-[13px] font-medium text-ink outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
          >
            {ORGS.map((o) => (
              <option key={o.id} value={o.id}>
                {o.name}
              </option>
            ))}
          </select>
        )}
      </div>

      {/* Workspace nav — data-driven */}
      <nav aria-label={t('app.workspace')} className="flex-1 overflow-y-auto px-3">
        <p className="px-1 py-2 text-[10px] font-semibold uppercase tracking-wider text-muted-2">{t('app.workspace')}</p>
        {isLoading ? (
          <div className="flex flex-col gap-1.5 px-1">
            {Array.from({ length: 7 }).map((_, i) => (
              <Skeleton key={i} className="h-9 w-full" />
            ))}
          </div>
        ) : isError || !menus ? (
          <p className="px-1 text-[13px] text-muted">{t('error.genericTitle')}</p>
        ) : (
          <ul className="flex flex-col gap-0.5">
            {menus
              // Settings has a fixed home in the bottom utility block (below), like the
              // WhatsApp pill and profile — so it is not repeated in the scrollable list.
              .filter((node) => node.key !== 'settings')
              .map((node) => (
                <NavItem
                  key={node.key}
                  node={node}
                  pathname={pathname}
                  badges={badges}
                  isHindi={isHindi}
                  onNavigate={onNavigate}
                />
              ))}
          </ul>
        )}
      </nav>

      {/* Bottom block */}
      <div className="mt-auto border-t border-line px-3 py-3">
        <div className="flex items-center gap-2 rounded-[var(--radius-sm)] bg-whatsapp-soft px-2.5 py-2">
          <span className="h-2 w-2 rounded-full bg-whatsapp" aria-hidden="true" />
          <span className="flex-1 text-[13px] font-medium text-whatsapp-ink">{t('agent.label')}</span>
          <span className="text-[10px] font-bold uppercase tracking-wider text-whatsapp-ink">{t('agent.live')}</span>
        </div>

        <Link
          to="/settings"
          onClick={onNavigate}
          aria-current={pathname === '/settings' || pathname.startsWith('/settings/') ? 'page' : undefined}
          className={[
            'mt-1 flex items-center gap-2.5 rounded-[var(--radius-sm)] px-2.5 py-2 text-[13px] transition-colors',
            pathname === '/settings' || pathname.startsWith('/settings/')
              ? 'bg-primary-soft text-primary'
              : 'text-ink hover:bg-surface-sunk',
          ].join(' ')}
        >
          <SettingsIcon size={16} aria-hidden="true" />
          {t('app.settings')}
        </Link>

        <div className="mt-1 flex items-center gap-2.5 rounded-[var(--radius-sm)] px-2.5 py-2">
          <Avatar name={user?.fullName ?? 'DocSlot'} size="sm" />
          <div className="min-w-0 flex-1 leading-tight">
            <p className="truncate text-[13px] font-medium text-ink">{user?.fullName ?? '—'}</p>
            {/* Real role names from /me (active-tenant roles). Join multiple with " · ";
                fall back to the bilingual generic label when the API returns none. */}
            <p className="truncate text-[11px] text-muted">{roleLabel}</p>
          </div>
          <button
            type="button"
            aria-label={t('auth.signOut')}
            onClick={() => void onSignOut()}
            disabled={doLogout.isPending}
            className="rounded p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:opacity-50"
          >
            <LogOut size={15} aria-hidden="true" />
          </button>
        </div>
      </div>
    </aside>
  );
}

function NavItem({
  node,
  pathname,
  badges,
  isHindi,
  onNavigate,
}: {
  node: MenuNode;
  pathname: string;
  badges: Record<string, number> | undefined;
  isHindi: boolean;
  onNavigate?: () => void;
}) {
  // icon/labelHi are nullable on the wire; fall back to a default glyph and to
  // the English label when the Hindi string is absent.
  const Icon = iconForKey(node.icon ?? '');
  const label = (isHindi ? node.labelHi : node.label) ?? node.label;
  const badge = node.badgeSource ? badges?.[node.badgeSource] : undefined;

  // Routeless GROUP node (section header) → a label that renders its children indented.
  // A group has no page of its own, so its children are the actual navigable items.
  if (node.isSectionHeader || !node.route) {
    return (
      <>
        <li className="px-1 py-1 text-[10px] font-semibold uppercase tracking-wider text-muted-2">{label}</li>
        {node.children && node.children.length > 0 ? (
          <li>
            <ul className="ml-3 flex flex-col gap-0.5 border-l border-line pl-2">
              {node.children.map((child) => (
                <NavItem
                  key={child.key}
                  node={child}
                  pathname={pathname}
                  badges={badges}
                  isHindi={isHindi}
                  onNavigate={onNavigate}
                />
              ))}
            </ul>
          </li>
        ) : null}
      </>
    );
  }

  // Routed node → a single FLAT leaf. Its children (e.g. Settings' Organization/Users,
  // Care Partners' Directory/Payouts) are DELIBERATELY not nested here: the destination
  // page presents them as tabs / a section-rail, so the sidebar stays one level deep and
  // never duplicates a page's own sub-navigation. Active on the node's own path OR any
  // sub-path, so a parent stays highlighted while you're on one of its in-page sections
  // (e.g. /patients/$id, /settings/booking-rules).
  const active = pathname === node.route || pathname.startsWith(node.route + '/');
  return (
    <li>
      <Link
        to={node.route}
        onClick={onNavigate}
        className={[
          'flex items-center gap-2.5 rounded-[var(--radius-sm)] px-2.5 py-2 text-sm transition-colors',
          active ? 'bg-primary text-bg' : 'text-ink hover:bg-surface-sunk',
          isHindi ? 'deva' : '',
        ].join(' ')}
      >
        <Icon size={17} aria-hidden="true" />
        <span className="flex-1 truncate">{label}</span>
        {badge ? (
          <span
            className={[
              'inline-flex min-w-5 items-center justify-center rounded-full px-1.5 text-[11px] font-semibold',
              active ? 'bg-bg/20 text-bg' : 'bg-accent-soft text-accent',
            ].join(' ')}
          >
            {badge}
          </span>
        ) : null}
      </Link>
    </li>
  );
}
