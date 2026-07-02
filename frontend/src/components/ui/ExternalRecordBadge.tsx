// Badge for a medical-history row that came from OUTSIDE DocSlot (a paper
// prescription photographed at the front desk, or patient-reported history).
//
//  - source 'clinic'      → nothing (an in-app row is not "external").
//  - external + unverified → warn-soft "Unverified · Paper Rx" / "· Patient reported".
//  - external + verified   → neutral "Paper Rx · Verified" (+ the external doctor).
//
// Lives in components/ui because it's shared across features (the patient History
// tab and the consult SafetyStrip) — a cross-feature import would otherwise be
// illegal. It carries no PHI beyond the (non-PHI) external doctor name.

import { useTranslation } from 'react-i18next';
import { FileCheck2, FileWarning } from 'lucide-react';

const DEVA = /[ऀ-ॿ]/;

export function ExternalRecordBadge({
  source,
  verifiedAt,
  externalDoctorName,
}: {
  source: string;
  verifiedAt: string | null;
  externalDoctorName?: string | null;
}) {
  const { t } = useTranslation();
  if (source === 'clinic') return null;

  const isPaper = source === 'paper_prescription';
  const verified = verifiedAt !== null;

  const label = verified
    ? isPaper
      ? t('clinical.history.external.verifiedPaperRx')
      : t('clinical.history.external.verifiedPatientReported')
    : isPaper
      ? t('clinical.history.external.unverifiedPaperRx')
      : t('clinical.history.external.unverifiedPatientReported');

  const Icon = verified ? FileCheck2 : FileWarning;

  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full px-1.5 py-0.5 text-[10px] font-semibold ${
        verified ? 'bg-surface-sunk text-muted' : 'bg-warn-soft text-warn'
      }`}
      title={verified ? undefined : t('clinical.history.external.unverifiedTooltip')}
    >
      <Icon size={10} aria-hidden="true" />
      {label}
      {verified && externalDoctorName ? (
        <span className={`font-normal ${DEVA.test(externalDoctorName) ? 'deva' : ''}`}>
          · {externalDoctorName}
        </span>
      ) : null}
    </span>
  );
}
