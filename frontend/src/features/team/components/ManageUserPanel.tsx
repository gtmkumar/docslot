// Manage user access slide-over. Three sections:
//   1. Roles — current assignments (with expiry), assign a new role, revoke.
//   2. Permission overrides — current overrides + an inline grant/deny form with
//      a MANDATORY reason and optional expiry. Deny-wins is stated in the UI.
//   3. Effective permissions — the "why does X have Y" explainer (role vs override).
//
// Role assignment gates on tenant.roles.assign; overrides on
// platform.overrides.grant (both checked in-memory via can()). Each mutation
// generates a stable Idempotency-Key at action start.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Plus, ShieldAlert, Trash2 } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { Select, TextArea, labelClass } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { shortDate } from '@/lib/format';
import { idempotencyKey } from '@/lib/api-client';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import {
  useAssignRole,
  useEffectivePermissions,
  usePermissionRegistry,
  useRoles,
  useSetOverride,
  useTenantUsers,
  useUserOverrides,
} from '../api';

export function ManageUserPanel({ userId, open, onClose }: { userId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { data: users } = useTenantUsers();
  const user = users?.find((u) => u.userId === userId);

  const canAssign = can('tenant.roles.assign');
  const canOverride = can('platform.overrides.grant');

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.manage.eyebrow')}
      title={user?.fullName ?? t('team.manage.eyebrow')}
    >
      <div className="flex flex-col gap-6">
        {user ? <p className="-mt-2 text-[12px] text-muted">{user.email}</p> : null}

        <RolesSection userId={userId} canAssign={canAssign} />
        {canOverride ? <OverridesSection userId={userId} /> : null}
        <EffectiveSection userId={userId} />
      </div>
    </SlideOver>
  );
}

// ── 1. Roles ─────────────────────────────────────────────────────────────────
function RolesSection({ userId, canAssign }: { userId: string; canAssign: boolean }) {
  const { t } = useTranslation();
  const { data: users } = useTenantUsers();
  const { data: roles } = useRoles();
  const assign = useAssignRole();
  const user = users?.find((u) => u.userId === userId);
  const [roleId, setRoleId] = useState('');

  const onAssign = async () => {
    if (!roleId) return;
    await assign.mutateAsync({ userId, roleId, isPrimary: false, idempotencyKey: idempotencyKey() });
    toast.success(t('team.manage.assigned'));
    setRoleId('');
  };

  // Roles not already held, for the assign picker.
  const heldIds = new Set(user?.roles.map((r) => r.roleId));
  const assignable = roles?.filter((r) => !heldIds.has(r.roleId)) ?? [];

  return (
    <section>
      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
        {t('team.manage.rolesHeading')}
      </h3>

      <ul className="flex flex-col gap-1.5">
        {(user?.roles ?? []).map((r) => (
          <li
            key={r.userTenantRoleId}
            className="flex items-center gap-2 rounded-[var(--radius-sm)] border border-line px-3 py-2"
          >
            <span className="flex-1 text-[13px] text-ink">
              {r.name}
              {r.isPrimary ? (
                <span className="ml-2 text-[10px] uppercase tracking-wider text-muted-2">{t('team.primary')}</span>
              ) : null}
            </span>
            {r.expiresAt ? (
              <span className="mono text-[11px] text-muted-2">{t('team.expires', { date: shortDate(r.expiresAt) })}</span>
            ) : null}
            {canAssign ? (
              <button
                type="button"
                aria-label={t('team.manage.revoke')}
                onClick={() => toast(t('team.manage.revoked'))}
                className="rounded p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <Trash2 size={14} aria-hidden="true" />
              </button>
            ) : null}
          </li>
        ))}
      </ul>

      {canAssign ? (
        <div className="mt-2 flex items-end gap-2">
          <div className="flex-1">
            <label htmlFor="assign-role" className={labelClass}>
              {t('team.manage.addRole')}
            </label>
            <Select id="assign-role" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
              <option value="">{t('team.manage.assignRole')}</option>
              {assignable.map((r) => (
                <option key={r.roleId} value={r.roleId}>
                  {r.name}
                </option>
              ))}
            </Select>
          </div>
          <Button variant="ghost" size="md" disabled={!roleId || assign.isPending} onClick={() => void onAssign()}>
            <Plus size={15} aria-hidden="true" />
            {t('team.manage.assignRole')}
          </Button>
        </div>
      ) : null}
    </section>
  );
}

