// IP allow-list editor (#91) — lives inside the Security tab's "Access restrictions"
// section. Lists this tenant's CIDR entries, adds a new one, and soft-deactivates an
// existing one. All three endpoints gate on platform.ip_allowlist.manage (SEPARATE
// from the tenant.settings.update that gates the policy toggle) — so the editor is
// shown only to a holder; without it we render an honest "no access" note while the
// toggle above still renders (it's part of the policy plane).
//
// CIDRs are network metadata, not secrets. The server validates the IP/CIDR
// authoritatively (422 → toast); the client regex is only for instant feedback.
// States: skeleton, empty, error (+retry). Add invalidates; remove drops the row
// surgically then reconciles. Each write carries a caller-generated Idempotency-Key.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Network, Plus, Trash2, TriangleAlert } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { shortDate } from '@/lib/format';
import { toUserError } from '@/lib/backend';
import type { IpAllowlistEntry } from '@/lib/mock/contracts';
import { useAddIpAllowlist, useIpAllowlist, useRemoveIpAllowlist } from '../api';

/** Loose IPv4/IPv6 (+ optional /prefix) check for instant UX feedback only. The
 *  server (Postgres inet/cidr) is the authoritative validator. */
function looksLikeCidr(value: string): boolean {
  const s = value.trim();
  if (!s) return false;
  const ipv4 = /^(\d{1,3})(\.\d{1,3}){3}(\/(3[0-2]|[12]?\d))?$/;
  if (ipv4.test(s)) {
    return s.split('/')[0].split('.').every((o) => Number(o) >= 0 && Number(o) <= 255);
  }
  // Very loose IPv6 (must contain a colon); server rejects anything malformed.
  return /^[0-9a-fA-F:]+(\/(12[0-8]|1[01]\d|\d?\d))?$/.test(s) && s.includes(':');
}

export function IpAllowlistEditor({ canManage, enabled }: { canManage: boolean; enabled: boolean }) {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useIpAllowlist(canManage);
  const add = useAddIpAllowlist();
  const remove = useRemoveIpAllowlist();

  const [cidr, setCidr] = useState('');
  const [label, setLabel] = useState('');
  const [touched, setTouched] = useState(false);
  const [confirmId, setConfirmId] = useState<string | null>(null);

  const trimmed = cidr.trim();
  const invalid = trimmed.length > 0 && !looksLikeCidr(trimmed);

  // The CIDR management endpoints require their own permission. When the caller lacks
  // it, the toggle above still works (policy plane) but we can't list/edit — say so.
  if (!canManage) {
    return (
      <p className="rounded-[var(--radius-sm)] border border-line bg-surface-sunk px-3 py-2 text-[12px] text-muted">
        {t('team.security.access.ipNoAccess')}
      </p>
    );
  }

  const onAdd = async () => {
    setTouched(true);
    if (!trimmed || invalid) return;
    try {
      await add.mutateAsync({ cidrRange: trimmed, label: label.trim() || null, idempotencyKey: idempotencyKey() });
      toast.success(t('team.security.access.ipAdded'));
      setCidr('');
      setLabel('');
      setTouched(false);
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const onRemove = async (entry: IpAllowlistEntry) => {
    setConfirmId(null);
    try {
      await remove.mutateAsync({ allowlistId: entry.allowlistId, idempotencyKey: idempotencyKey() });
      toast.success(t('team.security.access.ipRemoved'));
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const entries = data ?? [];

  return (
    <div className="flex flex-col gap-3">
      {/* Lock-out guard: enforcing an empty allow-list fails closed for everyone. */}
      {enabled && !isLoading && !isError && entries.length === 0 ? (
        <p className="flex items-start gap-1.5 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2 text-[12px] text-warn">
          <TriangleAlert size={14} aria-hidden="true" className="mt-0.5 shrink-0" />
          {t('team.security.access.ipLockoutWarning')}
        </p>
      ) : null}

      {isError ? (
        <EmptyState
          icon={<TriangleAlert size={24} aria-hidden="true" />}
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading ? (
        <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
          {Array.from({ length: 2 }).map((_, i) => (
            <Skeleton key={i} className="h-10 w-full" />
          ))}
        </div>
      ) : entries.length === 0 ? (
        <EmptyState
          icon={<Network size={24} aria-hidden="true" />}
          title={t('team.security.access.ipEmptyTitle')}
          description={t('team.security.access.ipEmptyBody')}
        />
      ) : (
        <ul className="flex flex-col gap-1.5">
          {entries.map((entry) => (
            <li key={entry.allowlistId} className="rounded-[var(--radius-sm)] border border-line px-3 py-2">
              <div className="flex items-center gap-2">
                <span className="mono text-[13px] text-ink">{entry.cidrRange}</span>
                {entry.label ? <span className="truncate text-[12px] text-muted">{entry.label}</span> : null}
                <span className="ml-auto text-[11px] text-muted-2">
                  {t('team.security.access.ipAddedOn', { date: shortDate(entry.createdAt) })}
                </span>
                {confirmId !== entry.allowlistId ? (
                  <button
                    type="button"
                    aria-label={t('team.security.access.ipRemove')}
                    disabled={remove.isPending}
                    onClick={() => setConfirmId(entry.allowlistId)}
                    className="rounded p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:opacity-50"
                  >
                    <Trash2 size={14} aria-hidden="true" />
                  </button>
                ) : null}
              </div>
              {entry.expiresAt ? (
                <p className="mt-0.5 text-[11px] text-muted-2">
                  {t('team.security.access.ipExpires', { date: shortDate(entry.expiresAt) })}
                </p>
              ) : null}
              {confirmId === entry.allowlistId ? (
                <div className="mt-2 flex items-center justify-between gap-2 border-t border-line pt-2">
                  <p className="text-[12px] text-muted">{t('team.security.access.ipConfirmRemove')}</p>
                  <div className="flex shrink-0 gap-2">
                    <Button variant="ghost" size="sm" onClick={() => setConfirmId(null)}>
                      {t('common.cancel')}
                    </Button>
                    <Button variant="danger" size="sm" disabled={remove.isPending} onClick={() => void onRemove(entry)}>
                      {t('team.security.access.ipRemove')}
                    </Button>
                  </div>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      )}

      {/* Add a CIDR. */}
      <div className="flex flex-col gap-2 rounded-[var(--radius-sm)] border border-line bg-bg-2 p-3 sm:flex-row sm:items-end">
        <div className="flex-1">
          <label htmlFor="ip-cidr" className={labelClass}>
            {t('team.security.access.ipCidrLabel')}
          </label>
          <TextInput
            id="ip-cidr"
            value={cidr}
            onChange={(e) => setCidr(e.target.value)}
            placeholder="203.0.113.0/24"
            aria-invalid={touched && (invalid || trimmed.length === 0)}
            className="mono"
          />
        </div>
        <div className="flex-1">
          <label htmlFor="ip-label" className={labelClass}>
            {t('team.security.access.ipLabelLabel')}
            <span className="font-normal lowercase tracking-normal text-muted-2"> ({t('team.security.optional')})</span>
          </label>
          <TextInput
            id="ip-label"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            placeholder={t('team.security.access.ipLabelPlaceholder')}
          />
        </div>
        <Button variant="primary" size="md" disabled={!trimmed || invalid || add.isPending} onClick={() => void onAdd()}>
          <Plus size={15} aria-hidden="true" />
          {t('team.security.access.ipAdd')}
        </Button>
      </div>
      {touched && invalid ? <p className="text-[12px] text-danger">{t('team.security.access.ipInvalid')}</p> : null}
    </div>
  );
}
