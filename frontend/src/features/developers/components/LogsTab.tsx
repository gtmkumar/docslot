// API request logs (read-only, filterable per client, paginated). Skeleton +
// empty + error. A non-PHI activity log — endpoints + status + latency only.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Select, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { dateTime } from '@/lib/format';
import { useApiClients, useApiRequestLogs } from '../api';

export function LogsTab() {
  const { t } = useTranslation();
  const [clientId, setClientId] = useState<string>('');
  const [page, setPage] = useState(1);
  const { data: clients } = useApiClients();
  const { data, isLoading, isError, refetch, isFetching } = useApiRequestLogs({
    clientId: clientId || null,
    page,
    pageSize: 15,
  });

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="flex flex-col gap-4">
      {/* Filter */}
      <div className="flex items-end gap-2">
        <div className="w-64 max-w-full">
          <label htmlFor="log-client" className={labelClass}>
            {t('developers.logs.filterClient')}
          </label>
          <Select
            id="log-client"
            value={clientId}
            onChange={(e) => {
              setClientId(e.target.value);
              setPage(1);
            }}
          >
            <option value="">{t('developers.logs.allClients')}</option>
            {clients?.map((c) => (
              <option key={c.clientId} value={c.clientId}>
                {c.clientName}
              </option>
            ))}
          </Select>
        </div>
      </div>

      <Card>
        {isError ? (
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 8 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-2.5 last:border-0">
                <Skeleton className="h-3 w-28" />
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-3 w-12" />
              </li>
            ))}
          </ul>
        ) : data.items.length === 0 ? (
          <EmptyState title={t('developers.logs.emptyTitle')} description={t('developers.logs.emptyBody')} />
        ) : (
          <>
            {/* Header row (desktop) */}
            <div className="hidden grid-cols-[140px_1fr_160px_70px_60px] gap-3 border-b border-line px-4 py-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2 lg:grid">
              <span>{t('developers.logs.colTime')}</span>
              <span>{t('developers.logs.colEndpoint')}</span>
              <span>{t('developers.logs.colScope')}</span>
              <span className="text-right">{t('developers.logs.colStatus')}</span>
              <span className="text-right">{t('developers.logs.colLatency')}</span>
            </div>
            <ul className={`flex flex-col ${isFetching ? 'opacity-60' : ''}`}>
              {data.items.map((row) => (
                <li
                  key={row.requestId}
                  className="grid grid-cols-1 gap-1 border-b border-line px-4 py-2.5 last:border-0 lg:grid-cols-[140px_1fr_160px_70px_60px] lg:items-center lg:gap-3"
                >
                  <span className="mono text-[11px] text-muted-2">{dateTime(row.occurredAt)}</span>
                  <span className="flex items-center gap-2 truncate text-[12px] text-ink">
                    <span className="mono rounded bg-surface-sunk px-1 text-[10px] text-muted">{row.method}</span>
                    <span className="truncate">{row.path}</span>
                  </span>
                  <span className="mono truncate text-[11px] text-muted">{row.scopeUsed ?? '—'}</span>
                  <span className="lg:text-right">
                    <StatusCode code={row.statusCode} />
                  </span>
                  <span className="mono text-[11px] text-muted lg:text-right">
                    {row.responseTimeMs != null ? `${row.responseTimeMs}ms` : '—'}
                  </span>
                </li>
              ))}
            </ul>
          </>
        )}
      </Card>

      {/* Pagination */}
      {data && data.items.length > 0 ? (
        <div className="flex items-center justify-between">
          <span className="text-[12px] text-muted">{t('developers.logs.page', { page, pages })}</span>
          <div className="flex items-center gap-2">
            <Button variant="ghost" size="sm" disabled={page <= 1} onClick={() => setPage((p) => Math.max(1, p - 1))}>
              <ChevronLeft size={14} aria-hidden="true" />
              {t('developers.logs.prev')}
            </Button>
            <Button variant="ghost" size="sm" disabled={page >= pages} onClick={() => setPage((p) => Math.min(pages, p + 1))}>
              {t('developers.logs.next')}
              <ChevronRight size={14} aria-hidden="true" />
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function StatusCode({ code }: { code: number }) {
  const tone =
    code < 300 ? 'text-primary' : code < 400 ? 'text-info' : code < 500 ? 'text-warn' : 'text-danger';
  return <span className={`mono text-[12px] font-medium ${tone}`}>{code}</span>;
}
