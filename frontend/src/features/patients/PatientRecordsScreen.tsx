// Patient clinical records (/patients/{id}/records) — the most PHI-sensitive
// screen. Header: identity (MASKED phone), consent badge. Body: the
// purpose-of-use GATE — clinical tabs are LOCKED until a purpose is declared.
// Once declared, the purpose is passed to every clinical read. Tabs:
// Prescriptions | Lab reports | Medical history | ABDM records.
//
// The declared purpose lives in component state so it RESETS when you navigate
// away — re-entry requires re-declaring (and re-logging) access.

import { useState } from 'react';
import * as Tabs from '@radix-ui/react-tabs';
import { Link, useParams } from '@tanstack/react-router';
import { ArrowLeft } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Avatar } from '@/components/ui/Avatar';
import { Card } from '@/components/ui/Card';
import { Skeleton } from '@/components/ui/Skeleton';
import { maskPhone, shortDate } from '@/lib/format';
import { PATIENTS } from '@/lib/data';
import { usePermissions } from '@/lib/permissions';
import { usePatientConsent } from './api';
import { ConsentBadge } from './components/ConsentBadge';
import { PurposeBanner, PurposeGate } from './components/PurposeGate';
import { PrescriptionsTab } from './components/PrescriptionsTab';
import { ReportsTab } from './components/ReportsTab';
import { HistoryTab } from './components/HistoryTab';
import { AbdmTab } from './components/AbdmTab';
import type { PurposeOfUse } from '@/lib/mock/contracts';

const tabTrigger =
  'shrink-0 whitespace-nowrap px-3 py-2 text-[13px] font-medium text-muted border-b-2 border-transparent transition-colors ' +
  'hover:text-ink data-[state=active]:border-primary data-[state=active]:text-ink ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary';

export function PatientRecordsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { patientId } = useParams({ from: '/authed/patients/$patientId/records' });
  const patient = PATIENTS.find((p) => p.id === patientId);
  const { data: consent, isLoading: consentLoading } = usePatientConsent(patientId);

  // Declared purpose — null until the gate is satisfied. Resets on navigation.
  const [purpose, setPurpose] = useState<PurposeOfUse | null>(null);

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
            <p className="mono text-[12px] text-muted">{patient ? maskPhone(patient.phone) : '—'}</p>
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

      {/* Gate: clinical tabs are locked until a purpose is declared. */}
      {purpose === null ? (
        <PurposeGate onDeclare={setPurpose} />
      ) : (
        <>
          <PurposeBanner purpose={purpose} onChange={() => setPurpose(null)} />
          <Tabs.Root defaultValue="prescriptions">
            <div className="border-b border-line">
              <Tabs.List className="flex gap-1 overflow-x-auto" aria-label={t('clinical.title')}>
                {can('docslot.prescription.read') ? (
                  <Tabs.Trigger value="prescriptions" className={tabTrigger}>
                    {t('clinical.tabPrescriptions')}
                  </Tabs.Trigger>
                ) : null}
                {can('docslot.report.read') ? (
                  <Tabs.Trigger value="reports" className={tabTrigger}>
                    {t('clinical.tabReports')}
                  </Tabs.Trigger>
                ) : null}
                {can('docslot.medical_history.read') ? (
                  <Tabs.Trigger value="history" className={tabTrigger}>
                    {t('clinical.tabHistory')}
                  </Tabs.Trigger>
                ) : null}
                {can('docslot.abdm.records.read') ? (
                  <Tabs.Trigger value="abdm" className={tabTrigger}>
                    {t('clinical.tabAbdm')}
                  </Tabs.Trigger>
                ) : null}
              </Tabs.List>
            </div>

            {can('docslot.prescription.read') ? (
              <Tabs.Content value="prescriptions" className="pt-5 focus-visible:outline-none">
                <PrescriptionsTab patientId={patientId} purpose={purpose} />
              </Tabs.Content>
            ) : null}
            {can('docslot.report.read') ? (
              <Tabs.Content value="reports" className="pt-5 focus-visible:outline-none">
                <ReportsTab patientId={patientId} purpose={purpose} />
              </Tabs.Content>
            ) : null}
            {can('docslot.medical_history.read') ? (
              <Tabs.Content value="history" className="pt-5 focus-visible:outline-none">
                <HistoryTab patientId={patientId} purpose={purpose} />
              </Tabs.Content>
            ) : null}
            {can('docslot.abdm.records.read') ? (
              <Tabs.Content value="abdm" className="pt-5 focus-visible:outline-none">
                <AbdmTab patientId={patientId} purpose={purpose} abdmConsent={consent?.abdmConsent} />
              </Tabs.Content>
            ) : null}
          </Tabs.Root>
        </>
      )}
    </section>
  );
}
