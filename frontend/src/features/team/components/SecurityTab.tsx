// Security tab (#91) — the tenant SECURITY POLICY sections stacked ABOVE the
// existing Active-sessions panel (#87, unchanged). Three sections mirror the mockup:
//   1. Two-factor authentication  — mfaPolicy tier + "N of M have 2FA" + pending warning
//   2. Password & session policy   — min length, idle sign-out, new-device verification
//   3. Access restrictions         — login hours, IP allow-list, receptionist masking
//
// Bound to GET/PUT /security/policy. The tab only mounts when tenant.settings.read is
// held (TeamScreen gates it); editing gates on tenant.settings.update — without it the
// whole form renders READ-ONLY. The IP allow-list plane gates separately on
// platform.ip_allowlist.manage. Saves are OPTIMISTIC + reconcile (useUpdateSecurityPolicy);
// each write carries a stable Idempotency-Key. States: skeleton, error (+retry); the
// singleton policy always merges to defaults so there is no "empty" policy — the empty
// state lives on the IP allow-list. NO PHI (tenant configuration + CIDR metadata only).
//
// Honesty (design DNA) — no false assurance. The new-device toggle is labelled that
// e-mail DELIVERY is not wired yet (#93 family): tracking is stored now, the code is sent
// later. Idle timeout is stored + range-validated but NOT enforced server-side yet, so it
// carries a "Not yet enforced" badge. The 2FA "Required" tiers force enrolment on next
// sign-in, but there is no TOTP enrolment flow yet — an enrol-pending note sits under the
// radios. The receptionist mask governs the patient PHONE NUMBER only (email/DOB stay
// visible), so its label/subtext name the phone number rather than "sensitive data".

import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Info, KeyRound, Lock, ShieldCheck, TriangleAlert } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { Toggle } from '@/components/ui/Toggle';
import { Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import type { MfaPolicy, SecurityPolicyInput, SecurityPolicyView } from '@/lib/mock/contracts';
import { useSecurityPolicy, useTenantUsers, useUpdateSecurityPolicy } from '../api';
import { IpAllowlistEditor } from './IpAllowlistEditor';
import { SessionsTab } from './SessionsTab';

const IDLE_PRESETS = [15, 30, 60, 120, 240, 480, 1440];

/** Editable slice of the view (drops the derived pending count). */
function toInput(v: SecurityPolicyView): SecurityPolicyInput {
  return {
    mfaPolicy: v.mfaPolicy,
    minPasswordLength: v.minPasswordLength,
    idleTimeoutMinutes: v.idleTimeoutMinutes,
    requireNewDeviceVerification: v.requireNewDeviceVerification,
    restrictLoginHours: v.restrictLoginHours,
    loginHoursStart: v.loginHoursStart,
    loginHoursEnd: v.loginHoursEnd,
    doctorsExemptFromHours: v.doctorsExemptFromHours,
    ipAllowlistEnabled: v.ipAllowlistEnabled,
    maskSensitiveForReceptionist: v.maskSensitiveForReceptionist,
  };
}

export function SecurityTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const canUpdate = can('tenant.settings.update');
  const canManageIp = can('platform.ip_allowlist.manage');

  const { data: policy, isLoading, isError, refetch } = useSecurityPolicy();

  return (
    <div className="flex flex-col gap-8">
      <div>
        <h2 className="text-base font-semibold text-ink">{t('team.security.title')}</h2>
        <p className="mt-0.5 max-w-xl text-[13px] text-muted">{t('team.security.subtitle')}</p>
      </div>

      {!canUpdate ? (
        <p className="flex items-center gap-1.5 rounded-[var(--radius-sm)] bg-surface-sunk px-3 py-2 text-[12px] text-muted">
          <Info size={13} aria-hidden="true" />
          {t('team.security.readOnly')}
        </p>
      ) : null}

      {isError ? (
        <Card className="p-0">
          <EmptyState
            icon={<TriangleAlert size={28} aria-hidden="true" />}
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        </Card>
      ) : isLoading || !policy ? (
        <PolicySkeleton />
      ) : (
        <PolicyForm policy={policy} canUpdate={canUpdate} canManageIp={canManageIp} />
      )}

      {/* The #87 Active-sessions panel — unchanged. */}
      <SessionsTab />
    </div>
  );
}

