// Topbar (matches image.png): org breadcrumb, search input that opens the
// command palette (focus or Cmd/Ctrl+K), and the right-side action cluster.
// "+ New walk-in" (primary teal) opens the newBooking slide-over — gated by the
// in-memory booking.create permission, never a role check.

import { useTranslation } from 'react-i18next';
import { Menu, Search } from 'lucide-react';
import { AppearanceMenu } from './AppearanceMenu';
import { TopbarActions } from './TopbarActions';
import { ORGS } from '@/lib/data';
import { USE_REAL_API } from '@/lib/backend/flag';
import { useSession } from '@/stores/session';
import { useUI } from '@/stores/ui';

export function Topbar({ onOpenPalette, onOpenMenu }: { onOpenPalette: () => void; onOpenMenu: () => void }) {
  const { t } = useTranslation();
  const orgId = useUI((s) => s.orgId);
  const org = ORGS.find((o) => o.id === orgId) ?? ORGS[0];
  // Live mode: the breadcrumb is the SESSION's active tenant (like the Sidebar's
  // org box, #59) — never the mock ORGS list. No active tenant (a platform
  // super_admin) → the platform label, with no "Reception desk" suffix.
  const user = useSession((s) => s.user);
  const tenantId = useSession((s) => s.tenantId);
  const activeTenantName = user?.tenants.find((tn) => tn.tenantId === tenantId)?.displayName;
  const orgName = USE_REAL_API ? (activeTenantName ?? t('topbar.platformScope')) : org.name;
  const isPlatformScope = USE_REAL_API && !activeTenantName;
  const showReceptionSuffix = !isPlatformScope;
  // Platform scope has no patients/bookings to search — the palette only offers actions.
  const searchPlaceholder = t(isPlatformScope ? 'topbar.searchPlaceholderPlatform' : 'topbar.searchPlaceholder');

  return (
    <header className="flex h-14 shrink-0 items-center gap-3 border-b border-line bg-surface px-4 sm:px-5">
      {/* Mobile hamburger → opens the sidebar drawer. */}
      <button
        type="button"
        aria-label={t('nav_aria.openMenu')}
        onClick={onOpenMenu}
        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-[var(--radius-sm)] border border-line-strong text-ink transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary md:hidden"
      >
        <Menu size={16} aria-hidden="true" />
      </button>

      <nav aria-label="Breadcrumb" className="min-w-0 flex-1 md:flex-none">
        <p className="truncate text-[13px] text-muted">
          <span className="font-medium text-ink">{orgName}</span>
          {showReceptionSuffix ? (
            <>
              <span className="mx-1.5 text-muted-2">·</span>
              {t('topbar.breadcrumbReception')}
            </>
          ) : null}
        </p>
      </nav>

      <div className="relative ml-auto hidden max-w-sm flex-1 md:block">
        <Search
          size={15}
          aria-hidden="true"
          className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-2"
        />
        <input
          type="text"
          readOnly
          onFocus={onOpenPalette}
          onClick={onOpenPalette}
          placeholder={searchPlaceholder}
          aria-label={searchPlaceholder}
          className="w-full cursor-pointer rounded-[var(--radius-sm)] border border-line bg-surface-sunk py-2 pl-9 pr-12 text-[13px] text-ink outline-none placeholder:text-muted-2 focus:border-primary focus:ring-2 focus:ring-primary-soft"
        />
        <kbd className="mono pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 rounded border border-line bg-surface px-1.5 py-0.5 text-[10px] text-muted-2">
          ⌘K
        </kbd>
      </div>

      {/* Appearance stays a fixed control; the rest (roster/book-time/new) live in
          TopbarActions which collapses gracefully below lg/sm. */}
      <div className="ml-auto flex shrink-0 items-center gap-2 md:ml-0">
        <AppearanceMenu />
        <TopbarActions />
      </div>
    </header>
  );
}
