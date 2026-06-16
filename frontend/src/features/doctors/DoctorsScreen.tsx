// Doctors directory (/doctors) — "Practitioners · N". Department filter tabs with
// live counts, an "Add doctor" button (opens the addDoctor slide-over, gated on
// the in-memory permission set), and a responsive card grid. Loading / empty /
// error states all implemented. No role branches; nav is backend-driven elsewhere.

import { useState } from 'react';
import { UserPlus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { DEPARTMENTS } from '@/lib/data';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { useDoctorCards } from './api';
import { DoctorCardView } from './components/DoctorCardView';

const tabTrigger =
  'shrink-0 whitespace-nowrap rounded-full px-3 py-1.5 text-[13px] font-medium transition-colors ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function DoctorsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const { data, isLoading, isError, refetch } = useDoctorCards();
  const [dept, setDept] = useState<string>('all');

  const all = data ?? [];
  const visible = dept === 'all' ? all : all.filter((d) => d.deptId === dept);
  const countFor = (deptId: string) => (deptId === 'all' ? all.length : all.filter((d) => d.deptId === deptId).length);

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {data ? t('doctors.count', { count: all.length }) : t('doctors.title')}
        </h1>
        {can('docslot.doctor.create') ? (
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'addDoctor' })}>
            <UserPlus size={15} aria-hidden="true" />
            {t('doctors.addDoctor')}
          </Button>
        ) : null}
      </div>

      {/* Department filter tabs with counts. Counts come from the loaded data, so
          they reflect the in-memory list, not a per-tab fetch. */}
      <div className="flex flex-wrap gap-2" role="tablist" aria-label={t('doctors.title')}>
        <DeptTab active={dept === 'all'} count={countFor('all')} onClick={() => setDept('all')}>
          {t('doctors.deptAll')}
        </DeptTab>
        {DEPARTMENTS.map((d) => (
          <DeptTab key={d.id} active={dept === d.id} count={countFor(d.id)} onClick={() => setDept(d.id)}>
            {d.name}
          </DeptTab>
        ))}
      </div>

      {isError ? (
        <Card>
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        </Card>
      ) : isLoading || !data ? (
        <DoctorsGridSkeleton />
      ) : visible.length === 0 ? (
        <Card>
          <EmptyState
            title={all.length === 0 ? t('doctors.emptyTitle') : t('doctors.emptyFilteredTitle')}
            description={all.length === 0 ? t('doctors.emptyBody') : t('doctors.emptyFilteredBody')}
            actionLabel={all.length === 0 && can('docslot.doctor.create') ? t('doctors.addDoctor') : undefined}
            onAction={all.length === 0 && can('docslot.doctor.create') ? () => openPanel({ type: 'addDoctor' }) : undefined}
          />
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {visible.map((d) => (
            <DoctorCardView key={d.id} doctor={d} />
          ))}
        </div>
      )}
    </section>
  );
}

function DeptTab({
  active,
  count,
  onClick,
  children,
}: {
  active: boolean;
  count: number;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={`${tabTrigger} ${
        active ? 'bg-primary text-bg' : 'border border-line bg-surface text-muted hover:bg-surface-sunk hover:text-ink'
      }`}
    >
      {children}
      <span className={`mono ml-1.5 text-[11px] ${active ? 'text-bg/70' : 'text-muted-2'}`}>{count}</span>
    </button>
  );
}

function DoctorsGridSkeleton() {
  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3" role="status" aria-busy="true">
      {Array.from({ length: 6 }).map((_, i) => (
        <Card key={i} className="flex flex-col gap-4 p-4">
          <div className="flex items-start gap-3">
            <Skeleton className="h-12 w-12 rounded-full" />
            <div className="flex flex-1 flex-col gap-2">
              <Skeleton className="h-3 w-2/3" />
              <Skeleton className="h-3 w-1/2" />
              <Skeleton className="h-4 w-20 rounded-full" />
            </div>
          </div>
          <Skeleton className="h-12 w-full" />
          <Skeleton className="h-2 w-full" />
          <div className="grid grid-cols-2 gap-2">
            <Skeleton className="h-8 w-full" />
            <Skeleton className="h-8 w-full" />
          </div>
        </Card>
      ))}
    </div>
  );
}
