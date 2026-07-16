// Workspace Settings (Phase 1). Layout: a page header ("Settings / Workspace"), a left
// section rail (sub-nav — a new pattern here; horizontal pills on narrow screens), and a
// stacked column of section cards. The rail links to /settings/$section and moves focus
// to that section on navigation (deep-linkable + refresh-safe).
//
// Permission model (in-memory can(), never a role branch):
//  - The settings query only runs when the caller holds tenant.settings.read; without it
//    the Organization / Booking-rules / WhatsApp sections collapse to ONE forbidden card.
//  - A GET 404 (no facility row) is a DISTINCT "not set up" state, not "empty data".
//  - Editing gates on tenant.settings.update (the sections disable their controls + hide
//    Save when it's absent).
//  - Languages + the More-settings links ALWAYS render (Languages is available to everyone).
//
// All three list states are implemented: loading skeleton, error (+retry), and the two
// distinct empty states above. Zero hex — tokens only.

import { useEffect } from 'react';
import { Link, useParams } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Lock, TriangleAlert } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { ApiError } from '@/lib/api-client';
import { USE_REAL_API } from '@/lib/backend/flag';
import { usePermissions } from '@/lib/permissions';
import { useSession } from '@/stores/session';
import { useSettings } from './api';
import { OrganizationSection } from './components/OrganizationSection';
import { BookingRulesSection } from './components/BookingRulesSection';
import { WhatsappSection } from './components/WhatsappSection';
import { LanguagesSection } from './components/LanguagesSection';
import { LinksSection } from './components/LinksSection';

// The rail sections, in display order. `param` is both the URL segment and the DOM anchor
// suffix (`section-<param>`). The first three are gated by tenant.settings.read.
const RAIL = [
  { param: 'organization', labelKey: 'settings.section.organization' },
  { param: 'booking-rules', labelKey: 'settings.section.bookingRules' },
  { param: 'whatsapp', labelKey: 'settings.section.whatsapp' },
  { param: 'languages', labelKey: 'settings.section.languages' },
  { param: 'team', labelKey: 'settings.section.team' },
] as const;
const KNOWN_SECTIONS = new Set<string>(RAIL.map((r) => r.param));
const GATED_SECTIONS = new Set<string>(['organization', 'booking-rules', 'whatsapp']);

