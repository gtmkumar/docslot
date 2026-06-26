// Role privilege-matrix slide-over (the heart of the Roles screen). Renders
// GET /iam/roles/{id}/matrix as grouped module sections (icon + name +
// granted/total tally), each with action cells shown as checkboxes.
//
//  - Built-in / non-editable role  → the grid is READ-ONLY and a prominent
//    Duplicate CTA opens the duplicate slide-over (clone + grants → new role).
//  - Editable (custom) role        → cells are live: ON → POST grant, OFF →
//    DELETE revoke, applied OPTIMISTICALLY via useOptimistic and reconciled on
//    settle (a 403 rolls the matrix back and surfaces a toast via toUserError).
//  - Dangerous cells (isDangerous) carry a red-dot affordance and require an
//    inline confirm step before the toggle fires (no centered modal, no new dep).
//  - Unlicensed modules render disabled / greyed ("Module not licensed").
//
// Toggle + Duplicate gate on tenant.roles.assign (in-memory can()); the DB is the
// real boundary and re-checks, so built-in roles stay read-only regardless.

import { useOptimistic, useState, useTransition } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertCircle, Check, Copy, Lock, ShieldAlert } from 'lucide-react';
import { toast } from 'sonner';
import { SlideOver } from '@/components/ui/SlideOver';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useRoleMatrix, useToggleRolePermission } from '../api';
import type { RoleMatrix } from '@/lib/mock/contracts';

export function RoleMatrixPanel({ roleId, open, onClose }: { roleId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const { data: matrix, isLoading, isError, refetch } = useRoleMatrix(roleId);

  const canToggle = can('tenant.roles.assign');

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('team.matrix.eyebrow')}
      title={matrix?.name ?? t('team.matrix.eyebrow')}
      description={t('team.matrix.description')}
      footer={
        matrix && (matrix.isSystem || !matrix.editable) && canToggle ? (
          <Button
            variant="primary"
            size="md"
            onClick={() => openPanel({ type: 'duplicateRole', roleId: matrix.roleId })}
          >
            <Copy size={15} aria-hidden="true" />
            {t('team.matrix.duplicate')}
          </Button>
        ) : null
      }
    >
      {isError ? (
        <EmptyState
          title={t('error.genericTitle')}
          description={t('error.genericBody')}
          actionLabel={t('common.retry')}
          onAction={() => void refetch()}
        />
      ) : isLoading || !matrix ? (
        <MatrixSkeleton />
      ) : (
        <MatrixBody matrix={matrix} canToggle={canToggle} />
      )}
    </SlideOver>
  );
}

