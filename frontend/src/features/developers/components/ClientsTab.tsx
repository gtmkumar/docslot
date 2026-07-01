// API clients list. Skeleton + empty + error. Rows open the manage-client
// slide-over when the viewer can manage clients. No secrets here — the list DTO
// never carries them.

import { useTranslation } from 'react-i18next';
import { ChevronRight } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { dateTime } from '@/lib/format';
import { useUI } from '@/stores/ui';
import { useApiClients } from '../api';
import { StatusBadge } from './StatusBadge';
import type { ApiClient } from '@/lib/mock/contracts';

export function ClientsTab({ canManage }: { canManage: boolean }) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useApiClients();
  const openPanel = useUI((s) => s.openPanel);

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
              <div className="flex flex-1 flex-col gap-2">
                <Skeleton className="h-3 w-44" />
                <Skeleton className="h-3 w-28" />
              </div>
              <Skeleton className="h-5 w-20" />
            </li>
          ))}
        </ul>
      </Card>
    );
  }

  if (data.length === 0) {
    return (
      <Card>
        <EmptyState title={t('developers.clients.emptyTitle')} description={t('developers.clients.emptyBody')} />
      </Card>
    );
  }

  return (
    <Card>
      <ul className="flex flex-col">
        {data.map((c) => (
          <ClientRow
            key={c.clientId}
            client={c}
            interactive={canManage}
            onOpen={() => openPanel({ type: 'manageClient', clientId: c.clientId })}
          />
        ))}
      </ul>
    </Card>
  );
}

function ClientRow({
  client,
  interactive,
  onOpen,
}: {
  client: ApiClient;
  interactive: boolean;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  const inner = (
    <>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-sm font-medium text-ink">{client.clientName}</span>
          <span className="mono text-[11px] text-muted-2">{client.clientCode}</span>
        </div>
        <p className="text-[12px] text-muted">{t(`developers.clientType.${client.clientType}`)}</p>
      </div>

      <div className="hidden w-28 shrink-0 sm:block">
        <StatusBadge tone={client.status} label={t(`developers.status.${client.status}`)} />
      </div>

      <span className="mono hidden w-20 shrink-0 text-right text-[12px] text-muted md:block">
        {t('developers.clients.scopesCount', { count: client.grantedScopes.length })}
      </span>

      <span className="mono hidden w-20 shrink-0 text-right text-[12px] text-muted lg:block">
        {t('developers.clients.perMin', { count: client.rateLimitPerMinute })}
      </span>

      <span
        className="mono hidden w-20 shrink-0 text-right text-[12px] text-muted lg:block"
        title={t('developers.clients.requests24hTitle')}
      >
        {t('developers.clients.requests24h', { count: client.requestsLast24h })}
      </span>

      <span className="mono hidden w-32 shrink-0 text-right text-[11px] text-muted-2 lg:block">
        {client.lastUsedAt ? dateTime(client.lastUsedAt) : t('developers.clients.neverUsed')}
      </span>

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
