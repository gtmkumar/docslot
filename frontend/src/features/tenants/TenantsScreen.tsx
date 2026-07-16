// Platform Console — Tenants (clinic) management. A super_admin sees how many
// clinics exist, searches them (client-side over displayName / tenantCode / city),
// and opens one to view + edit via the right-side slide-over (?panel=manageTenant).
//
// Reuses the shared ['tenants','list'] query (useTenants) that already backs the
// impersonation picker — one list source, never a second query. Four data states:
// loading skeleton, load error, tenant-has-no-clinics empty, and a DISTINCT
// filtered-empty state. Backend-driven nav surfaces this route for holders of
// `platform.tenants.read`; the edit action inside the panel gates on
// `platform.tenants.update`. This screen never branches on a role name.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Building2, Search } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { TextInput } from '@/components/ui/Field';
import { useTenants } from '@/features/impersonation/api';
import { useUI } from '@/stores/ui';
import type { TenantListItem } from '@/lib/mock/contracts';
import { TenantStatusChip } from './components/TenantStatusChip';

export function TenantsScreen() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useTenants();
  const openPanel = useUI((s) => s.openPanel);

  const [search, setSearch] = useState('');

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <header className="min-w-0">
        <h1
          id="screen-heading"
          tabIndex={-1}
          className="text-2xl font-semibold tracking-tight text-ink outline-none"
        >
          {data !== undefined ? t('tenants.list.titleCount', { count: data.length }) : t('tenants.list.title')}
        </h1>
        <p className="mt-1 text-[13px] text-muted">{t('tenants.list.subtitle')}</p>
      </header>

      {/* Search — client-side over the loaded list (displayName / code / city). */}
      <div className="relative sm:max-w-xs">
        <Search
          size={15}
          aria-hidden="true"
          className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-2"
        />
        <TextInput
          type="search"
          value={search}
          disabled={isLoading || isError || data?.length === 0}
          onChange={(e) => setSearch(e.target.value)}
          placeholder={t('tenants.list.searchPlaceholder')}
          aria-label={t('tenants.list.searchPlaceholder')}
          className="pl-9"
        />
      </div>

      {isError ? (
        <Card>
          <EmptyState
            title={t('tenants.list.errorTitle')}
            description={t('tenants.list.errorBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        </Card>
      ) : isLoading || !data ? (
        <Card>
          <ul className="flex flex-col" role="status" aria-busy="true" aria-label={t('common.loading')}>
            {Array.from({ length: 5 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-9 w-9 rounded-[var(--radius-sm)]" />
                <div className="flex flex-1 flex-col gap-2">
                  <Skeleton className="h-3 w-48" />
                  <Skeleton className="h-3 w-28" />
                </div>
                <Skeleton className="h-5 w-16" />
              </li>
            ))}
          </ul>
        </Card>
      ) : data.length === 0 ? (
        <Card>
          <EmptyState title={t('tenants.list.emptyTitle')} description={t('tenants.list.emptyBody')} />
        </Card>
      ) : (
        <TenantList tenants={data} query={search} onOpen={(id) => openPanel({ type: 'manageTenant', tenantId: id })} />
      )}
    </section>
  );
}

function TenantList({
  tenants,
  query,
  onOpen,
}: {
  tenants: TenantListItem[];
  query: string;
  onOpen: (tenantId: string) => void;
}) {
  const { t } = useTranslation();
  // Client-side filter. The React Compiler memoizes this derived list, so we do NOT
  // hand-write useMemo.
  const q = query.trim().toLowerCase();
  const filtered =
    q.length === 0
      ? tenants
      : tenants.filter(
          (tn) =>
            tn.displayName.toLowerCase().includes(q) ||
            tn.tenantCode.toLowerCase().includes(q) ||
            (tn.city ?? '').toLowerCase().includes(q),
        );

  return (
    <div className="flex flex-col gap-3">
      <p className="text-[12px] text-muted-2" aria-live="polite">
        {t('tenants.list.resultCount', { shown: filtered.length, total: tenants.length })}
      </p>

      {filtered.length === 0 ? (
        <Card>
          <EmptyState
            title={t('tenants.list.emptyFilteredTitle')}
            description={t('tenants.list.emptyFilteredBody')}
          />
        </Card>
      ) : (
        <Card>
          <ul className="flex flex-col">
            {filtered.map((tn) => (
              <li key={tn.tenantId} className="border-b border-line last:border-0">
                <button
                  type="button"
                  onClick={() => onOpen(tn.tenantId)}
                  className="flex w-full items-center gap-3 px-4 py-3 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-primary"
                >
                  <span
                    aria-hidden="true"
                    className="flex h-9 w-9 shrink-0 items-center justify-center rounded-[var(--radius-sm)] bg-primary-soft text-primary"
                  >
                    <Building2 size={17} />
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-ink">{tn.displayName}</p>
                    <p className="mono truncate text-[12px] text-muted">
                      {tn.tenantCode}
                      {tn.city ? <span className="text-muted-2"> · {tn.city}</span> : null}
                    </p>
                  </div>
                  <span className="hidden shrink-0 text-[12px] text-muted-2 sm:block">
                    {t(`tenants.types.${tn.tenantType}`, { defaultValue: tn.tenantType })}
                  </span>
                  <TenantStatusChip status={tn.status} />
                </button>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}