function MatrixSkeleton() {
  return (
    <div className="flex flex-col gap-4" role="status" aria-busy="true">
      {Array.from({ length: 3 }).map((_, s) => (
        <div key={s} className="flex flex-col gap-2 rounded-[var(--radius)] border border-line p-3">
          <Skeleton className="h-4 w-32" />
          <div className="grid grid-cols-2 gap-2">
            {Array.from({ length: 4 }).map((_, i) => (
              <Skeleton key={i} className="h-9 w-full" />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

// An optimistic cell flip: { permissionId → granted }.
type OptimisticFlip = { permissionId: string; granted: boolean };

function MatrixBody({ matrix, canToggle }: { matrix: RoleMatrix; canToggle: boolean }) {
  const { t } = useTranslation();
  const toggle = useToggleRolePermission();
  const [, startTransition] = useTransition();

  const editable = matrix.editable && !matrix.isSystem && canToggle;

  // Optimistic overlay of cell states, keyed by permissionId. The base comes from
  // the (already-fetched) matrix; useOptimistic applies in-flight flips on top so
  // the checkbox reflects the intent instantly and reverts if the mutation throws.
  const baseGranted = new Map<string, boolean>();
  for (const m of matrix.modules) for (const c of m.cells) baseGranted.set(c.permissionId, c.granted);

  const [optimistic, applyFlip] = useOptimistic(baseGranted, (state, flip: OptimisticFlip) => {
    const next = new Map(state);
    next.set(flip.permissionId, flip.granted);
    return next;
  });

  const runToggle = (permissionId: string, nextGranted: boolean) => {
    startTransition(async () => {
      applyFlip({ permissionId, granted: nextGranted });
      try {
        await toggle.mutateAsync({ roleId: matrix.roleId, permissionId, granted: nextGranted, idempotencyKey: idempotencyKey() });
      } catch (e) {
        // The optimistic flip auto-reverts when the transition rejects; tell the user why.
        toast.error(toUserError(e));
      }
    });
  };

  const grantedNow = [...optimistic.values()].filter(Boolean).length;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="mono text-[12px] text-muted">{matrix.roleKey}</span>
        <span
          className={[
            'rounded-full px-2 py-0.5 text-[11px]',
            matrix.isSystem ? 'bg-surface-sunk text-muted' : 'bg-primary-soft text-primary',
          ].join(' ')}
        >
          {matrix.isSystem ? t('team.systemRole') : t('team.customRole')}
        </span>
        <span className="ml-auto text-[12px] text-muted">
          {t('team.matrix.grantedOf', { granted: grantedNow, total: matrix.totalCount })}
        </span>
      </div>

      {matrix.description ? <p className="text-[12px] text-muted">{matrix.description}</p> : null}

      {!editable ? (
        <p className="flex items-center gap-1.5 rounded-[var(--radius-sm)] bg-surface-sunk px-3 py-2 text-[12px] text-muted">
          <Lock size={13} aria-hidden="true" />
          {matrix.isSystem ? t('team.matrix.readOnlySystem') : t('team.matrix.readOnlyPerm')}
        </p>
      ) : null}

      {matrix.modules.length === 0 ? (
        <EmptyState title={t('team.matrix.noModules')} />
      ) : (
        <div className="flex flex-col gap-3">
          {matrix.modules.map((mod) => (
            <ModuleSection
              key={mod.resourceKey}
              mod={mod}
              optimistic={optimistic}
              editable={editable}
              onToggle={runToggle}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function ModuleSection({
  mod,
  optimistic,
  editable,
  onToggle,
}: {
  mod: RoleMatrix['modules'][number];
  optimistic: Map<string, boolean>;
  editable: boolean;
  onToggle: (permissionId: string, nextGranted: boolean) => void;
}) {
  const { t } = useTranslation();
  const grantedNow = mod.cells.filter((c) => optimistic.get(c.permissionId) ?? c.granted).length;
  const disabled = !mod.licensed;

  return (
    <section
      className={[
        'rounded-[var(--radius)] border p-3',
        disabled ? 'border-line bg-surface-sunk opacity-60' : 'border-line',
      ].join(' ')}
      aria-disabled={disabled || undefined}
    >
      <div className="mb-2 flex items-center gap-2">
        <h3 className="text-[13px] font-semibold text-ink">{mod.name}</h3>
        <span className="mono text-[11px] text-muted-2">
          {t('team.matrix.grantedOf', { granted: grantedNow, total: mod.totalCount })}
        </span>
        {disabled ? (
          <span className="ml-auto inline-flex items-center gap-1 rounded-full bg-surface px-2 py-0.5 text-[10px] text-muted-2">
            <Lock size={10} aria-hidden="true" />
            {t('team.matrix.notLicensed')}
          </span>
        ) : null}
      </div>
      {mod.description ? <p className="mb-2 text-[11px] text-muted">{mod.description}</p> : null}

      <div className="grid grid-cols-2 gap-1.5">
        {mod.cells.map((cell) => (
          <Cell
            key={cell.permissionId}
            cell={cell}
            granted={optimistic.get(cell.permissionId) ?? cell.granted}
            editable={editable && !disabled}
            onToggle={onToggle}
          />
        ))}
      </div>
    </section>
  );
}

function Cell({
  cell,
  granted,
  editable,
  onToggle,
}: {
  cell: RoleMatrix['modules'][number]['cells'][number];
  granted: boolean;
  editable: boolean;
  onToggle: (permissionId: string, nextGranted: boolean) => void;
}) {
  const { t } = useTranslation();
  // Dangerous cells require an inline confirm step before the toggle fires.
  const [confirming, setConfirming] = useState(false);

  const labelId = `cell-${cell.permissionId}`;

  const request = () => {
    if (!editable) return;
    if (cell.isDangerous) {
      setConfirming(true);
      return;
    }
    onToggle(cell.permissionId, !granted);
  };

  const confirm = () => {
    setConfirming(false);
    onToggle(cell.permissionId, !granted);
  };

  return (
    <div className="flex flex-col">
      <button
        type="button"
        role="checkbox"
        aria-checked={granted}
        aria-labelledby={labelId}
        disabled={!editable}
        onClick={request}
        className={[
          'flex items-center gap-2 rounded-[var(--radius-sm)] border px-2.5 py-2 text-left transition-colors',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary',
          granted ? 'border-primary bg-primary-soft' : 'border-line bg-surface',
          editable ? 'hover:bg-surface-sunk' : 'cursor-default opacity-80',
        ].join(' ')}
      >
        <span
          aria-hidden="true"
          className={[
            'flex h-4 w-4 shrink-0 items-center justify-center rounded-[4px] border',
            granted ? 'border-primary bg-primary text-bg' : 'border-line-strong bg-surface',
          ].join(' ')}
        >
          {granted ? <Check size={11} strokeWidth={3} /> : null}
        </span>
        <span id={labelId} className="min-w-0 flex-1 truncate text-[12px] text-ink">
          {cell.actionName}
        </span>
        {cell.isDangerous ? (
          <span
            className="h-1.5 w-1.5 shrink-0 rounded-full bg-danger"
            aria-label={t('team.matrix.dangerous')}
            title={t('team.matrix.dangerous')}
          />
        ) : null}
      </button>

      {confirming ? (
        <div className="mt-1 flex flex-col gap-1.5 rounded-[var(--radius-sm)] border border-warn bg-warn-soft px-2.5 py-2">
          <p className="flex items-start gap-1.5 text-[11px] text-warn">
            <ShieldAlert size={13} className="mt-px shrink-0" aria-hidden="true" />
            {granted ? t('team.matrix.confirmRevoke') : t('team.matrix.confirmGrant')}
          </p>
          <div className="flex justify-end gap-1.5">
            <Button variant="ghost" size="sm" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="sm" onClick={confirm}>
              <AlertCircle size={13} aria-hidden="true" />
              {t('team.matrix.confirmYes')}
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
