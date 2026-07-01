// Active sessions (#87) — rendered inside the Team console "Security" tab (the
// rest of Security settings are #91). Lists every active session grouped by user
// (user · last active · ip · self badge), each with a Revoke action, plus a
// "Sign out all sessions" per user behind an inline confirm. Gated on
// tenant.users.update by the parent tab.
//
// React 19 idiom: revokes flip the list INSTANTLY via useOptimistic inside a
// transition; the mutation reconciles the cache on success (surgical removal, no
// refetch flash) and the optimistic overlay reverts on error. NO PHI — session
// users are staff identities; the ip shows its resolved city when present ("IP ·
// city", #94), raw IP only when the geo resolver is offline. States: skeleton,
// empty, error.

import { useOptimistic, useState, useTransition } from 'react';
import { useTranslation } from 'react-i18next';
import { LogOut, MonitorSmartphone, TriangleAlert } from 'lucide-react';
import { toast } from 'sonner';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { idempotencyKey } from '@/lib/api-client';
import { relativeTime } from '@/lib/format';
import { toUserError } from '@/lib/backend';
import { useActiveSessions, useRevokeAllSessions, useRevokeSession } from '../api';
import type { ActiveSession } from '@/lib/mock/contracts';

type OptimisticAction = { type: 'revoke'; sessionId: string } | { type: 'revokeAll'; userId: string };

interface UserGroup {
  userId: string;
  userName: string;
  userEmail: string | null;
  sessions: ActiveSession[];
}

function groupByUser(sessions: ActiveSession[]): UserGroup[] {
  const map = new Map<string, UserGroup>();
  for (const s of sessions) {
    const g = map.get(s.userId);
    if (g) g.sessions.push(s);
    else map.set(s.userId, { userId: s.userId, userName: s.userName, userEmail: s.userEmail, sessions: [s] });
  }
  return [...map.values()];
}

