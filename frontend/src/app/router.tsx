// Type-safe TanStack Router (code-based; no file-route plugin configured).
//
// Two layers:
//   root (bare <Outlet/>) ─┬─ /login            → standalone LoginScreen
//                          └─ authed (pathless) → guarded; renders AppShell
//                               ├─ / (Overview), /bookings, /calendar, …
//                               └─ /team (RBAC admin)
//
// The `authed` layout's beforeLoad redirects unauthenticated users to /login
// with a `redirect` back-target. Slide-over state stays URL-addressable via the
// shared `panel`/`id` search params validated on the root (every route inherits
// them, so a deep-linked panel survives refresh — REACT_SKILL pattern 1).

import { lazy } from 'react';
import { createRootRoute, createRoute, createRouter, Outlet, redirect } from '@tanstack/react-router';
import { z } from 'zod';
import { AppShell } from '@/components/layout/AppShell';
import { PlaceholderScreen } from '@/components/layout/PlaceholderScreen';
import { NotFoundScreen } from '@/components/layout/NotFoundScreen';
import { LoginScreen } from '@/features/auth/LoginScreen';
import { useSession } from '@/stores/session';

// Heavy feature screens are code-split: each becomes its own chunk, loaded on
// first navigation. The shell (AppShell) + auth (LoginScreen) stay in the initial
// chunk so the first paint and the login flow have no extra round-trip. The
// Suspense boundary + skeleton fallback live in AppShell's <main> (pattern 13).
const OverviewScreen = lazy(() =>
  import('@/features/dashboard/OverviewScreen').then((m) => ({ default: m.OverviewScreen })),
);
const TeamScreen = lazy(() => import('@/features/team/TeamScreen').then((m) => ({ default: m.TeamScreen })));
const DevelopersScreen = lazy(() =>
  import('@/features/developers/DevelopersScreen').then((m) => ({ default: m.DevelopersScreen })),
);
const SecurityScreen = lazy(() =>
  import('@/features/security/SecurityScreen').then((m) => ({ default: m.SecurityScreen })),
);
const PatientsScreen = lazy(() =>
  import('@/features/patients/PatientsScreen').then((m) => ({ default: m.PatientsScreen })),
);
const PatientRecordsScreen = lazy(() =>
  import('@/features/patients/PatientRecordsScreen').then((m) => ({ default: m.PatientRecordsScreen })),
);
const CarePartnersScreen = lazy(() =>
  import('@/features/commission/CarePartnersScreen').then((m) => ({ default: m.CarePartnersScreen })),
);
const BookingsScreen = lazy(() =>
  import('@/features/bookings/BookingsScreen').then((m) => ({ default: m.BookingsScreen })),
);
const CalendarScreen = lazy(() =>
  import('@/features/calendar/CalendarScreen').then((m) => ({ default: m.CalendarScreen })),
);
const DoctorsScreen = lazy(() =>
  import('@/features/doctors/DoctorsScreen').then((m) => ({ default: m.DoctorsScreen })),
);
const AnalyticsScreen = lazy(() =>
  import('@/features/analytics/AnalyticsScreen').then((m) => ({ default: m.AnalyticsScreen })),
);

// Shared slide-over search params (root-level so all routes carry them).
// `clientSecret` and `deletionCertificate` are intentionally NOT here — each
// carries one-time plaintext (secret / erasure certificate) that must never be
// URL-encoded or survive a refresh. Clinical panels are excluded too (PHI +
// purpose-of-use must never be URL-encoded).
const panelSearchSchema = z.object({
  panel: z
    .enum([
      'conversation', 'manage', 'approve', 'newBooking', 'bookTime', 'addDoctor', 'addPatient',
      'inviteUser', 'manageUser', 'roleView', 'createRole',
      'registerClient', 'manageClient', 'createWebhook', 'webhookForm', 'webhookDeliveries',
      'exportData', 'eraseData', 'reportBreach', 'breakGlass',
      'registerBroker', 'manageBroker', 'createCommissionRule', 'raiseDispute', 'resolveDispute',
    ])
    .optional(),
  id: z.string().optional(),
});

const rootRoute = createRootRoute({
  validateSearch: panelSearchSchema,
  component: () => <Outlet />,
  notFoundComponent: NotFoundScreen,
});

// ── /login (standalone) ──────────────────────────────────────────────────────
const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login',
  validateSearch: (search: Record<string, unknown>) => ({
    redirect: typeof search.redirect === 'string' ? search.redirect : undefined,
  }),
  beforeLoad: () => {
    // Already signed in → skip the login screen.
    if (useSession.getState().isAuthenticated()) throw redirect({ to: '/' });
  },
  component: LoginScreen,
});

// ── authed layout (pathless) — the guarded AppShell ──────────────────────────
const authLayoutRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'authed',
  beforeLoad: ({ location }) => {
    if (!useSession.getState().isAuthenticated()) {
      throw redirect({ to: '/login', search: { redirect: location.pathname } });
    }
  },
  // Unmatched routes under the (authed) shell render the on-brand 404 INSIDE
  // AppShell — sidebar + topbar stay usable. The root-level notFoundComponent is
  // the fallback for genuinely top-level misses (handled before the shell).
  notFoundComponent: NotFoundScreen,
  component: AppShell,
});

const indexRoute = createRoute({ getParentRoute: () => authLayoutRoute, path: '/', component: OverviewScreen });
const bookingsRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/bookings',
  component: BookingsScreen,
});
const calendarRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/calendar',
  component: CalendarScreen,
});
const doctorsRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/doctors',
  component: DoctorsScreen,
});
const patientsRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/patients',
  component: PatientsScreen,
});
// Clinical records live under the patient (PHI surface, purpose-gated in the UI).
const patientRecordsRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/patients/$patientId/records',
  component: PatientRecordsScreen,
});
const analyticsRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/analytics',
  component: AnalyticsScreen,
});
const teamRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/team',
  component: TeamScreen,
});
const developersRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/developers',
  component: DevelopersScreen,
});
const securityRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/security',
  component: SecurityScreen,
});
const carePartnersRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/care-partners',
  component: CarePartnersScreen,
});
const settingsRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/settings',
  component: () => <PlaceholderScreen titleKey="app.settings" />,
});
// Lab Tests — backend nav surfaces this for lab/hospital/diagnostic tenants; the
// full screen lands in a later wave, so the route resolves to a placeholder
// rather than 404 (same pattern as /settings).
const labRoute = createRoute({
  getParentRoute: () => authLayoutRoute,
  path: '/lab',
  component: () => <PlaceholderScreen titleKey="nav.lab" />,
});

const routeTree = rootRoute.addChildren([
  loginRoute,
  authLayoutRoute.addChildren([
    indexRoute,
    bookingsRoute,
    calendarRoute,
    doctorsRoute,
    patientsRoute,
    patientRecordsRoute,
    analyticsRoute,
    teamRoute,
    developersRoute,
    securityRoute,
    carePartnersRoute,
    settingsRoute,
    labRoute,
  ]),
]);

export const router = createRouter({ routeTree });

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}
