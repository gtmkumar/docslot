// Invites tab (#89, epic #80 Phase C) — the Team console's PENDING token-based
// invitations. Lists each invite (email · role · invited by · sent · expiry
// countdown · status) with per-row Resend + Revoke actions. The list is gated on
// tenant.users.read by the parent tab; the actions gate on tenant.users.create via
// the in-memory can() (never a network call, never a role-name branch).
//
// React 19 idiom: revoke flips the row out INSTANTLY and resend bumps the
// resend-count + expiry via useOptimistic inside a transition; the mutation
// reconciles the cache on success (surgical patch, no refetch flash) and the
// optimistic overlay reverts on error. Resend returns a NEW one-time token, handed
// to the reveal panel. States: skeleton, empty, error. NO PHI — staff emails only.

import { useOptimistic, useState, useTransition } from 'react';
import { useTranslation } from 'react-i18next';
import { Ban, Check, Clock, MailPlus, Send, ShieldX, TriangleAlert, X } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { relativeTime } from '@/lib/format';
import { toUserError } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useInvitations, useResendInvitation, useRevokeInvitation, useTenantUsers } from '../api';
import type { Invitation, InvitationStatus } from '@/lib/mock/contracts';

const SEVEN_DAYS_MS = 7 * 86_400_000; // mirrors the server invitation TTL for the optimistic bump

type OptimisticAction =
  | { type: 'revoke'; invitationId: string }
  | { type: 'resend'; invitationId: string; expiresAt: string };