export function SessionsTab() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useActiveSessions();
  const revokeOne = useRevokeSession();
  const revokeAll = useRevokeAllSessions();

  const [, startTransition] = useTransition();
  const [confirmUserId, setConfirmUserId] = useState<string | null>(null);

  const sessions = data ?? [];
  const [optimisticSessions, applyOptimistic] = useOptimistic(
    sessions,
    (state: ActiveSession[], action: OptimisticAction) =>
      action.type === 'revoke'
        ? state.filter((s) => s.sessionId !== action.sessionId)
        : state.filter((s) => s.userId !== action.userId),
  );

  const onRevoke = (sessionId: string) => {
    startTransition(async () => {
      applyOptimistic({ type: 'revoke', sessionId });
      try {
        await revokeOne.mutateAsync({ sessionId, idempotencyKey: idempotencyKey() });
        toast.success(t('team.sessions.revoked'));
      } catch (e) {
        toast.error(toUserError(e));
      }
    });
  };

  const onRevokeAll = (userId: string) => {
    setConfirmUserId(null);
    startTransition(async () => {
      applyOptimistic({ type: 'revokeAll', userId });
      try {
        const res = await revokeAll.mutateAsync({ userId, idempotencyKey: idempotencyKey() });
        toast.success(t('team.sessions.signedOutAll', { count: res.revokedCount }));
      } catch (e) {
        toast.error(toUserError(e));
      }
    });
  };

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h2 className="text-base font-semibold text-ink">{t('team.sessions.title')}</h2>
        <p className="mt-0.5 max-w-xl text-[13px] text-muted">{t('team.sessions.subtitle')}</p>
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
        <SessionsSkeleton />
      ) : optimisticSessions.length === 0 ? (
        <Card>
          <EmptyState
            icon={<MonitorSmartphone size={28} aria-hidden="true" />}
            title={t('team.sessions.emptyTitle')}
            description={t('team.sessions.emptyBody')}
          />
        </Card>
      ) : (
        <div className="flex flex-col gap-3">
          {groupByUser(optimisticSessions).map((group) => (
            <UserSessions
              key={group.userId}
              group={group}
              confirming={confirmUserId === group.userId}
              onArmRevokeAll={() => setConfirmUserId(group.userId)}
              onCancelRevokeAll={() => setConfirmUserId(null)}
              onConfirmRevokeAll={() => onRevokeAll(group.userId)}
              onRevoke={onRevoke}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function UserSessions({
  group,
  confirming,
  onArmRevokeAll,
  onCancelRevokeAll,
  onConfirmRevokeAll,
  onRevoke,
}: {
  group: UserGroup;
  confirming: boolean;
  onArmRevokeAll: () => void;
  onCancelRevokeAll: () => void;
  onConfirmRevokeAll: () => void;
  onRevoke: (sessionId: string) => void;
}) {
  const { t } = useTranslation();
  const multiple = group.sessions.length > 1;

  return (
    <Card className="overflow-hidden">
      <header className="flex items-center gap-3 border-b border-line px-4 py-3">
        <Avatar name={group.userName} size="sm" />
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-medium text-ink">{group.userName}</p>
          {group.userEmail ? <p className="truncate text-[12px] text-muted">{group.userEmail}</p> : null}
        </div>
        <span className="shrink-0 text-[12px] text-muted-2">
          {t('team.sessions.count', { count: group.sessions.length })}
        </span>
        {/* Sign out all — only meaningful when the user has >1 session. */}
        {multiple && !confirming ? (
          <Button variant="ghost" size="sm" onClick={onArmRevokeAll}>
            <LogOut size={14} aria-hidden="true" />
            {t('team.sessions.signOutAll')}
          </Button>
        ) : null}
      </header>

      {/* Inline confirm for the destructive bulk action. */}
      {confirming ? (
        <div className="flex flex-col gap-2 border-b border-line bg-bg-2 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-[12px] text-muted">
            {t('team.sessions.confirmAll', { name: group.userName, count: group.sessions.length })}
          </p>
          <div className="flex shrink-0 justify-end gap-2">
            <Button variant="ghost" size="sm" onClick={onCancelRevokeAll}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="sm" onClick={onConfirmRevokeAll}>
              {t('team.sessions.signOutAll')}
            </Button>
          </div>
        </div>
      ) : null}

      <ul className="flex flex-col">
        {group.sessions.map((s) => (
          <SessionRow key={s.sessionId} session={s} onRevoke={() => onRevoke(s.sessionId)} />
        ))}
      </ul>
    </Card>
  );
}

function SessionRow({ session, onRevoke }: { session: ActiveSession; onRevoke: () => void }) {
  const { t } = useTranslation();
  return (
    <li className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
      <span
        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-surface-sunk text-muted-2"
        aria-hidden="true"
      >
        <MonitorSmartphone size={15} />
      </span>

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          {/* IP + resolved city (#94) — "103.21.4.2 · Mumbai"; raw IP only when the
              geo resolver returned null (offline default). */}
          <span className="text-[12px] text-ink">
            <span className="mono">{session.ipAddress ?? t('team.sessions.unknownIp')}</span>
            {session.ipAddress && session.city ? (
              <span className="text-muted">
                <span aria-hidden="true"> · </span>
                {session.city}
              </span>
            ) : null}
          </span>
          {session.isSelf ? (
            <span className="rounded-full bg-primary-soft px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-primary">
              {t('team.sessions.thisDevice')}
            </span>
          ) : null}
        </div>
        <p className="mt-0.5 text-[12px] text-muted">
          {t('team.sessions.lastActive', { time: relativeTime(session.lastActivityAt) })}
        </p>
      </div>

      <Button variant="ghost" size="sm" onClick={onRevoke}>
        <LogOut size={14} aria-hidden="true" />
        {t('team.sessions.revoke')}
      </Button>
    </li>
  );
}

function SessionsSkeleton() {
  return (
    <div className="flex flex-col gap-3" role="status" aria-busy="true">
      {Array.from({ length: 2 }).map((_, g) => (
        <Card key={g} className="overflow-hidden">
          <div className="flex items-center gap-3 border-b border-line px-4 py-3">
            <Skeleton className="h-8 w-8 rounded-full" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-3 w-40" />
              <Skeleton className="h-3 w-56" />
            </div>
          </div>
          <ul className="flex flex-col">
            {Array.from({ length: 2 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-8 w-8 rounded-full" />
                <div className="flex flex-1 flex-col gap-2">
                  <Skeleton className="h-3 w-28" />
                  <Skeleton className="h-3 w-20" />
                </div>
                <Skeleton className="h-7 w-20" />
              </li>
            ))}
          </ul>
        </Card>
      ))}
    </div>
  );
}
