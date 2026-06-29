// The Care Partner's referral links — short code, target URL (copyable), and
// click/conversion counts. Generate gates on commission.broker.generate_link_self
// and opens the slide-over. Owns its own skeleton/empty/error states.

import { useTranslation } from 'react-i18next';
import { Copy, Link2 } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { CommissionBadge } from '@/features/commission/components/CommissionBadge';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useReferralLinks } from '../api';
import type { ReferralLink } from '@/lib/mock/contracts';

export function ReferralLinks() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useReferralLinks();
  const openPanel = useUI((s) => s.openPanel);

  return (
    <section aria-labelledby="portal-links-heading" className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 id="portal-links-heading" className="text-base font-semibold text-ink">
            {t('portal.links.title')}
          </h2>
          <p className="text-[13px] text-muted">{t('portal.links.sub')}</p>
        </div>
        {can('commission.broker.generate_link_self') ? (
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'generateLink' })}>
            {t('portal.links.generate')}
          </Button>
        ) : null}
      </div>

      {isError ? (
        <Card>
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        </Card>
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-2" role="status" aria-busy="true">
          {Array.from({ length: 2 }).map((_, i) => (
            <Skeleton key={i} className="h-16 w-full" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <Card>
          <EmptyState
            icon={<Link2 size={28} aria-hidden="true" />}
            title={t('portal.links.empty')}
            description={t('portal.links.emptySub')}
            actionLabel={can('commission.broker.generate_link_self') ? t('portal.links.generate') : undefined}
            onAction={can('commission.broker.generate_link_self') ? () => openPanel({ type: 'generateLink' }) : undefined}
          />
        </Card>
      ) : (
        <ul className="flex flex-col gap-3">
          {data.map((l) => (
            <LinkCard key={l.linkId} link={l} />
          ))}
        </ul>
      )}
    </section>
  );
}

function LinkCard({ link }: { link: ReferralLink }) {
  const { t } = useTranslation();

  const onCopy = async () => {
    if (!link.targetUrl) return;
    try {
      await navigator.clipboard.writeText(link.targetUrl);
      toast.success(t('portal.links.copied'));
    } catch {
      toast.error(t('portal.links.copyFailed'));
    }
  };

  return (
    <li>
      <Card className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="mono text-sm font-medium text-ink">{link.shortCode}</span>
            <CommissionBadge
              tone={link.isActive ? 'active' : 'inactive'}
              label={link.isActive ? t('portal.links.active') : t('portal.links.inactive')}
            />
            {link.campaignName ? (
              <span className="rounded-full bg-surface-sunk px-1.5 py-0.5 text-[10px] uppercase text-muted">
                {link.campaignName}
              </span>
            ) : null}
          </div>
          {link.targetUrl ? <p className="mono mt-1 truncate text-[12px] text-muted">{link.targetUrl}</p> : null}
        </div>

        <div className="flex items-center gap-4">
          <div className="text-right">
            <p className="mono text-sm font-semibold text-ink">{link.clickCount}</p>
            <p className="text-[10px] uppercase tracking-wider text-muted-2">{t('portal.links.clicks')}</p>
          </div>
          <div className="text-right">
            <p className="mono text-sm font-semibold text-ink">{link.conversionCount}</p>
            <p className="text-[10px] uppercase tracking-wider text-muted-2">{t('portal.links.conversions')}</p>
          </div>
          {link.targetUrl ? (
            <Button variant="ghost" size="sm" onClick={() => void onCopy()} aria-label={t('portal.links.copy')}>
              <Copy size={14} aria-hidden="true" />
              {t('portal.links.copy')}
            </Button>
          ) : null}
        </div>
      </Card>
    </li>
  );
}
