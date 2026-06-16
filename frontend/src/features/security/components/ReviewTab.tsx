// Security review queue tab. Break-glass accesses + anomalies + consent
// revocations awaiting review (mirrors v_security_review_queue). "Record
// break-glass access" gates on docslot.medical_access.break_glass. The queue read
// is gated by the menu (platform.anomalies.review server-side). PHI: only a
// masked subject ref + an actor label (no name/email). Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { KeyRound } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { dateTime } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useReviewQueue } from '../api';
import { SecurityBadge } from './SecurityBadges';
import type { ReviewQueueItem } from '@/lib/mock/contracts';

export function ReviewTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useReviewQueue();
  const openPanel = useUI((s) => s.openPanel);
  const canBreakGlass = can('docslot.medical_access.break_glass');

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-[13px] text-muted">{t('security.review.sub')}</p>
        {canBreakGlass ? (
          <Button variant="ghost" size="sm" onClick={() => openPanel({ type: 'breakGlass' })}>
            <KeyRound size={14} aria-hidden="true" />
            {t('security.review.recordBreakGlass')}
          </Button>
        ) : null}
      </div>

      {isError ? (
        <Card>
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        </Card>
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-2" role="status" aria-busy="true">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-20 w-full" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <Card>
          <EmptyState title={t('security.review.empty')} description={t('security.review.emptySub')} />
        </Card>
      ) : (
        <ul className="flex flex-col gap-2">
          {data.map((item) => (
            <ReviewItem key={item.itemId} item={item} />
          ))}
        </ul>
      )}
    </div>
  );
}

function ReviewItem({ item }: { item: ReviewQueueItem }) {
  const { t } = useTranslation();
  const sourceLabel =
    item.source === 'break_glass' ? t('security.review.sourceBreakGlass') : item.source === 'anomaly' ? t('security.review.sourceAnomaly') : t('security.review.sourceConsent');

  return (
    <li>
      <Card className="p-3">
        <div className="flex items-start gap-2">
          <SecurityBadge tone={item.severity} label={sourceLabel} />
          <span className="ml-auto mono shrink-0 text-[11px] text-muted-2">{dateTime(item.occurredAt)}</span>
        </div>
        <p className="mt-2 text-[13px] text-ink">{item.description}</p>
        <div className="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-muted">
          {item.actorLabel ? <span>{t('security.review.actor', { label: item.actorLabel })}</span> : null}
          {item.subjectMaskedPhone ? <span className="mono">{t('security.review.subject', { phone: item.subjectMaskedPhone })}</span> : null}
        </div>
      </Card>
    </li>
  );
}
