// Webhooks list. Each row shows subscription state + a Deliveries action; when
// the viewer can manage, the row opens the edit form. Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { useUI } from '@/stores/ui';
import { useWebhooks } from '../api';
import { StatusBadge } from './StatusBadge';
import type { WebhookSubscription } from '@/lib/mock/contracts';

export function WebhooksTab({ canManage }: { canManage: boolean }) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useWebhooks();

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
          {Array.from({ length: 3 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
              <div className="flex flex-1 flex-col gap-2">
                <Skeleton className="h-3 w-40" />
                <Skeleton className="h-3 w-56" />
              </div>
              <Skeleton className="h-5 w-16" />
            </li>
          ))}
        </ul>
      </Card>
    );
  }

  if (data.length === 0) {
    return (
      <Card>
        <EmptyState title={t('developers.webhooks.emptyTitle')} description={t('developers.webhooks.emptyBody')} />
      </Card>
    );
  }

  return (
    <Card>
      <ul className="flex flex-col">
        {data.map((w) => (
          <WebhookRow key={w.webhookId} webhook={w} canManage={canManage} />
        ))}
      </ul>
    </Card>
  );
}

function WebhookRow({ webhook, canManage }: { webhook: WebhookSubscription; canManage: boolean }) {
  const { t } = useTranslation();
  const openPanel = useUI((s) => s.openPanel);

  const tone = webhook.autoDisabledAt ? 'failed' : webhook.isActive ? 'active' : 'inactive';
  const label = webhook.autoDisabledAt
    ? t('developers.webhooks.autoDisabled')
    : webhook.isActive
      ? t('developers.webhooks.active')
      : t('developers.webhooks.inactive');

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="truncate text-sm font-medium text-ink">{webhook.name}</span>
          {webhook.consecutiveFailures > 0 ? (
            <span className="rounded-full bg-danger-soft px-1.5 py-0.5 text-[10px] font-medium text-danger">
              {t('developers.webhooks.failures', { count: webhook.consecutiveFailures })}
            </span>
          ) : null}
        </div>
        <p className="mono truncate text-[12px] text-muted">{webhook.url}</p>
        <p className="text-[11px] text-muted-2">
          {t('developers.webhooks.eventsCount', { count: webhook.eventTypes.length })}
        </p>
      </div>

      <div className="hidden w-24 shrink-0 sm:block">
        <StatusBadge tone={tone} label={label} />
      </div>

      <div className="flex shrink-0 items-center gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => openPanel({ type: 'webhookDeliveries', webhookId: webhook.webhookId })}
        >
          {t('developers.webhooks.viewDeliveries')}
        </Button>
        {canManage ? (
          <Button
            variant="subtle"
            size="sm"
            onClick={() => openPanel({ type: 'webhookForm', webhookId: webhook.webhookId })}
          >
            {t('common.edit')}
          </Button>
        ) : null}
      </div>
    </li>
  );
}
