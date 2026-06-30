// Webhook deliveries slide-over. Per-subscription delivery history (event,
// status, attempts, response code, last attempt) with a manual Retry for
// failed/abandoned attempts. Skeleton + empty + error. Retry carries a stable
// Idempotency-Key.

import { useTranslation } from 'react-i18next';
import { RotateCw } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { dateTime } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import { useRetryDelivery, useWebhookDeliveries, useWebhooks } from '../api';
import { StatusBadge } from './StatusBadge';
import type { WebhookDelivery } from '@/lib/mock/contracts';

export function DeliveriesPanel({ webhookId, open, onClose }: { webhookId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data: webhooks } = useWebhooks();
  const webhook = webhooks?.find((w) => w.webhookId === webhookId);
  const { data, isLoading, isError, refetch } = useWebhookDeliveries(webhookId);
  const retry = useRetryDelivery();
  const canManage = can('platform.api_clients.manage');

  const onRetry = (deliveryId: string) => {
    retry.mutate(
      { deliveryId, idempotencyKey: idempotencyKey() },
      {
        onSuccess: () => toast.success(t('developers.deliveries.retried')),
        onError: (e) => toast.error(toUserError(e)), // a 404/409 now surfaces instead of failing silently (#55)
      },
    );
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('developers.deliveries.eyebrow')}
      title={webhook?.name ?? t('developers.deliveries.title')}
    >
      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-2" role="status" aria-busy="true">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-16 w-full" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <EmptyState title={t('developers.deliveries.emptyTitle')} description={t('developers.deliveries.emptyBody')} />
      ) : (
        <ul className="flex flex-col gap-2">
          {data.map((d) => (
            <DeliveryRow key={d.deliveryId} delivery={d} canRetry={canManage} onRetry={() => onRetry(d.deliveryId)} retrying={retry.isPending} />
          ))}
        </ul>
      )}
    </SlideOver>
  );
}

function DeliveryRow({
  delivery,
  canRetry,
  onRetry,
  retrying,
}: {
  delivery: WebhookDelivery;
  canRetry: boolean;
  onRetry: () => void;
  retrying: boolean;
}) {
  const { t } = useTranslation();
  const retryable = delivery.status === 'failed' || delivery.status === 'abandoned';

  return (
    <li className="rounded-[var(--radius)] border border-line p-3">
      <div className="flex items-center gap-2">
        <span className="mono flex-1 truncate text-[12px] text-ink">{delivery.eventType}</span>
        <StatusBadge tone={delivery.status} label={t(`developers.deliveries.status.${delivery.status}`)} />
      </div>
      <div className="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-muted">
        <span>
          {t('developers.deliveries.colAttempts')}: <span className="mono text-ink">{delivery.attemptCount}</span>
        </span>
        <span>
          {t('developers.deliveries.colCode')}:{' '}
          <span className="mono text-ink">{delivery.responseStatusCode ?? '—'}</span>
        </span>
        <span>
          {t('developers.deliveries.colTime')}:{' '}
          <span className="mono text-ink">{dateTime(delivery.deliveredAt ?? delivery.createdAt)}</span>
        </span>
      </div>
      {delivery.errorMessage ? (
        <p className="mt-1 text-[11px] text-danger">{delivery.errorMessage}</p>
      ) : null}
      {retryable && canRetry ? (
        <Button variant="ghost" size="sm" className="mt-2" onClick={onRetry} disabled={retrying}>
          <RotateCw size={13} aria-hidden="true" />
          {t('developers.deliveries.retry')}
        </Button>
      ) : null}
    </li>
  );
}
