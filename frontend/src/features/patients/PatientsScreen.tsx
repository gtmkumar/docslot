// Patients list. Each row links to that patient's clinical records. PHI: the
// list shows a MASKED phone only — clinical content never appears here. "Add
// patient" opens the existing slide-over.
//
// Data comes from usePatients() (mock derives from the seed; the live API hits
// GET /patients behind VITE_USE_REAL_API). All three list states are implemented:
// error → Card + EmptyState with retry; loading → content-shaped skeleton;
// empty → EmptyState (truly-empty offers the add action).

import { Link } from '@tanstack/react-router';
import { ChevronRight, UserPlus, Users } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { usePermissions } from '@/lib/permissions';
import { useUI } from '@/stores/ui';
import { usePatients } from './api';

export function PatientsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const openPanel = useUI((s) => s.openPanel);
  const { data, isLoading, isError, refetch } = usePatients();

  const canAdd = can('docslot.patient.update');

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <div className="flex items-center justify-between gap-3">
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {data ? t('patients.count', { count: data.length }) : t('nav.patients')}
        </h1>
        {canAdd ? (
          <Button variant="primary" size="sm" onClick={() => openPanel({ type: 'addPatient' })}>
            <UserPlus size={15} aria-hidden="true" />
            {t('addPatient.title')}
          </Button>
        ) : null}
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
        <PatientsListSkeleton />
      ) : data.length === 0 ? (
        <Card>
          <EmptyState
            icon={<Users size={28} aria-hidden="true" />}
            title={t('patients.emptyTitle')}
            description={t('patients.emptyBody')}
            actionLabel={canAdd ? t('addPatient.title') : undefined}
            onAction={canAdd ? () => openPanel({ type: 'addPatient' }) : undefined}
          />
        </Card>
      ) : (
        <Card>
          <ul className="flex flex-col">
            {data.map((p) => (
              <li key={p.id}>
                <Link
                  to="/patients/$patientId/records"
                  params={{ patientId: p.id }}
                  className="flex w-full items-center gap-3 border-b border-line px-4 py-3 text-left transition-colors last:border-0 hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                >
                  <Avatar name={p.name} size="md" />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-ink">{p.name}</p>
                    {/* PHI: masked phone only in the list. */}
                    <p className="mono text-[12px] text-muted">{p.maskedPhone}</p>
                  </div>
                  {p.age != null ? (
                    <span className="mono hidden w-16 shrink-0 text-right text-[12px] text-muted-2 sm:block">
                      {p.age}y
                    </span>
                  ) : null}
                  <ChevronRight size={16} className="shrink-0 text-muted-2" aria-hidden="true" />
                </Link>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </section>
  );
}

/** Content-shaped skeleton (rows), not a spinner. */
function PatientsListSkeleton() {
  return (
    <Card>
      <ul className="flex flex-col">
        {Array.from({ length: 8 }).map((_, i) => (
          <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
            <Skeleton className="h-9 w-9 rounded-full" />
            <div className="flex min-w-0 flex-1 flex-col gap-1.5">
              <Skeleton className="h-3.5 w-32" />
              <Skeleton className="h-3 w-24" />
            </div>
            <Skeleton className="hidden h-3 w-10 sm:block" />
          </li>
        ))}
      </ul>
    </Card>
  );
}
