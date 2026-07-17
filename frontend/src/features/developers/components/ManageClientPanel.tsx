// Manage API client slide-over. Sections:
//   1. Details + status actions (approve / suspend / reactivate).
//   2. Rotate secret → opens the one-time secret reveal.
//   3. Rate limits (per-minute / per-day / burst).
//   4. Granted scopes (grant/revoke from the catalog).
// All mutations gate upstream on platform.api_clients.manage and carry a stable
// Idempotency-Key. No secret is ever shown here except via the rotate flow.

import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { KeyRound } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { TextInput, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { dateTime, shortDate } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useUI } from '@/stores/ui';
import {
  useApiClients,
  useRotateSecret,
  useScopes,
  useSetClientRateLimits,
  useSetClientScopes,
  useSetClientStatus,
} from '../api';
import { StatusBadge } from './StatusBadge';

export function ManageClientPanel({ clientId, open, onClose }: { clientId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { data: clients, isLoading } = useApiClients();
  const client = clients?.find((c) => c.clientId === clientId);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('developers.manage.eyebrow')}
      title={client?.clientName ?? t('developers.manage.eyebrow')}
    >
      {isLoading || !client ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : (
        <div className="flex flex-col gap-6">
          <DetailsSection client={client} />
          <RateLimitsSection
            clientId={clientId}
            perMin={client.rateLimitPerMinute}
            perDay={client.rateLimitPerDay}
            burst={client.burstLimit}
          />
          <ScopesSection clientId={clientId} granted={client.grantedScopes} />
        </div>
      )}
    </SlideOver>
  );
}

function DetailsSection({
  client,
}: {
  client: NonNullable<ReturnType<typeof useApiClients>['data']>[number];
}) {
  const { t } = useTranslation();
  const setStatus = useSetClientStatus();
  const rotate = useRotateSecret();
  const openPanel = useUI((s) => s.openPanel);

  // Status actions are OPTIMISTIC: the badge + action cluster flip instantly (the
  // hook patches the cached row); on failure the row snaps back and we toast the
  // revert so the user isn't misled.
  const onRevert = (e: unknown) => toast.error(t('common.reverted', { error: toUserError(e) }));
  const onApprove = () => {
    setStatus.mutate(
      { clientId: client.clientId, isActive: true, isVerified: true, idempotencyKey: idempotencyKey() },
      { onError: onRevert },
    );
    toast.success(t('developers.manage.approved'));
  };
  const onSuspend = () => {
    setStatus.mutate(
      { clientId: client.clientId, isActive: false, isVerified: client.isVerified, idempotencyKey: idempotencyKey() },
      { onError: onRevert },
    );
    toast(t('developers.manage.suspended'));
  };
  const onReactivate = () => {
    setStatus.mutate(
      { clientId: client.clientId, isActive: true, isVerified: client.isVerified, idempotencyKey: idempotencyKey() },
      { onError: onRevert },
    );
    toast.success(t('developers.manage.reactivated'));
  };

  const onRotate = async () => {
    const result = await rotate.mutateAsync({ clientId: client.clientId, idempotencyKey: idempotencyKey() });
    // Hand the one-time secret straight to the reveal panel (never cached).
    openPanel({ type: 'clientSecret', result, kind: 'client', intent: 'rotated' });
  };

  const rows: { label: string; value: string; mono?: boolean }[] = [
    { label: t('developers.manage.owner'), value: client.ownerEmail },
    { label: t('developers.register.clientType'), value: t(`developers.clientType.${client.clientType}`) },
    { label: t('developers.manage.created'), value: shortDate(client.createdAt), mono: true },
    { label: t('developers.manage.lastUsed'), value: client.lastUsedAt ? dateTime(client.lastUsedAt) : t('developers.clients.neverUsed'), mono: true },
  ];

  return (
    <section className="flex flex-col gap-3">
      <div className="flex items-center justify-between">
        <span className="mono text-[12px] text-muted">{client.clientCode}</span>
        <StatusBadge tone={client.status} label={t(`developers.status.${client.status}`)} />
      </div>

      <dl className="grid grid-cols-2 gap-x-4 gap-y-3 rounded-[var(--radius)] border border-line p-3">
        {rows.map((r) => (
          <div key={r.label}>
            <dt className="text-[11px] uppercase tracking-wider text-muted-2">{r.label}</dt>
            <dd className={`mt-0.5 truncate text-[13px] text-ink ${r.mono ? 'mono' : ''}`}>{r.value}</dd>
          </div>
        ))}
      </dl>

      <div className="flex flex-wrap gap-2">
        {/* Status actions reflect the current state. */}
        {client.status === 'pending' ? (
          <Button variant="primary" size="sm" onClick={onApprove} disabled={setStatus.isPending}>
            {t('developers.manage.approve')}
          </Button>
        ) : null}
        {client.status === 'approved' ? (
          <Button variant="danger" size="sm" onClick={onSuspend} disabled={setStatus.isPending}>
            {t('developers.manage.suspend')}
          </Button>
        ) : null}
        {client.status === 'suspended' ? (
          <Button variant="primary" size="sm" onClick={onReactivate} disabled={setStatus.isPending}>
            {t('developers.manage.reactivate')}
          </Button>
        ) : null}
        <Button variant="ghost" size="sm" onClick={() => void onRotate()} disabled={rotate.isPending}>
          <KeyRound size={14} aria-hidden="true" />
          {t('developers.manage.rotateSecret')}
        </Button>
      </div>
    </section>
  );
}

