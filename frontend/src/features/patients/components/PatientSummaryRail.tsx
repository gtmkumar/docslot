// Left rail of the patient records screen.
//  - Recent-patients switcher: a search field over the patients list query;
//    clicking a result switches the route param (which resets the purpose gate).
//  - Patient summary: name / age / sex, ALLERGIES (danger tokens) + chronic
//    conditions, "Patient since <month year> · N visits", masked phone.
//
// Identity (name/age/sex/masked phone) comes from the patients list query — the
// real, tenant-scoped row keyed by the route's patient id. patientSince +
// visitCount come from the timeline endpoint (shared query, deduped). Allergies +
// chronic come from the purpose-gated medical-history query when the caller can
// read it (a denial shows a "locked" hint, never a false "no allergies").

import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from '@tanstack/react-router';
import { HeartPulse, Phone, Search, ShieldAlert, ShieldCheck } from 'lucide-react';
import { Avatar } from '@/components/ui/Avatar';
import { Card } from '@/components/ui/Card';
import { TextInput } from '@/components/ui/Field';
import { Skeleton } from '@/components/ui/Skeleton';
import { usePermissions } from '@/lib/permissions';
import { useMedicalHistory, usePatients, usePatientTimeline } from '../api';
import type { PurposeOfUse } from '@/lib/mock/contracts';

const DEVA = /[ऀ-ॿ]/;

/** ISO date → "Nov 2025" in the active locale. */
function monthYear(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(lang.startsWith('hi') ? 'hi-IN' : 'en-IN', { month: 'short', year: 'numeric' });
}

