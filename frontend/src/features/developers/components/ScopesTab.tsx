// Scopes catalog (read-only). Lists platform_api.api_scopes: key + description,
// with consent-required and sensitive markers. Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { ShieldAlert, UserCheck } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { useScopes } from '../api';

export function ScopesTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useScopes();

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
          {Array.from({ length: 6 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
              <Skeleton className="h-3 w-48" />
              <Skeleton className="ml-auto h-3 w-40" />
            </li>
          ))}
        </ul>
      </Card>
    );
  }

  if (data.length === 0) {
    return (
      <Card>
        <EmptyState title={t('developers.scopes.emptyTitle')} />
      </Card>
    );
  }

  return (
    <Card>
      <p className="border-b border-line px-4 py-2.5 text-[12px] text-muted">{t('developers.scopes.sub')}</p>
      <ul className="flex flex-col">
        {data.map((s) => (
          <li key={s.scopeKey} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
            <div className="min-w-0 flex-1">
              <span className="mono block truncate text-[13px] text-ink">{s.scopeKey}</span>
              <span className="block truncate text-[12px] text-muted">{s.description}</span>
            </div>
            <div className="flex shrink-0 items-center gap-1.5">
              {s.requiresConsent ? (
                <span className="inline-flex items-center gap-1 rounded-full bg-info-soft px-2 py-0.5 text-[10px] font-medium text-info">
                  <UserCheck size={11} aria-hidden="true" />
                  {t('developers.scopes.consent')}
                </span>
              ) : null}
              {s.isDangerous ? (
                <span className="inline-flex items-center gap-1 rounded-full bg-warn-soft px-2 py-0.5 text-[10px] font-medium text-warn">
                  <ShieldAlert size={11} aria-hidden="true" />
                  {t('developers.scopes.dangerous')}
                </span>
              ) : null}
            </div>
          </li>
        ))}
      </ul>
    </Card>
  );
}