export function SettingsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  // Platform scope (super_admin, no active tenant): tenant.* permissions resolve but
  // there is no workspace to read — never fire the settings query without a tenant
  // (same gate as TeamScreen; avoids null-tenant requests).
  const tenantId = useSession((s) => s.tenantId);
  const hasTenant = !USE_REAL_API || Boolean(tenantId);
  const canRead = can('tenant.settings.read') && hasTenant;
  const canUpdate = can('tenant.settings.update') && hasTenant;

  // `strict: false` reads the optional $section param for both /settings and
  // /settings/$section (the two routes render this same screen).
  const { section } = useParams({ strict: false }) as { section?: string };

  const { data, isLoading, isError, error, refetch } = useSettings(canRead);
  const notFound = isError && error instanceof ApiError && error.status === 404;
  // The three gated sections only render (with their own anchors) once data is in hand.
  const sectionsRendered = canRead && !notFound && !isError && !isLoading && Boolean(data);

  // Section rail navigation → move focus to the target section (deep-link + refresh safe).
  // When the gated sections are collapsed, gated params resolve to the single collapsed
  // card's anchor ('organization'). Deferred with rAF so it wins over AppShell's
  // route-change h1 focus (the parent effect runs after this child effect).
  useEffect(() => {
    if (!section || !KNOWN_SECTIONS.has(section)) return;
    const anchor = !sectionsRendered && GATED_SECTIONS.has(section) ? 'organization' : section;
    const raf = requestAnimationFrame(() => {
      const el = document.getElementById(`section-${anchor}`);
      if (!el) return;
      const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
      el.scrollIntoView({ block: 'start', behavior: reduce ? 'auto' : 'smooth' });
      el.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(raf);
  }, [section, sectionsRendered]);

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-6">
      <header>
        <p className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('settings.eyebrow')}</p>
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('settings.workspace')}
        </h1>
        <p className="mt-1 max-w-2xl text-[13px] text-muted">{t('settings.subtitle')}</p>
      </header>

      <div className="flex flex-col gap-5 md:flex-row md:gap-8">
        {/* Section rail — horizontal pills (narrow) / sticky vertical list (desktop). */}
        <nav
          aria-label={t('settings.railLabel')}
          className="flex gap-1 overflow-x-auto pb-1 md:sticky md:top-2 md:w-52 md:shrink-0 md:flex-col md:self-start md:overflow-visible md:pb-0"
        >
          {/* Menus never lie: without the read permission the gated entries are omitted,
              not shown-then-refused (a gated deep-link still lands on the forbidden card). */}
          {RAIL.filter((item) => canRead || !GATED_SECTIONS.has(item.param)).map((item) => {
            const active = section === item.param;
            return (
              <Link
                key={item.param}
                to="/settings/$section"
                params={{ section: item.param }}
                className={[
                  'shrink-0 whitespace-nowrap rounded-[var(--radius-sm)] px-3 py-2 text-[13px] font-medium transition-colors',
                  active ? 'bg-primary-soft text-primary' : 'text-muted hover:bg-surface-sunk hover:text-ink',
                ].join(' ')}
              >
                {t(item.labelKey)}
              </Link>
            );
          })}
        </nav>

        {/* Content column */}
        <div className="flex min-w-0 flex-1 flex-col gap-5">
          {!canRead ? (
            <AnchoredState
              icon={<Lock size={26} aria-hidden="true" />}
              title={t('settings.forbiddenTitle')}
              description={t('settings.forbiddenBody')}
            />
          ) : notFound ? (
            <AnchoredState
              icon={<TriangleAlert size={26} aria-hidden="true" />}
              title={t('settings.noFacilityTitle')}
              description={t('settings.noFacilityBody')}
            />
          ) : isError ? (
            <AnchoredState
              icon={<TriangleAlert size={26} aria-hidden="true" />}
              title={t('error.genericTitle')}
              description={t('error.genericBody')}
              actionLabel={t('common.retry')}
              onAction={() => void refetch()}
            />
          ) : isLoading || !data ? (
            <SettingsSkeleton />
          ) : (
            <>
              <OrganizationSection settings={data} canUpdate={canUpdate} />
              <BookingRulesSection settings={data} canUpdate={canUpdate} />
              <WhatsappSection whatsapp={data.whatsApp} />
            </>
          )}

          {/* Always available, regardless of settings permissions. */}
          <LanguagesSection />
          <LinksSection />
        </div>
      </div>
    </section>
  );
}

/** A state card (forbidden / not-set-up / error) that carries the collapsed gated-region
 *  anchor (`section-organization`) + a screen-reader heading, so the rail's gated links
 *  still land here and focus moves correctly. */
function AnchoredState({
  icon,
  title,
  description,
  actionLabel,
  onAction,
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
  actionLabel?: string;
  onAction?: () => void;
}) {
  return (
    <Card className="scroll-mt-6">
      <h2 id="section-organization" tabIndex={-1} className="sr-only">
        {title}
      </h2>
      <EmptyState
        icon={icon}
        title={title}
        description={description}
        actionLabel={actionLabel}
        onAction={onAction}
      />
    </Card>
  );
}

function SettingsSkeleton() {
  return (
    <div className="flex flex-col gap-5" role="status" aria-busy="true">
      {Array.from({ length: 3 }).map((_, s) => (
        <Card key={s} className="p-4 sm:p-5">
          <div className="mb-4 flex items-center gap-2.5">
            <Skeleton className="h-7 w-7 rounded-full" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-3.5 w-40" />
              <Skeleton className="h-3 w-64" />
            </div>
          </div>
          <div className="flex flex-col gap-3">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-3/4" />
          </div>
        </Card>
      ))}
    </div>
  );
}
