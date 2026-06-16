// AppShell — sidebar + topbar + content Outlet on the cream app background.
// Owns: theme/density data-attributes (driven by the UI store), the command
// palette open state, the single SlideOverHost mount, and route-change focus
// (pattern 14: focus the route h1 on navigation).

import { Outlet, useRouterState } from '@tanstack/react-router';
import { Suspense, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CommandPalette } from '@/components/ui/CommandPalette';
import { ShortcutsSheet } from '@/components/ui/ShortcutsSheet';
import { MobileNavDrawer } from './MobileNavDrawer';
import { RouteFallback } from './RouteFallback';
import { Sidebar } from './Sidebar';
import { SlideOverHost } from './SlideOverHost';
import { Topbar } from './Topbar';
import { usePermissions } from '@/lib/permissions';
import { useShortcuts } from '@/lib/useShortcuts';
import { useMe } from '@/features/auth/api';
import { useUI } from '@/stores/ui';

export function AppShell() {
  const { t } = useTranslation();
  const theme = useUI((s) => s.theme);
  const density = useUI((s) => s.density);
  const openPanel = useUI((s) => s.openPanel);
  const { can } = usePermissions();
  // Bootstrap the signed-in profile once mounted (the route guard already ensured
  // a token exists). /me/menus + /me/permissions bootstrap via their own hooks.
  const { isLoading: meLoading } = useMe();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [cheatsheetOpen, setCheatsheetOpen] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  // Global shortcuts (pattern 11): ? → cheatsheet, N → new walk-in (permission-
  // gated). j/k/Enter for queue rows are registered inside ApprovalQueue.
  useShortcuts({
    '?': () => setCheatsheetOpen(true),
    n: () => {
      if (can('docslot.booking.create')) openPanel({ type: 'newBooking' });
    },
  });

  // Apply theme + density tokens to the document root.
  useEffect(() => {
    const root = document.documentElement;
    root.setAttribute('data-theme', theme);
    root.setAttribute('data-density', density);
  }, [theme, density]);

  // Route change → move focus to the content h1 (a11y, pattern 14) + close the
  // mobile drawer.
  useEffect(() => {
    const h1 = document.querySelector<HTMLElement>('main h1');
    h1?.focus();
    setDrawerOpen(false);
  }, [pathname]);

  // Auth bootstrap gate — brief full-screen loader while /me resolves on entry.
  if (meLoading) {
    return (
      <div
        className="flex h-screen w-full items-center justify-center bg-bg text-muted"
        role="status"
        aria-busy="true"
      >
        <span className="text-[13px]">{t('auth.bootstrapping')}</span>
      </div>
    );
  }

  return (
    <div className="flex h-screen w-full overflow-hidden bg-bg text-ink">
      {/* Desktop static rail (≥md). */}
      <div className="hidden w-60 shrink-0 md:block">
        <Sidebar />
      </div>

      {/* Mobile drawer (<md): Radix Dialog → focus-trapped, Esc + overlay close. */}
      <MobileNavDrawer open={drawerOpen} onClose={() => setDrawerOpen(false)} />

      <div className="flex min-w-0 flex-1 flex-col">
        <Topbar onOpenPalette={() => setPaletteOpen(true)} onOpenMenu={() => setDrawerOpen(true)} />
        <main className="flex-1 overflow-y-auto px-4 py-5 sm:px-6 sm:py-6">
          {/* Suspense boundary for code-split route screens (pattern 13). */}
          <Suspense fallback={<RouteFallback />}>
            <Outlet />
          </Suspense>
        </main>
      </div>

      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <ShortcutsSheet open={cheatsheetOpen} onClose={() => setCheatsheetOpen(false)} />
      <SlideOverHost />
    </div>
  );
}