function PolicyForm({
  policy,
  canUpdate,
  canManageIp,
}: {
  policy: SecurityPolicyView;
  canUpdate: boolean;
  canManageIp: boolean;
}) {
  const { t } = useTranslation();
  const update = useUpdateSecurityPolicy();
  const { data: users } = useTenantUsers();

  const [draft, setDraft] = useState<SecurityPolicyInput>(() => toInput(policy));

  // "N of M staff have 2FA" — derived from the People directory when the caller can
  // read it (no role logic). Absent (viewer without tenant.users.read) → line hidden.
  const activeStaff = users?.filter((u) => u.isActive);
  const staffTotal = activeStaff?.length;
  const staffWithMfa = activeStaff?.filter((u) => u.mfaEnabled).length;

  const dirty = JSON.stringify(draft) !== JSON.stringify(toInput(policy));
  const minLenError = draft.minPasswordLength < 8 || draft.minPasswordLength > 128;
  const saveDisabled = !canUpdate || !dirty || minLenError || update.isPending;

  // The pending-enrolment count is SERVER truth for the SAVED policy (recomputed on
  // PUT). When the 2FA tier is edited but not yet saved, tell the user it recalculates.
  const mfaTierDirty = draft.mfaPolicy !== policy.mfaPolicy;

  const set = <K extends keyof SecurityPolicyInput>(key: K, value: SecurityPolicyInput[K]) =>
    setDraft((d) => ({ ...d, [key]: value }));

  const onSave = async () => {
    if (saveDisabled) return;
    try {
      const updated = await update.mutateAsync({ policy: draft, idempotencyKey: idempotencyKey() });
      setDraft(toInput(updated));
      toast.success(t('team.security.saved'));
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const idleOptions = IDLE_PRESETS.includes(draft.idleTimeoutMinutes)
    ? IDLE_PRESETS
    : [draft.idleTimeoutMinutes, ...IDLE_PRESETS].sort((a, b) => a - b);
  const idleLabel = (mins: number) =>
    mins % 60 === 0 ? t('team.security.session.hoursOption', { count: mins / 60 }) : t('team.security.session.minutesOption', { count: mins });

  return (
    <div className="flex flex-col gap-5">
      {/* ── 1. Two-factor authentication ─────────────────────────────────────── */}
      <SectionCard icon={<ShieldCheck size={16} aria-hidden="true" />} title={t('team.security.mfa.heading')} description={t('team.security.mfa.sub')}>
        <div role="radiogroup" aria-label={t('team.security.mfa.heading')} className="flex flex-col gap-2">
          <RadioCard
            active={draft.mfaPolicy === 'optional'}
            disabled={!canUpdate}
            onSelect={() => set('mfaPolicy', 'optional' satisfies MfaPolicy)}
            title={t('team.security.mfa.optional')}
            description={t('team.security.mfa.optionalSub')}
          />
          <RadioCard
            active={draft.mfaPolicy === 'owners_admins'}
            disabled={!canUpdate}
            onSelect={() => set('mfaPolicy', 'owners_admins' satisfies MfaPolicy)}
            title={t('team.security.mfa.ownersAdmins')}
            description={t('team.security.mfa.ownersAdminsSub')}
          />
          <RadioCard
            active={draft.mfaPolicy === 'all'}
            disabled={!canUpdate}
            onSelect={() => set('mfaPolicy', 'all' satisfies MfaPolicy)}
            title={t('team.security.mfa.all')}
            description={t('team.security.mfa.allSub')}
          />
        </div>

        <div className="mt-3 flex flex-col gap-2">
          {draft.mfaPolicy !== 'optional' ? (
            <p className="flex items-start gap-1.5 text-[11px] text-muted-2">
              <Info size={12} aria-hidden="true" className="mt-0.5 shrink-0" />
              {t('team.security.mfa.enrolNote')}
            </p>
          ) : null}
          {staffTotal !== undefined ? (
            <p className="text-[12px] text-muted">
              {t('team.security.mfa.coverage', { withMfa: staffWithMfa ?? 0, total: staffTotal })}
            </p>
          ) : null}
          {policy.staffPendingMfaEnrolment > 0 ? (
            <p className="flex items-start gap-1.5 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2 text-[12px] text-warn">
              <TriangleAlert size={14} aria-hidden="true" className="mt-0.5 shrink-0" />
              {t('team.security.mfa.pending', { count: policy.staffPendingMfaEnrolment })}
            </p>
          ) : null}
          {mfaTierDirty ? <p className="text-[11px] text-muted-2">{t('team.security.mfa.recalcNote')}</p> : null}
        </div>
      </SectionCard>

      {/* ── 2. Password & session policy ─────────────────────────────────────── */}
      <SectionCard icon={<KeyRound size={16} aria-hidden="true" />} title={t('team.security.session.heading')} description={t('team.security.session.sub')}>
        <div className="grid gap-4 sm:grid-cols-2">
          <div>
            <label htmlFor="min-pw" className={labelClass}>
              {t('team.security.session.minLength')}
            </label>
            <TextInput
              id="min-pw"
              type="number"
              min={8}
              max={128}
              disabled={!canUpdate}
              value={draft.minPasswordLength}
              onChange={(e) => set('minPasswordLength', Number.parseInt(e.target.value, 10) || 0)}
              aria-invalid={minLenError}
            />
            <p className={`mt-1 text-[11px] ${minLenError ? 'text-danger' : 'text-muted-2'}`}>
              {minLenError ? t('team.security.session.minLengthError') : t('team.security.session.minLengthHint')}
            </p>
          </div>

          <div>
            <div className="mb-1 flex flex-wrap items-center gap-2">
              <label htmlFor="idle" className="block text-[11px] font-semibold uppercase tracking-wider text-muted-2">
                {t('team.security.session.idle')}
              </label>
              <span className="inline-flex items-center rounded-full bg-surface-sunk px-1.5 py-[1px] text-[10px] font-semibold uppercase tracking-wide text-muted-2">
                {t('team.security.session.notEnforced')}
              </span>
            </div>
            <Select
              id="idle"
              disabled={!canUpdate}
              value={draft.idleTimeoutMinutes}
              onChange={(e) => set('idleTimeoutMinutes', Number.parseInt(e.target.value, 10))}
            >
              {idleOptions.map((mins) => (
                <option key={mins} value={mins}>
                  {idleLabel(mins)}
                </option>
              ))}
            </Select>
            <p className="mt-1 text-[11px] text-muted-2">{t('team.security.session.idleHint')}</p>
          </div>
        </div>

        <ToggleRow
          id="new-device"
          checked={draft.requireNewDeviceVerification}
          disabled={!canUpdate}
          onChange={(v) => set('requireNewDeviceVerification', v)}
          title={t('team.security.session.newDevice')}
          description={t('team.security.session.newDeviceSub')}
        />
      </SectionCard>

      {/* ── 3. Access restrictions ───────────────────────────────────────────── */}
      <SectionCard icon={<Lock size={16} aria-hidden="true" />} title={t('team.security.access.heading')} description={t('team.security.access.sub')}>
        <ToggleRow
          id="login-hours"
          checked={draft.restrictLoginHours}
          disabled={!canUpdate}
          onChange={(v) => set('restrictLoginHours', v)}
          title={t('team.security.access.loginHours')}
          description={t('team.security.access.loginHoursSub')}
        />
        {draft.restrictLoginHours ? (
          <div className="ml-0 flex flex-col gap-3 rounded-[var(--radius-sm)] border border-line bg-bg-2 p-3">
            <div className="flex flex-wrap items-end gap-3">
              <div>
                <label htmlFor="hours-start" className={labelClass}>
                  {t('team.security.access.from')}
                </label>
                <TextInput
                  id="hours-start"
                  type="time"
                  disabled={!canUpdate}
                  value={draft.loginHoursStart}
                  onChange={(e) => set('loginHoursStart', e.target.value)}
                  className="w-32"
                />
              </div>
              <div>
                <label htmlFor="hours-end" className={labelClass}>
                  {t('team.security.access.to')}
                </label>
                <TextInput
                  id="hours-end"
                  type="time"
                  disabled={!canUpdate}
                  value={draft.loginHoursEnd}
                  onChange={(e) => set('loginHoursEnd', e.target.value)}
                  className="w-32"
                />
              </div>
              <span className="pb-2 text-[11px] text-muted-2">{t('team.security.access.hoursTz')}</span>
            </div>
            <div className="flex items-center gap-2 text-[12px] text-ink">
              <Toggle
                id="doctors-exempt"
                checked={draft.doctorsExemptFromHours}
                disabled={!canUpdate}
                onChange={(v) => set('doctorsExemptFromHours', v)}
                label={t('team.security.access.doctorsExempt')}
              />
              <label htmlFor="doctors-exempt">{t('team.security.access.doctorsExempt')}</label>
            </div>
          </div>
        ) : null}

        <div className="border-t border-line pt-4">
          <ToggleRow
            id="ip-allowlist"
            checked={draft.ipAllowlistEnabled}
            disabled={!canUpdate}
            onChange={(v) => set('ipAllowlistEnabled', v)}
            title={t('team.security.access.ipEnable')}
            description={t('team.security.access.ipEnableSub')}
          />
          <div className="mt-3">
            <IpAllowlistEditor canManage={canManageIp} enabled={draft.ipAllowlistEnabled} />
          </div>
        </div>

        <div className="border-t border-line pt-4">
          <ToggleRow
            id="mask-sensitive"
            checked={draft.maskSensitiveForReceptionist}
            disabled={!canUpdate}
            onChange={(v) => set('maskSensitiveForReceptionist', v)}
            title={t('team.security.access.mask')}
            description={t('team.security.access.maskSub')}
          />
        </div>
      </SectionCard>

      {/* Save bar — only meaningful when the caller can edit + has changes. */}
      {canUpdate && dirty ? (
        <div className="sticky bottom-0 flex items-center justify-between gap-3 rounded-[var(--radius)] border border-line bg-surface px-4 py-3 shadow-[var(--shadow-sm)]">
          <span className="flex items-center gap-1.5 text-[12px] text-muted">
            <Info size={13} aria-hidden="true" />
            {t('team.security.unsaved')}
          </span>
          <div className="flex shrink-0 gap-2">
            <Button variant="ghost" size="sm" disabled={update.isPending} onClick={() => setDraft(toInput(policy))}>
              {t('team.security.discard')}
            </Button>
            <Button variant="primary" size="sm" disabled={saveDisabled} onClick={() => void onSave()}>
              {t('team.security.save')}
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function SectionCard({
  icon,
  title,
  description,
  children,
}: {
  icon: ReactNode;
  title: string;
  description: string;
  children: ReactNode;
}) {
  return (
    <Card className="p-4 sm:p-5">
      <div className="mb-4 flex items-start gap-2.5">
        <span className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-primary-soft text-primary">
          {icon}
        </span>
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-ink">{title}</h3>
          <p className="mt-0.5 text-[12px] text-muted">{description}</p>
        </div>
      </div>
      <div className="flex flex-col gap-4">{children}</div>
    </Card>
  );
}

function RadioCard({
  active,
  disabled,
  onSelect,
  title,
  description,
}: {
  active: boolean;
  disabled?: boolean;
  onSelect: () => void;
  title: string;
  description: string;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      disabled={disabled}
      onClick={onSelect}
      className={[
        'flex items-start gap-3 rounded-[var(--radius-sm)] border px-3 py-2.5 text-left transition-colors',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:cursor-not-allowed disabled:opacity-60',
        active ? 'border-primary bg-primary-soft' : 'border-line hover:bg-surface-sunk',
      ].join(' ')}
    >
      <span
        aria-hidden="true"
        className={[
          'mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border',
          active ? 'border-primary' : 'border-line',
        ].join(' ')}
      >
        {active ? <span className="h-2 w-2 rounded-full bg-primary" /> : null}
      </span>
      <span className="min-w-0">
        <span className="block text-[13px] font-medium text-ink">{title}</span>
        <span className="mt-0.5 block text-[12px] text-muted">{description}</span>
      </span>
    </button>
  );
}

function ToggleRow({
  id,
  checked,
  disabled,
  onChange,
  title,
  description,
}: {
  id: string;
  checked: boolean;
  disabled?: boolean;
  onChange: (next: boolean) => void;
  title: string;
  description: string;
}) {
  const descId = `${id}-desc`;
  return (
    <div className="flex items-start justify-between gap-4">
      <div className="min-w-0">
        <label htmlFor={id} className="block text-[13px] font-medium text-ink">
          {title}
        </label>
        <p id={descId} className="mt-0.5 text-[12px] text-muted">
          {description}
        </p>
      </div>
      <Toggle id={id} checked={checked} disabled={disabled} onChange={onChange} label={title} describedBy={descId} />
    </div>
  );
}

function PolicySkeleton() {
  return (
    <div className="flex flex-col gap-5" role="status" aria-busy="true">
      {Array.from({ length: 3 }).map((_, s) => (
        <Card key={s} className="p-4 sm:p-5">
          <div className="mb-4 flex items-center gap-2.5">
            <Skeleton className="h-7 w-7 rounded-full" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-3.5 w-40" />
              <Skeleton className="h-3 w-64" />
            </div>
          </div>
          <div className="flex flex-col gap-3">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        </Card>
      ))}
    </div>
  );
}
