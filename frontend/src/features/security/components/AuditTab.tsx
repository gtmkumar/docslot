// Audit integrity tab. A big trust indicator (chain intact vs broken), a
// "Verify now" action, the break detail if broken, and the anchor history with
// "Anchor head now". Verify gates on platform.audit.verify_chain; anchor on
// platform.audit.anchor. Skeleton + empty + error.

import { useTranslation } from 'react-i18next';
import { CircleCheck, Link2, RefreshCw, ShieldX } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { dateTime, relativeTime } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { usePermissions } from '@/lib/permissions';
import { useAnchorChain, useAnchors, useAuditVerify } from '../api';
import { SensitiveTag } from './SecurityBadges';

export function AuditTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch, isFetching } = useAuditVerify();
  const anchor = useAnchorChain();
  const canVerify = can('platform.audit.verify_chain');
  const canAnchor = can('platform.audit.anchor');

  const onAnchor = () => {
    anchor.mutate(
      { anchorType: 'transparency_log', anchorReference: 'transparency-log', idempotencyKey: idempotencyKey() },
      { onSuccess: () => toast.success(t('security.audit.anchored')) },
    );
  };

  return (
    <div className="flex flex-col gap-5">
      {/* Trust indicator */}
      {isError ? (
        <Card>
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        </Card>
      ) : isLoading || !data ? (
        <Card className="p-5">
          <Skeleton className="h-6 w-48" />
          <Skeleton className="mt-3 h-4 w-72" />
        </Card>
      ) : (
        <Card tone={data.intact ? 'surface' : 'surface'} className="p-5">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
            <div className="flex items-start gap-3">
              <span
                className={`flex h-11 w-11 shrink-0 items-center justify-center rounded-full ${data.intact ? 'bg-primary-soft text-primary' : 'bg-danger-soft text-danger'}`}
                aria-hidden="true"
              >
                {data.intact ? <CircleCheck size={22} /> : <ShieldX size={22} />}
              </span>
              <div>
                <h2 className={`text-base font-semibold ${data.intact ? 'text-ink' : 'text-danger'}`}>
                  {data.intact ? t('security.audit.intact') : t('security.audit.broken')}
                </h2>
                <p className="mt-0.5 max-w-xl text-[13px] text-muted">
                  {data.intact ? t('security.audit.intactSub') : t('security.audit.brokenSub')}
                </p>
                {data.lastVerifiedAt ? (
                  <p className="mt-1 text-[11px] text-muted-2">
                    {t('security.audit.lastVerified', { time: relativeTime(data.lastVerifiedAt) })}
                  </p>
                ) : null}
              </div>
            </div>
            {canVerify ? (
              <Button variant="ghost" size="sm" onClick={() => void refetch()} disabled={isFetching}>
                <RefreshCw size={14} aria-hidden="true" className={isFetching ? 'animate-spin' : ''} />
                {t('security.audit.verifyNow')}
              </Button>
            ) : null}
          </div>

          {!data.intact && data.breaks.length > 0 ? (
            <ul className="mt-4 flex flex-col gap-2 border-t border-line pt-4">
              {data.breaks.map((b) => (
                <li key={b.auditId} className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2">
                  <p className="text-[12px] font-medium text-danger">
                    {t('security.audit.brokenAt')} #{b.sequence}
                  </p>
                  <p className="mono mt-1 truncate text-[11px] text-muted">
                    {t('security.audit.expected')}: {b.expectedHash.slice(0, 24)}…
                  </p>
                  <p className="mono truncate text-[11px] text-muted">
                    {t('security.audit.actual')}: {b.actualHash.slice(0, 24)}…
                  </p>
                </li>
              ))}
            </ul>
          ) : null}
        </Card>
      )}

      {/* Anchor history */}
      <AnchorHistory canAnchor={canAnchor} onAnchor={onAnchor} anchoring={anchor.isPending} />
    </div>
  );
}

function AnchorHistory({ canAnchor, onAnchor, anchoring }: { canAnchor: boolean; onAnchor: () => void; anchoring: boolean }) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useAnchors();

  return (
    <Card>
      <header className="flex items-center justify-between gap-3 border-b border-line px-4 py-3">
        <div>
          <h3 className="text-sm font-semibold text-ink">{t('security.audit.anchorsHeading')}</h3>
          <p className="text-[12px] text-muted">{t('security.audit.anchorsSub')}</p>
        </div>
        {canAnchor ? (
          <Button variant="ghost" size="sm" onClick={onAnchor} disabled={anchoring}>
            <Link2 size={14} aria-hidden="true" />
            {t('security.audit.anchorNow')}
            <SensitiveTag label={t('security.sensitive')} />
          </Button>
        ) : null}
      </header>

      {isError ? (
        <EmptyState title={t('error.genericTitle')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
      ) : isLoading || !data ? (
        <ul className="flex flex-col" role="status" aria-busy="true">
          {Array.from({ length: 2 }).map((_, i) => (
            <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
              <Skeleton className="h-3 w-40" />
              <Skeleton className="ml-auto h-3 w-24" />
            </li>
          ))}
        </ul>
      ) : data.length === 0 ? (
        <EmptyState title={t('security.audit.noAnchors')} />
      ) : (
        <ul className="flex flex-col">
          {data.map((a) => (
            <li key={a.anchorId} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
              <div className="min-w-0 flex-1">
                <p className="text-[13px] text-ink">{t('security.audit.headSeq', { seq: a.chainHeadSequence })}</p>
                <p className="mono truncate text-[11px] text-muted">{a.anchorReference}</p>
              </div>
              <span className="rounded-full bg-surface-sunk px-2 py-0.5 text-[10px] uppercase tracking-wider text-muted">
                {a.anchorType.replace(/_/g, ' ')}
              </span>
              <span className="mono shrink-0 text-[11px] text-muted-2">{dateTime(a.anchoredAt)}</span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