function RateLimitsSection({
  clientId,
  perMin,
  perDay,
  burst,
}: {
  clientId: string;
  perMin: number;
  perDay: number;
  burst: number;
}) {
  const { t } = useTranslation();
  const setLimits = useSetClientRateLimits();
  const [rpm, setRpm] = useState(perMin);
  const [rpd, setRpd] = useState(perDay);
  const [b, setB] = useState(burst);

  // Re-sync when switching between clients without unmounting.
  useEffect(() => {
    setRpm(perMin);
    setRpd(perDay);
    setB(burst);
  }, [perMin, perDay, burst]);

  const dirty = rpm !== perMin || rpd !== perDay || b !== burst;

  const onSave = () => {
    setLimits.mutate(
      { clientId, limits: { rateLimitPerMinute: rpm, rateLimitPerDay: rpd, burstLimit: b }, idempotencyKey: idempotencyKey() },
      { onSuccess: () => toast.success(t('developers.manage.rateSaved')) },
    );
  };

  return (
    <section>
      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
        {t('developers.manage.rateHeading')}
      </h3>
      <div className="grid grid-cols-3 gap-2">
        <div>
          <label htmlFor="rl-min" className={labelClass}>
            {t('developers.manage.perMinute')}
          </label>
          <TextInput id="rl-min" type="number" min={0} className="mono" value={rpm} onChange={(e) => setRpm(Number(e.target.value) || 0)} />
        </div>
        <div>
          <label htmlFor="rl-day" className={labelClass}>
            {t('developers.manage.perDay')}
          </label>
          <TextInput id="rl-day" type="number" min={0} className="mono" value={rpd} onChange={(e) => setRpd(Number(e.target.value) || 0)} />
        </div>
        <div>
          <label htmlFor="rl-burst" className={labelClass}>
            {t('developers.manage.burst')}
          </label>
          <TextInput id="rl-burst" type="number" min={0} className="mono" value={b} onChange={(e) => setB(Number(e.target.value) || 0)} />
        </div>
      </div>
      <Button variant="ghost" size="sm" className="mt-2" disabled={!dirty || setLimits.isPending} onClick={onSave}>
        {t('developers.manage.saveRate')}
      </Button>
    </section>
  );
}

function ScopesSection({ clientId, granted }: { clientId: string; granted: string[] }) {
  const { t } = useTranslation();
  const { data: scopes, isLoading } = useScopes();
  const setScopes = useSetClientScopes();
  const [selected, setSelected] = useState<Set<string>>(new Set(granted));

  useEffect(() => {
    setSelected(new Set(granted));
  }, [granted]);

  const toggle = (key: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const dirty =
    selected.size !== granted.length || granted.some((g) => !selected.has(g));

  const onSave = () => {
    setScopes.mutate(
      { clientId, scopeKeys: [...selected], idempotencyKey: idempotencyKey() },
      { onSuccess: () => toast.success(t('developers.manage.scopesSaved')) },
    );
  };

  return (
    <section>
      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
        {t('developers.manage.scopesHeading')}
      </h3>
      {isLoading || !scopes ? (
        <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
          {Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-9 w-full" />
          ))}
        </div>
      ) : (
        <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
          {scopes.map((s) => (
            <li key={s.scopeKey}>
              <label className="flex cursor-pointer items-center gap-2.5 px-3 py-2 transition-colors hover:bg-surface-sunk">
                <input
                  type="checkbox"
                  checked={selected.has(s.scopeKey)}
                  onChange={() => toggle(s.scopeKey)}
                  className="h-4 w-4 accent-[var(--primary)]"
                />
                <span className="mono min-w-0 flex-1 truncate text-[12px] text-ink">{s.scopeKey}</span>
                {s.requiresConsent ? (
                  <span className="rounded-full bg-info-soft px-1.5 py-0.5 text-[9px] font-medium uppercase text-info">
                    {t('developers.scopes.consent')}
                  </span>
                ) : null}
              </label>
            </li>
          ))}
        </ul>
      )}
      <Button variant="ghost" size="sm" className="mt-2" disabled={!dirty || setScopes.isPending} onClick={onSave}>
        {t('developers.manage.saveScopes')}
      </Button>
    </section>
  );
}
