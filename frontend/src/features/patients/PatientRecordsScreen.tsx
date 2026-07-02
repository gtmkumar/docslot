// Patient clinical records (/patients/{id}/records) — the most PHI-sensitive
// screen. Header: identity (MASKED phone), consent badge. Body: the
// purpose-of-use GATE — the record is LOCKED until a purpose is declared. Once
// declared, ONE declaration covers the whole view: a left summary rail (recent-
// patient switcher, identity, allergies, chronic, visits) + a center unified
// timeline (GET /patients/{id}/timeline) with backend-driven category chips that
// replaces the old per-type tabs.
//
// The declared purpose lives in component state so it RESETS when you navigate
// away — re-entry requires re-declaring (and re-logging) access.

import { useState } from 'react';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { useQuery } from '@tanstack/react-query';
import { ArrowLeft } from 'lucide-react';
import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { Card } from '@/components/ui/Card';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { listBookings } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import { usePatientConsent, usePatients } from './api';
import { ConsentBadge } from './components/ConsentBadge';
import { PurposeBanner, PurposeGate } from '@/components/ui/PurposeGate';
import { PatientSummaryRail } from './components/PatientSummaryRail';
import { ClinicalTimeline } from './components/ClinicalTimeline';
import type { PurposeOfUse } from '@/lib/mock/contracts';

export function PatientRecordsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const navigate = useNavigate();
  const { patientId } = useParams({ from: '/authed/patients/$patientId/records' });
  // Identity from the tenant-scoped patients list (masked phone only — no clinical
  // content). Keyed by the route id, so it resolves for real UUIDs too.
  const { data: patients } = usePatients();
  const patient = patients?.find((p) => p.id === patientId);
  const { data: consent, isLoading: consentLoading } = usePatientConsent(patientId);

  // Declared purpose — null until the gate is satisfied. Resets on navigation.
  const [purpose, setPurpose] = useState<PurposeOfUse | null>(null);

  // "New prescription" routes to the consultation composer for the patient's ACTIVE
  // booking (a prescription must bind a real booking — no synthetic ids). Resolved
  // from the bookings list by masked phone; only fetched when the operator can
  // prescribe. If there's no active booking, we explain that one is needed.
  const canCreateRx = can('docslot.prescription.create');
  const bookingsQ = useQuery({ queryKey: ['bookings', 'list'] as const, queryFn: listBookings, enabled: canCreateRx });
  const activeBooking = patient
    ? bookingsQ.data?.find(
        (b) => b.maskedPhone === patient.maskedPhone && (b.status === 'pending' || b.status === 'confirmed' || b.status === 'checked_in'),
      )
    : undefined;
  const onNewPrescription = () => {
    if (activeBooking) navigate({ to: '/consult/$bookingId', params: { bookingId: activeBooking.id } });
    else toast.info(t('clinical.rx.needBooking'));
  };

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-5">
      <Link
        to="/patients"
        className="inline-flex w-fit items-center gap-1 text-[13px] font-medium text-primary hover:underline"
      >
        <ArrowLeft size={14} aria-hidden="true" />
        {t('clinical.back')}
      </Link>

      {/* Patient header — identity is a MASKED phone; no clinical content here. */}
      <Card className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3">
          <Avatar name={patient?.name ?? '—'} size="lg" />
          <div>
            <h1 id="screen-heading" tabIndex={-1} className="text-lg font-semibold text-ink outline-none">
              {patient?.name ?? '—'}
            </h1>
            <p className="mono text-[12px] text-muted">{patient?.maskedPhone ?? '—'}</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          {consentLoading || !consent ? (
            <Skeleton className="h-7 w-28" />
          ) : (
            <div className="text-right">
              <ConsentBadge status={consent.clinicalConsent} />
              {consent.consentExpiresAt ? (
                <p className="mt-1 text-[11px] text-muted-2">{t('clinical.consent.expires', { date: shortDate(consent.consentExpiresAt) })}</p>
              ) : null}
            </div>
          )}
        </div>
      </Card>

      {/* Gate: the record is locked until a purpose is declared. One declaration
          covers the whole timeline + summary rail. */}
      {purpose === null ? (
        <PurposeGate onDeclare={setPurpose} />
      ) : (
        <>
          <PurposeBanner purpose={purpose} onChange={() => setPurpose(null)} />
          <div className="flex flex-col gap-5 lg:flex-row lg:items-start">
            <PatientSummaryRail patientId={patientId} purpose={purpose} />
            <div className="min-w-0 flex-1">
              <ClinicalTimeline patientId={patientId} purpose={purpose} abdmConsent={consent?.abdmConsent} onNewPrescription={onNewPrescription} />
            </div>
          </div>
        </>
      )}
    </section>
  );
}
