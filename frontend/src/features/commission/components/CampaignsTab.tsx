// Campaigns (marketing bonus programmes). Each campaign shows spent-so-far
// against its total budget (a usage bar). Create gates on commission.campaign.manage.
// The create form offers only the two supported bonus kinds (flat_bonus_per_booking,
// percentage_multiplier) — tier_upgrade is not offered here.

import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { inr } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useCampaigns } from '../api';
import { CommissionBadge } from './CommissionBadge';
import type { Campaign } from '@/lib/mock/contracts';

export function CampaignsTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useCampaigns();
  const openPanel = useUI((s) => s.openPanel);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-[13px] text-muted">{t('commission.campaigns.sub')}</p>
        {can('commission.campaign.manage') ? (
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'createCampaign' })}>
            {t('commission.campaigns.create')}
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
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-24 w-full" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <Card>
          <EmptyState title={t('commission.campaigns.empty')} description={t('commission.campaigns.emptySub')} />
        </Card>
      ) : (
        <ul className="flex flex-col gap-3">
          {data.map((c) => (
            <CampaignCard key={c.campaignId} campaign={c} />
          ))}
        </ul>
      )}
    </div>
  );
}

function CampaignCard({ campaign }: { campaign: Campaign }) {
  const { t } = useTranslation();
  const hasBudget = campaign.totalBudgetInr != null && campaign.totalBudgetInr > 0;
  const pct = hasBudget ? Math.min(100, Math.round((campaign.spentSoFarInr / campaign.totalBudgetInr!) * 100)) : 0;
  // Usage colour escalates as the budget depletes (token keys only, never a hex).
  const usageColor = pct >= 90 ? 'accent' : pct >= 70 ? 'warn' : 'primary';
  // bonusType is a free wire string; we only have copy for the two supported kinds.
  const bonusKey = `commission.campaigns.bonus.${campaign.bonusType}`;

  const bonusValueLabel =
    campaign.bonusValue == null
      ? '—'
      : campaign.bonusType === 'percentage_multiplier'
        ? `${campaign.bonusValue}×`
        : inr(campaign.bonusValue);

  return (
    <li>
      <Card className="p-4">
        <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <p className="text-sm font-medium text-ink">{campaign.campaignName}</p>
              <CommissionBadge
                tone={campaign.isActive ? 'active' : 'inactive'}
                label={campaign.isActive ? t('commission.campaigns.active') : t('commission.campaigns.inactive')}
              />
            </div>
            <p className="mt-0.5 text-[11px] text-muted">
              {t(bonusKey, { defaultValue: campaign.bonusType })} · {t('commission.campaigns.bonusValue')}: {bonusValueLabel}
            </p>
          </div>
        </div>

        {hasBudget ? (
          <div className="mt-3">
            <div className="mb-1 flex items-center justify-between text-[11px]">
              <span className="text-muted">{t('commission.campaigns.budgetUsage')}</span>
              <span className="mono text-ink">
                {inr(campaign.spentSoFarInr)} / {inr(campaign.totalBudgetInr!)} · {pct}%
              </span>
            </div>
            <ProgressBar
              value={campaign.spentSoFarInr}
              max={campaign.totalBudgetInr!}
              colorKey={usageColor}
              label={t('commission.campaigns.budgetUsage')}
            />
          </div>
        ) : (
          <p className="mt-3 text-[11px] text-muted-2">{t('commission.campaigns.noBudget')}</p>
        )}
      </Card>
    </li>
  );
}
