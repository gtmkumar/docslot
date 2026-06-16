// Manage Care Partner slide-over. Details (no PAN — only PAN-verified flag) +
// PCPNDT guarantee + status actions (suspend/activate) + the DANGEROUS, gated
// blacklist (permanent, platform-wide) with an inline reason + confirmation.
// Gates: suspend/activate on commission.broker.suspend/.activate; blacklist on
// commission.broker.blacklist. Money-free, but blacklist carries Idempotency-Key.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Ban, ShieldX } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { TextArea, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { usePermissions } from '@/lib/permissions';
import { useBlacklistBroker, useBrokers, useSetBrokerStatus } from '../api';
import { CommissionBadge, PndtBadge } from './CommissionBadge';

export function ManageBrokerPanel({ brokerId, open, onClose }: { brokerId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data: brokers, isLoading } = useBrokers();
  const broker = brokers?.find((b) => b.brokerId === brokerId);
  const setStatus = useSetBrokerStatus();
  const blacklist = useBlacklistBroker();

  const [blacklisting, setBlacklisting] = useState(false);
  const [reason, setReason] = useState('');
  const reasonOk = reason.trim().length > 0;

  const onSuspend = () =>
    setStatus.mutate({ brokerId, isActive: false, idempotencyKey: idempotencyKey() }, { onSuccess: () => toast(t('commission.manage.suspended')) });
  const onActivate = () =>
    setStatus.mutate({ brokerId, isActive: true, idempotencyKey: idempotencyKey() }, { onSuccess: () => toast.success(t('commission.manage.activated')) });
  const onBlacklist = () => {
    if (!reasonOk) return;
    blacklist.mutate(
      { brokerId, reason: reason.trim(), idempotencyKey: idempotencyKey() },
      { onSuccess: () => { toast(t('commission.manage.blacklisted')); onClose(); } },
    );
  };

  return (
    <SlideOver open={open} onClose={onClose} eyebrow={t('commission.manage.eyebrow')} title={broker?.fullName ?? t('commission.manage.eyebrow')}>
      {isLoading || !broker ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-16 w-full" />
        </div>
      ) : (
        <div className="flex flex-col gap-5">
          <div className="flex items-center justify-between">
            <span className="mono text-[12px] text-muted">{broker.maskedPhone}</span>
            <CommissionBadge tone={broker.tierLevel} label={t(`commission.tier.${broker.tierLevel}`)} dot={false} />
          </div>

          <dl className="grid grid-cols-2 gap-x-4 gap-y-3 rounded-[var(--radius)] border border-line p-3">
            <Detail label={t('commission.manage.type')} value={t(`commission.type.${broker.brokerType}`)} />
            <Detail label={t('commission.manage.kyc')} value={broker.panVerified ? t('commission.manage.panVerified') : t('commission.manage.panUnverified')} />
            <Detail label="GST" value={broker.gstVerified ? t('commission.manage.gstVerified') : '—'} />
          </dl>

          {/* PCPNDT — enforced guarantee, never editable. */}
          <div className="flex items-start gap-2 rounded-[var(--radius-sm)] bg-primary-soft px-3 py-2.5 text-[12px] text-primary">
            <PndtBadge label={t('commission.pndtEnforced')} />
            <span>{t('commission.manage.pndtGuarantee')}</span>
          </div>

          {broker.isBlacklisted ? (
            <p className="rounded-[var(--radius-sm)] bg-danger-soft px-3 py-2 text-[12px] text-danger">{t('commission.manage.alreadyBlacklisted')}</p>
          ) : (
            <>
              {/* Status actions. */}
              <div className="flex flex-wrap gap-2">
                {broker.isActive && can('commission.broker.suspend') ? (
                  <Button variant="ghost" size="sm" onClick={onSuspend} disabled={setStatus.isPending}>
                    {t('commission.manage.suspend')}
                  </Button>
                ) : null}
                {!broker.isActive && can('commission.broker.activate') ? (
                  <Button variant="primary" size="sm" onClick={onActivate} disabled={setStatus.isPending}>
                    {t('commission.manage.activate')}
                  </Button>
                ) : null}
              </div>

              {/* DANGEROUS: blacklist — permanent, platform-wide, gated + confirmed. */}
              {can('commission.broker.blacklist') ? (
                <section className="rounded-[var(--radius)] border border-danger-soft p-3">
                  <div className="flex items-start gap-2 text-danger">
                    <ShieldX size={16} className="mt-0.5 shrink-0" aria-hidden="true" />
                    <div>
                      <h3 className="text-[13px] font-semibold">{t('commission.manage.blacklistHeading')}</h3>
                      <p className="mt-0.5 text-[12px]">{t('commission.manage.blacklistWarn')}</p>
                    </div>
                  </div>
                  {!blacklisting ? (
                    <Button variant="danger" size="sm" className="mt-3" onClick={() => setBlacklisting(true)}>
                      <Ban size={14} aria-hidden="true" />
                      {t('commission.manage.blacklist')}
                    </Button>
                  ) : (
                    <div className="mt-3 flex flex-col gap-2">
                      <label htmlFor="bl-reason" className={labelClass}>
                        {t('commission.manage.blacklistReason')}
                      </label>
                      <TextArea id="bl-reason" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} placeholder={t('commission.manage.blacklistReasonPlaceholder')} aria-invalid={reason.length > 0 && !reasonOk} />
                      <div className="flex justify-end gap-2">
                        <Button variant="ghost" size="sm" onClick={() => setBlacklisting(false)}>
                          {t('common.cancel')}
                        </Button>
                        <Button variant="danger" size="sm" disabled={!reasonOk || blacklist.isPending} onClick={onBlacklist}>
                          {t('commission.manage.blacklistConfirm')}
                        </Button>
                      </div>
                    </div>
                  )}
                </section>
              ) : null}
            </>
          )}
        </div>
      )}
    </SlideOver>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-[11px] uppercase tracking-wider text-muted-2">{label}</dt>
      <dd className="mt-0.5 text-[13px] text-ink">{value}</dd>
    </div>
  );
}
