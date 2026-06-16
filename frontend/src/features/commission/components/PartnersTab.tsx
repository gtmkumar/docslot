// Care Partners list. Name, masked phone, type, tier, KYC, status. Register
// gates on commission.broker.invite; rows open manage when the viewer can manage
// (suspend/activate/blacklist). PHI: masked phone only, no PAN.

import { useTranslation } from 'react-i18next';
import { ChevronRight, UserPlus } from 'lucide-react';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useBrokers } from '../api';
import { CommissionBadge } from './CommissionBadge';
import type { Broker } from '@/lib/mock/contracts';

export function PartnersTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data, isLoading, isError, refetch } = useBrokers();
  const openPanel = useUI((s) => s.openPanel);
  const canManage = can('commission.broker.suspend') || can('commission.broker.blacklist') || can('commission.broker.activate');

  return (
    <div className="flex flex-col gap-4">
      {can('commission.broker.invite') ? (
        <div className="flex justify-end">
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'registerBroker' })}>
            <UserPlus size={15} aria-hidden="true" />
            {t('commission.partners.register')}
          </Button>
        </div>
      ) : null}

      <Card>
        {isError ? (
          <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} actionLabel={t('common.retry')} onAction={() => void refetch()} />
        ) : isLoading || !data ? (
          <ListSkeleton />
        ) : data.length === 0 ? (
          <EmptyState title={t('commission.partners.empty')} description={t('commission.partners.emptySub')} />
        ) : (
          <ul className="flex flex-col">
            {data.map((b) => (
              <PartnerRow key={b.brokerId} broker={b} interactive={canManage} onOpen={() => openPanel({ type: 'manageBroker', brokerId: b.brokerId })} />
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function PartnerRow({ broker, interactive, onOpen }: { broker: Broker; interactive: boolean; onOpen: () => void }) {
  const { t } = useTranslation();
  const statusTone = broker.isBlacklisted ? 'blacklisted' : broker.isActive ? 'active' : 'inactive';
  const statusLabel = broker.isBlacklisted ? t('commission.partners.blacklisted') : broker.isActive ? t('commission.partners.active') : t('commission.partners.inactive');

  const inner = (
    <>
      <Avatar name={broker.fullName} size="md" />
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium text-ink">{broker.fullName}</p>
        {/* PHI: masked phone only. */}
        <p className="mono text-[12px] text-muted">{broker.maskedPhone}</p>
      </div>
      <span className="hidden w-32 shrink-0 text-[12px] text-muted sm:block">{t(`commission.type.${broker.brokerType}`)}</span>
      <span className="hidden w-20 shrink-0 md:block">
        <CommissionBadge tone={broker.tierLevel} label={t(`commission.tier.${broker.tierLevel}`)} dot={false} />
      </span>
      <span className="hidden w-24 shrink-0 text-[11px] lg:block">
        <span className={broker.panVerified ? 'text-primary' : 'text-muted-2'}>
          {broker.panVerified ? t('commission.partners.kycVerified') : t('commission.partners.kycPending')}
        </span>
      </span>
      <span className="w-24 shrink-0">
        <CommissionBadge tone={statusTone} label={statusLabel} />
      </span>
      {interactive ? <ChevronRight size={16} className="shrink-0 text-muted-2" aria-hidden="true" /> : null}
    </>
  );

  const rowClass = 'flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left last:border-0';
  return (
    <li>
      {interactive ? (
        <button type="button" onClick={onOpen} className={`${rowClass} transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary`}>
          {inner}
        </button>
      ) : (
        <div className={rowClass}>{inner}</div>
      )}
    </li>
  );
}

function ListSkeleton() {
  return (
    <ul className="flex flex-col" role="status" aria-busy="true">
      {Array.from({ length: 4 }).map((_, i) => (
        <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
          <Skeleton className="h-10 w-10 rounded-full" />
          <div className="flex flex-1 flex-col gap-2">
            <Skeleton className="h-3 w-40" />
            <Skeleton className="h-3 w-24" />
          </div>
          <Skeleton className="h-5 w-20" />
        </li>
      ))}
    </ul>
  );
}