export function InvitesTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);

  const canCreate = can('tenant.users.create');

  const { data, isLoading, isError, refetch } = useInvitations('pending');
  const { data: users } = useTenantUsers();
  const resend = useResendInvitation();
  const revoke = useRevokeInvitation();

  const [, startTransition] = useTransition();

  // Resolve "invited by" to a display name from the People directory (shared cache).
  const nameById = new Map((users ?? []).map((u) => [u.userId, u.fullName]));

  const invites = data?.items ?? [];
  const [optimisticInvites, applyOptimistic] = useOptimistic(
    invites,
    (state: Invitation[], action: OptimisticAction) =>
      action.type === 'revoke'
        ? state.filter((i) => i.invitationId !== action.invitationId)
        : state.map((i) =>
            i.invitationId === action.invitationId
              ? { ...i, resendCount: i.resendCount + 1, expiresAt: action.expiresAt }
              : i,
          ),
  );

  const onResend = (invitationId: string, email: string) => {
    startTransition(async () => {
      applyOptimistic({ type: 'resend', invitationId, expiresAt: new Date(Date.now() + SEVEN_DAYS_MS).toISOString() });
      try {
        const result = await resend.mutateAsync({ invitationId, idempotencyKey: idempotencyKey() });
        // Reveal the NEW one-time token (replaces nothing here — it opens over the tab).
        openPanel({ type: 'invitationToken', result, email });
      } catch (e) {
        toast.error(toUserError(e));
      }
    });
  };

  const onRevoke = (invitationId: string) => {
    startTransition(async () => {
      applyOptimistic({ type: 'revoke', invitationId });
      try {
        const res = await revoke.mutateAsync({ invitationId, idempotencyKey: idempotencyKey() });
        toast.success(res.alreadyInactive ? t('team.invites.alreadyInactive') : t('team.invites.revoked'));
      } catch (e) {
        toast.error(toUserError(e));
      }
    });
  };

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h2 className="text-base font-semibold text-ink">{t('team.invites.title')}</h2>
        <p className="mt-0.5 max-w-xl text-[13px] text-muted">{t('team.invites.subtitle')}</p>
      </div>

      {isError ? (
        <Card>
          <EmptyState
            icon={<TriangleAlert size={28} aria-hidden="true" />}
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        </Card>
      ) : isLoading ? (
        <InvitesSkeleton />
      ) : optimisticInvites.length === 0 ? (
        <Card>
          <EmptyState
            icon={<MailPlus size={28} aria-hidden="true" />}
            title={t('team.invites.emptyTitle')}
            description={t('team.invites.emptyBody')}
            actionLabel={canCreate ? t('team.invites.newInvitation') : undefined}
            onAction={canCreate ? () => openPanel({ type: 'newInvitation' }) : undefined}
          />
        </Card>
      ) : (
        <Card className="overflow-hidden">
          <ul className="flex flex-col">
            {optimisticInvites.map((inv) => (
              <InviteRow
                key={inv.invitationId}
                invite={inv}
                invitedByName={inv.invitedByUserId ? nameById.get(inv.invitedByUserId) : undefined}
                canCreate={canCreate}
                onResend={() => onResend(inv.invitationId, inv.invitedEmail)}
                onRevoke={() => onRevoke(inv.invitationId)}
              />
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

function InviteRow({
  invite,
  invitedByName,
  canCreate,
  onResend,
  onRevoke,
}: {
  invite: Invitation;
  invitedByName: string | undefined;
  canCreate: boolean;
  onResend: () => void;
  onRevoke: () => void;
}) {
  const { t } = useTranslation();
  const [confirming, setConfirming] = useState(false);

  const isExpired = new Date(invite.expiresAt).getTime() < Date.now();
  // A still-"pending" invite past its expiry reads as expired in the console even if
  // no job has flipped the DB status yet.
  const effectiveStatus: InvitationStatus =
    invite.status === 'pending' && isExpired ? 'expired' : invite.status;

  return (
    <li className="flex items-start gap-3 border-b border-line px-4 py-3 last:border-0">
      <span
        className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-surface-sunk text-muted-2"
        aria-hidden="true"
      >
        <MailPlus size={15} />
      </span>

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="truncate text-sm font-medium text-ink">{invite.invitedEmail}</span>
          <InvitationStatusPill status={effectiveStatus} />
        </div>

        <p className="mt-0.5 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[12px] text-muted">
          <span>{invite.roleName ?? t('team.invites.noRole')}</span>
          {invitedByName ? (
            <>
              <span aria-hidden="true" className="text-muted-2">·</span>
              <span>{t('team.invites.by', { name: invitedByName })}</span>
            </>
          ) : null}
          <span aria-hidden="true" className="text-muted-2">·</span>
          <span>{t('team.invites.sent', { time: relativeTime(invite.createdAt) })}</span>
        </p>

        <p className="mt-0.5 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[12px]">
          <span className={isExpired ? 'inline-flex items-center gap-1 text-danger' : 'inline-flex items-center gap-1 text-muted'}>
            <Clock size={12} aria-hidden="true" />
            {isExpired
              ? t('team.invites.expired', { time: relativeTime(invite.expiresAt) })
              : t('team.invites.expires', { time: relativeTime(invite.expiresAt) })}
          </span>
          {invite.resendCount > 0 ? (
            <>
              <span aria-hidden="true" className="text-muted-2">·</span>
              <span className="text-muted-2">{t('team.invites.resent', { count: invite.resendCount })}</span>
            </>
          ) : null}
        </p>
      </div>

      {/* Actions — gated on tenant.users.create. Revoke arms an inline confirm so a
          misclick can't silently kill a live invitation link. */}
      {canCreate ? (
        confirming ? (
          <div className="flex shrink-0 items-center gap-2">
            <span className="hidden text-[12px] text-muted sm:inline">{t('team.invites.confirmRevoke')}</span>
            <Button variant="ghost" size="sm" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="danger"
              size="sm"
              onClick={() => {
                setConfirming(false);
                onRevoke();
              }}
            >
              {t('team.invites.revoke')}
            </Button>
          </div>
        ) : (
          <div className="flex shrink-0 items-center gap-1">
            <Button variant="ghost" size="sm" onClick={onResend}>
              <Send size={14} aria-hidden="true" />
              {t('team.invites.resend')}
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setConfirming(true)}>
              <Ban size={14} aria-hidden="true" />
              {t('team.invites.revoke')}
            </Button>
          </div>
        )
      ) : null}
    </li>
  );
}

interface StatusConfig {
  icon: typeof Check;
  className: string;
  labelKey: string;
}

// Status pill (REACT_SKILL pattern 12: icon + text + color, never color alone).
// Terracotta/accent is reserved for negative MEDICAL states — these are
// administrative, so expired uses the danger token and revoked stays neutral/muted.
const STATUS_CONFIG: Record<InvitationStatus, StatusConfig> = {
  pending: { icon: Clock, className: 'bg-warn-soft text-warn border-warn-soft', labelKey: 'team.invites.status.pending' },
  accepted: { icon: Check, className: 'bg-primary-soft text-primary border-primary-soft', labelKey: 'team.invites.status.accepted' },
  revoked: { icon: ShieldX, className: 'bg-surface-sunk text-muted border-line', labelKey: 'team.invites.status.revoked' },
  expired: { icon: X, className: 'bg-danger-soft text-danger border-danger-soft', labelKey: 'team.invites.status.expired' },
};

function InvitationStatusPill({ status }: { status: InvitationStatus }) {
  const { t } = useTranslation();
  const cfg = STATUS_CONFIG[status];
  const Icon = cfg.icon;
  return (
    <span className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium ${cfg.className}`}>
      <Icon size={11} aria-hidden="true" />
      {t(cfg.labelKey)}
    </span>
  );
}

function InvitesSkeleton() {
  return (
    <Card className="overflow-hidden" aria-busy="true" role="status">
      <ul className="flex flex-col">
        {Array.from({ length: 3 }).map((_, i) => (
          <li key={i} className="flex items-start gap-3 border-b border-line px-4 py-3 last:border-0">
            <Skeleton className="h-8 w-8 rounded-full" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-3 w-52" />
              <Skeleton className="h-3 w-40" />
              <Skeleton className="h-3 w-28" />
            </div>
            <Skeleton className="h-7 w-28" />
          </li>
        ))}
      </ul>
    </Card>
  );
}
