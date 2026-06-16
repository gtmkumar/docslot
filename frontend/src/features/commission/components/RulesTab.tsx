// Commission rules (rate cards). Each rule shows its PCPNDT-exclusion as an
// ENFORCED badge (never a toggle). Create gates on commission.rules.create.

import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { inr } from '@/lib/format';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useCommissionRules } from '../api';
import { CommissionBadge, PndtBadge } from './CommissionBadge';
import type { CommissionRule } from '@/lib/mock/contracts';

export function RulesTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useCommissionRules();
  const openPanel = useUI((s) => s.openPanel);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-[13px] text-muted">{t('commission.rules.sub')}</p>
        {can('commission.rules.create') ? (
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'createCommissionRule' })}>
            {t('commission.rules.create')}
          </Button>
        ) : null}
      </div>

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 3 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-5 w-20" />
              </li>
            ))}
          </ul>
        ) : data.length === 0 ? (
          <EmptyState title={t('commission.rules.empty')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((r) => (
              <RuleRow key={r.ruleId} rule={r} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function RuleRow({ rule }: { rule: CommissionRule }) {
  const { t } = useTranslation();
  const rate =
    rule.calcType === 'flat' && rule.flatAmountInr != null
      ? inr(rule.flatAmountInr)
      : rule.calcType === 'percentage' && rule.percentage != null
        ? `${rule.percentage}%`
        : t('commission.rules.calc.tiered_table');

  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="truncate text-[13px] font-medium text-ink">{rule.ruleName}</span>
          {/* PCPNDT exclusion — always enforced, shown as a guarantee. */}
          <PndtBadge label={t('commission.pndtEnforced')} />
          {rule.firstBookingOnly ? (
            <span className="rounded-full bg-surface-sunk px-1.5 py-0.5 text-[10px] uppercase text-muted">{t('commission.rules.firstBookingOnly')}</span>
          ) : null}
        </div>
        <p className="mono text-[11px] text-muted">{rule.ruleKey}</p>
      </div>
      <span className="hidden w-20 shrink-0 text-[12px] text-muted sm:block">{t(`commission.rules.calc.${rule.calcType}`)}</span>
      <span className="mono hidden w-20 shrink-0 text-right text-[12px] text-ink md:block">{rate}</span>
      <span className="mono hidden w-16 shrink-0 text-right text-[12px] text-muted lg:block">#{rule.priority}</span>
      <CommissionBadge tone={rule.isActive ? 'active' : 'inactive'} label={rule.isActive ? t('commission.rules.active') : t('commission.rules.inactive')} />
    </li>
  );
}