export function PatientSummaryRail({ patientId, purpose }: { patientId: string; purpose: PurposeOfUse }) {
  const { t, i18n } = useTranslation();
  const { can } = usePermissions();

  const patientsQ = usePatients();
  const patient = patientsQ.data?.find((p) => p.id === patientId);
  const timelineQ = usePatientTimeline(patientId, purpose);

  const canReadHistory = can('docslot.medical_history.read');
  const historyQ = useMedicalHistory(canReadHistory ? patientId : undefined, purpose);
  const unavailable = !canReadHistory || historyQ.isError;

  const active = (historyQ.data ?? []).filter((h) => h.isActive);
  const allergies = active.filter((h) => h.recordType === 'allergy');
  const chronic = active.filter((h) => h.recordType === 'chronic_condition');

  const since = timelineQ.data?.patient.patientSince;
  const visitCount = timelineQ.data?.patient.visitCount ?? 0;
  const sinceLabel = since ? monthYear(since, i18n.language) : null;

  const ageSex = [patient?.age != null ? t('clinical.summary.years', { n: patient.age }) : null, patient?.gender]
    .filter(Boolean)
    .join(' · ');

  return (
    <aside className="flex flex-col gap-3 lg:w-72 lg:shrink-0" aria-label={t('clinical.summary.title')}>
      <RecentPatientsSwitcher currentId={patientId} />

      {/* Identity + visit stats. */}
      <Card className="flex flex-col gap-2 p-3">
        <div className="flex items-center gap-2.5">
          <Avatar name={patient?.name ?? '—'} size="md" />
          <div className="min-w-0">
            <p className={`truncate text-[14px] font-semibold text-ink ${patient && DEVA.test(patient.name) ? 'deva' : ''}`}>{patient?.name ?? '—'}</p>
            {ageSex ? <p className="text-[12px] capitalize text-muted">{ageSex}</p> : null}
          </div>
        </div>
        <div className="border-t border-line pt-2 text-[12px] text-muted">
          {sinceLabel
            ? t('clinical.summary.sinceVisits', { since: sinceLabel, count: visitCount })
            : t('clinical.summary.visitsOnly', { count: visitCount })}
        </div>
        <div className="flex items-center gap-1.5 text-[12px] text-muted">
          <Phone size={12} aria-hidden="true" />
          <span className="mono">{patient?.maskedPhone ?? '—'}</span>
        </div>
      </Card>

      {/* Allergies — danger tone, most safety-critical. */}
      <Card className="p-3">
        <h2 className="mb-2 flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          <ShieldAlert size={13} aria-hidden="true" />
          {t('clinical.summary.allergies')}
        </h2>
        {unavailable ? (
          <p className="text-[12px] text-muted">{t('clinical.summary.locked')}</p>
        ) : historyQ.isLoading ? (
          <Skeleton className="h-6 w-full" />
        ) : allergies.length === 0 ? (
          <p className="inline-flex items-center gap-1.5 text-[12px] text-muted">
            <ShieldCheck size={13} className="text-primary" aria-hidden="true" />
            {t('clinical.summary.noAllergies')}
          </p>
        ) : (
          <ul className="flex flex-wrap gap-1.5">
            {allergies.map((a) => (
              <li key={a.historyId}>
                <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-[12px] font-medium ${a.isCritical ? 'bg-danger text-bg' : 'bg-danger-soft text-danger'} ${DEVA.test(a.title) ? 'deva' : ''}`}>
                  {a.title}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>

      {/* Chronic conditions. */}
      <Card className="p-3">
        <h2 className="mb-2 flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          <HeartPulse size={13} aria-hidden="true" />
          {t('clinical.summary.chronic')}
        </h2>
        {unavailable ? (
          <p className="text-[12px] text-muted">{t('clinical.summary.locked')}</p>
        ) : historyQ.isLoading ? (
          <Skeleton className="h-6 w-full" />
        ) : chronic.length === 0 ? (
          <p className="text-[12px] text-muted">{t('clinical.summary.noChronic')}</p>
        ) : (
          <ul className="flex flex-wrap gap-1.5">
            {chronic.map((c) => (
              <li key={c.historyId}>
                <span className={`inline-flex items-center rounded-full bg-surface-sunk px-2 py-0.5 text-[12px] font-medium text-ink ${DEVA.test(c.title) ? 'deva' : ''}`}>
                  {c.title}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </aside>
  );
}

/** Search the patients list and jump to another patient's records. Navigating
 *  resets the destination screen's purpose gate, so access is re-declared. */
function RecentPatientsSwitcher({ currentId }: { currentId: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { data } = usePatients();
  const [q, setQ] = useState('');

  const results = useMemo(() => {
    const all = (data ?? []).filter((p) => p.id !== currentId);
    const needle = q.trim().toLowerCase();
    const matched = needle
      ? all.filter((p) => p.name.toLowerCase().includes(needle) || p.maskedPhone.includes(needle))
      : all;
    return matched.slice(0, 6);
  }, [data, q, currentId]);

  return (
    <Card className="flex flex-col gap-2 p-3">
      <label htmlFor="patient-switch" className="text-[11px] font-semibold uppercase tracking-wider text-muted-2">
        {t('clinical.summary.switchPatient')}
      </label>
      <div className="relative">
        <Search size={14} aria-hidden="true" className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-2" />
        <TextInput
          id="patient-switch"
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder={t('clinical.summary.searchPatients')}
          className="pl-8"
        />
      </div>
      {results.length === 0 ? (
        <p className="px-1 py-1 text-[12px] text-muted-2">{t('clinical.summary.noMatches')}</p>
      ) : (
        <ul className="flex flex-col">
          {results.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => void navigate({ to: '/patients/$patientId/records', params: { patientId: p.id } })}
                className="flex w-full items-center gap-2 rounded-[var(--radius-sm)] px-1.5 py-1.5 text-left transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
              >
                <Avatar name={p.name} size="sm" />
                <span className="min-w-0 flex-1">
                  <span className={`block truncate text-[13px] text-ink ${DEVA.test(p.name) ? 'deva' : ''}`}>{p.name}</span>
                  <span className="mono block truncate text-[11px] text-muted-2">{p.maskedPhone}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
