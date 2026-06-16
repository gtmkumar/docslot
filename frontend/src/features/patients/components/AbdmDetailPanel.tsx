// ABDM record detail slide-over. Consent + purpose gated (the query throws on a
// consent failure → we surface a locked state, not data). The FHIR bundle
// CONTENTS are never rendered — only metadata (type, ABHA, resource count, PHR
// link). NOT URL-addressable.

import { useTranslation } from 'react-i18next';
import { SlideOver } from '@/components/ui/SlideOver';
import { Skeleton } from '@/components/ui/Skeleton';
import { EmptyState } from '@/components/ui/EmptyState';
import { shortDate } from '@/lib/format';
import { useAbdmRecord } from '../api';
import { ConsentRequiredError } from '@/lib/mock';
import type { PurposeOfUse } from '@/lib/mock/contracts';

export function AbdmDetailPanel({
  recordId,
  patientId,
  purpose,
  open,
  onClose,
}: {
  recordId: string;
  patientId: string;
  purpose: PurposeOfUse;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data, isLoading, isError, error } = useAbdmRecord(recordId, patientId, purpose);
  const consentBlocked = isError && error instanceof ConsentRequiredError;

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('clinical.abdm.detailTitle')}
      description={t('clinical.abdm.detailTitle')}
    >
      {consentBlocked ? (
        <EmptyState title={t('clinical.abdm.noConsentTitle')} description={t('clinical.abdm.noConsentBody')} />
      ) : isError ? (
        <EmptyState title={t('error.genericTitle')} description={t('error.genericBody')} />
      ) : isLoading || !data ? (
        <div className="flex flex-col gap-3" role="status" aria-busy="true">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-24 w-full" />
        </div>
      ) : (
        <dl className="flex flex-col gap-3 rounded-[var(--radius)] border border-line p-3">
          <Row label={t('clinical.abdm.recordType')} value={data.recordType} />
          <Row label={t('clinical.abdm.abha')} value={data.abhaNumber} mono />
          {/* Bundle CONTENTS not shown — only the resource count. */}
          <Row label={t('clinical.abdm.resources', { count: data.fhirResourceCount })} value={t('clinical.abdm.linkedPhr')} muted={!data.isLinkedToPhr} />
          <Row label={t('clinical.reports.colDate')} value={shortDate(data.createdAt)} mono />
        </dl>
      )}
    </SlideOver>
  );
}

function Row({ label, value, mono, muted }: { label: string; value: string; mono?: boolean; muted?: boolean }) {
  return (
    <div>
      <dt className="text-[11px] uppercase tracking-wider text-muted-2">{label}</dt>
      <dd className={`mt-0.5 text-[13px] ${muted ? 'text-muted-2' : 'text-ink'} ${mono ? 'mono' : ''}`}>{value}</dd>
    </div>
  );
}