// ── 2. Overrides ─────────────────────────────────────────────────────────────
type ExpiryChoice = 'none' | '30' | '90';

function OverridesSection({ userId }: { userId: string }) {
  const { t } = useTranslation();
  const { data: overrides, isLoading } = useUserOverrides(userId);
  const { data: registry } = usePermissionRegistry();
  const setOverride = useSetOverride();

  const [adding, setAdding] = useState(false);
  const [permissionKey, setPermissionKey] = useState('');
  const [isAllowed, setIsAllowed] = useState(false);
  const [reason, setReason] = useState('');
  const [expiry, setExpiry] = useState<ExpiryChoice>('none');
  const [reasonTouched, setReasonTouched] = useState(false);

  const selectedDef = registry?.find((p) => p.permissionKey === permissionKey);
  const reasonMissing = reason.trim().length === 0;

  const expiresAt = (): string | null => {
    if (expiry === 'none') return null;
    const days = Number(expiry);
    return new Date(Date.now() + days * 86_400_000).toISOString();
  };

  const onSave = async () => {
    setReasonTouched(true);
    if (!permissionKey || reasonMissing) return; // reason is MANDATORY
    await setOverride.mutateAsync({
      userId,
      permissionKey,
      isAllowed,
      reason: reason.trim(),
      expiresAt: expiresAt(),
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('team.override.saved'));
    setAdding(false);
    setPermissionKey('');
    setReason('');
    setReasonTouched(false);
    setIsAllowed(false);
    setExpiry('none');
  };

  return (
    <section>
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          {t('team.manage.overridesHeading')}
        </h3>
        {!adding ? (
          <Button variant="ghost" size="sm" onClick={() => setAdding(true)}>
            <Plus size={14} aria-hidden="true" />
            {t('team.manage.addOverride')}
          </Button>
        ) : null}
      </div>
      <p className="mb-2 text-[12px] text-muted">{t('team.manage.overridesSub')}</p>

      {isLoading ? (
        <Skeleton className="h-12 w-full" />
      ) : (overrides?.length ?? 0) === 0 && !adding ? (
        <p className="rounded-[var(--radius-sm)] border border-line px-3 py-2 text-[12px] text-muted">
          {t('team.manage.noOverrides')}
        </p>
      ) : (
        <ul className="flex flex-col gap-1.5">
          {overrides?.map((o) => (
            <li key={o.overrideId} className="rounded-[var(--radius-sm)] border border-line px-3 py-2">
              <div className="flex items-center gap-2">
                <span
                  className={[
                    'rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider',
                    o.isAllowed ? 'bg-primary-soft text-primary' : 'bg-danger-soft text-danger',
                  ].join(' ')}
                >
                  {o.isAllowed ? t('team.override.allow') : t('team.override.deny')}
                </span>
                <span className="mono flex-1 truncate text-[12px] text-ink">{o.permissionKey}</span>
                {o.expiresAt ? (
                  <span className="mono text-[11px] text-muted-2">{shortDate(o.expiresAt)}</span>
                ) : null}
              </div>
              <p className="mt-1 text-[12px] text-muted">{o.reason}</p>
            </li>
          ))}
        </ul>
      )}

      {/* Inline grant/deny form (avoids stacking slide-overs). */}
      {adding ? (
        <div className="mt-3 flex flex-col gap-3 rounded-[var(--radius)] border border-line bg-bg-2 p-3">
          <div>
            <label htmlFor="ov-perm" className={labelClass}>
              {t('team.override.permission')}
            </label>
            <Select id="ov-perm" value={permissionKey} onChange={(e) => setPermissionKey(e.target.value)}>
              <option value="">{t('team.override.selectPermission')}</option>
              {registry?.map((p) => (
                <option key={p.permissionKey} value={p.permissionKey}>
                  {p.permissionKey}
                </option>
              ))}
            </Select>
          </div>

          <div>
            <span className={labelClass}>{t('team.override.decision')}</span>
            <div role="radiogroup" aria-label={t('team.override.decision')} className="flex gap-2">
              <DecisionToggle active={isAllowed} onClick={() => setIsAllowed(true)} label={t('team.override.allow')} tone="allow" />
              <DecisionToggle active={!isAllowed} onClick={() => setIsAllowed(false)} label={t('team.override.deny')} tone="deny" />
            </div>
            <p className="mt-1 text-[11px] text-muted">{t('team.override.denyWins')}</p>
          </div>

          {selectedDef?.isDangerous ? (
            <p className="flex items-center gap-1.5 rounded-[var(--radius-sm)] bg-warn-soft px-2.5 py-1.5 text-[12px] text-warn">
              <ShieldAlert size={14} aria-hidden="true" />
              {t('team.override.dangerous')}
            </p>
          ) : null}

          <div>
            <label htmlFor="ov-reason" className={labelClass}>
              {t('team.override.reason')}
            </label>
            <TextArea
              id="ov-reason"
              rows={2}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder={t('team.override.reasonPlaceholder')}
              aria-invalid={reasonTouched && reasonMissing}
            />
            <p className={`mt-1 text-[11px] ${reasonTouched && reasonMissing ? 'text-danger' : 'text-muted'}`}>
              {reasonTouched && reasonMissing ? t('team.validation.reason') : t('team.override.reasonRequired')}
            </p>
          </div>

          <div>
            <span className={labelClass}>{t('team.override.expiry')}</span>
            <div role="radiogroup" aria-label={t('team.override.expiry')} className="grid grid-cols-3 gap-2">
              <ExpiryToggle active={expiry === 'none'} onClick={() => setExpiry('none')} label={t('team.override.noExpiry')} />
              <ExpiryToggle active={expiry === '30'} onClick={() => setExpiry('30')} label={t('team.override.in30')} />
              <ExpiryToggle active={expiry === '90'} onClick={() => setExpiry('90')} label={t('team.override.in90')} />
            </div>
          </div>

          <div className="flex justify-end gap-2">
            <Button variant="ghost" size="sm" onClick={() => setAdding(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="primary"
              size="sm"
              disabled={!permissionKey || reasonMissing || setOverride.isPending}
              onClick={() => void onSave()}
            >
              {t('team.override.save')}
            </Button>
          </div>
        </div>
      ) : null}
    </section>
  );
}

function DecisionToggle({
  active,
  onClick,
  label,
  tone,
}: {
  active: boolean;
  onClick: () => void;
  label: string;
  tone: 'allow' | 'deny';
}) {
  const activeClass = tone === 'allow' ? 'border-primary bg-primary-soft text-primary' : 'border-danger bg-danger-soft text-danger';
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      onClick={onClick}
      className={[
        'flex-1 rounded-[var(--radius-sm)] border px-2 py-2 text-[13px] transition-colors',
        active ? activeClass : 'border-line text-ink hover:bg-surface-sunk',
      ].join(' ')}
    >
      {label}
    </button>
  );
}

function ExpiryToggle({ active, onClick, label }: { active: boolean; onClick: () => void; label: string }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      onClick={onClick}
      className={[
        'rounded-[var(--radius-sm)] border px-2 py-2 text-[12px] transition-colors',
        active ? 'border-primary bg-primary text-bg' : 'border-line text-ink hover:bg-surface-sunk',
      ].join(' ')}
    >
      {label}
    </button>
  );
}

// ── 3. Effective permissions explainer ───────────────────────────────────────
function EffectiveSection({ userId }: { userId: string }) {
  const { t } = useTranslation();
  const openPanel = useUI((s) => s.openPanel);
  const { data, isLoading, isError, refetch } = useEffectivePermissions(userId);

  return (
    <section>
      <div className="mb-1 flex items-center justify-between gap-2">
        <h3 className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          {t('team.manage.effectiveHeading')}
        </h3>
        <button
          type="button"
          onClick={() => openPanel({ type: 'effectiveAccess', userId })}
          className="text-[11px] font-medium text-primary underline-offset-2 transition-colors hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
        >
          {t('team.manage.viewAllAccess')}
        </button>
      </div>
      <p className="mb-2 text-[12px] text-muted">{t('team.manage.effectiveSub')}</p>

      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-1.5" role="status" aria-busy="true">
          {Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-7 w-full" />
          ))}
        </div>
      ) : (
        <ul className="flex flex-col divide-y divide-line rounded-[var(--radius-sm)] border border-line">
          {data.map((e) => (
            <li key={e.permissionKey} className="flex items-center gap-2 px-3 py-1.5">
              <span className="mono flex-1 truncate text-[12px] text-ink">{e.permissionKey}</span>
              {e.source === 'override_grant' ? (
                <span className="rounded-full bg-accent-soft px-2 py-0.5 text-[10px] font-medium text-accent">
                  {t('team.manage.sourceOverride')}
                </span>
              ) : (
                <span className="text-[11px] text-muted-2">
                  {t('team.manage.sourceRole')}
                  {e.via ? ` · ${e.via}` : ''}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
