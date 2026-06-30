// OCR "Extract lab report" slide-over (Slice 11). Triggers an AI extraction for
// the patient and renders the structured analytes (test / value / unit / range /
// flag) with a requires-human-review banner + abnormal count.
//
// PHI discipline: the analyte values are PHI. They are returned by a MUTATION and
// live only in the transient mutation result — never a query key, never logged,
// never echoed into a toast/store/URL. The call is patient-bound, so the declared
// purpose-of-use is forwarded as X-Purpose-Of-Use (the server 422s without it);
// the extraction is PERSISTED server-side, so the POST carries an Idempotency-Key
// (handled in the hook). A consent 403 surfaces the contextual break-glass.
//
// All states (REACT_SKILL): idle, loading (skeleton), available:false ("extraction
// unavailable" — never fabricated), empty (no analytes), error, consent-denied.

import { useTranslation } from 'react-i18next';
import { AlertTriangle, ScanText } from 'lucide-react';
import { SlideOver } from '@/components/ui/SlideOver';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { useExtractLabReport } from '../ai';
import { ConsentBlocked, isConsentDenied } from './ConsentBlocked';
import type { OcrAnalyte, OcrExtraction, PurposeOfUse } from '@/lib/mock/contracts';

/** Analyte flag → token-only tone (zero hex). Defensive map for unknown tokens. */
function flagTone(flag: string): string {
  const f = flag.toLowerCase();
  if (f === 'critical') return 'text-danger font-semibold';
  if (f === 'high' || f === 'low' || f === 'abnormal') return 'text-warn';
  return 'text-muted';
}

export function OcrExtractPanel({
  patientId,
  purpose,
  bookingId,
  open,
  onClose,
}: {
  patientId: string;
  purpose: PurposeOfUse;
  bookingId?: string;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const ocr = useExtractLabReport(patientId, purpose, bookingId);
  const consentDenied = ocr.isError && isConsentDenied(ocr.error);

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('ocr.title')}
      description={t('ocr.subtitle')}
    >
      <div className="flex flex-col gap-4">
        <p className="text-[12px] text-muted">{t('ocr.intro')}</p>

        <div className="flex justify-end">
          <Button variant="primary" size="sm" type="button" onClick={() => ocr.mutate()} disabled={ocr.isPending}>
            <ScanText size={14} aria-hidden="true" />
            {ocr.isPending ? t('ocr.running') : ocr.data ? t('ocr.runAgain') : t('ocr.run')}
          </Button>
        </div>

        {consentDenied ? (
          <ConsentBlocked
            patientId={patientId}
            resourceType="lab_report"
            resourceId={null}
            onRetry={() => ocr.mutate()}
            inPanel
          />
        ) : ocr.isError ? (
          <p className="flex items-center gap-1.5 text-[12px] text-danger">
            <AlertTriangle size={13} aria-hidden="true" />
            {t('ocr.error')}
          </p>
        ) : ocr.isPending ? (
          <div className="flex flex-col gap-2" role="status" aria-busy="true">
            <Skeleton className="h-4 w-40" />
            <Skeleton className="h-32 w-full" />
          </div>
        ) : ocr.data ? (
          <OcrResultBody result={ocr.data} />
        ) : (
          <p className="text-[12px] text-muted">{t('ocr.idleHint')}</p>
        )}
      </div>
    </SlideOver>
  );
}

function OcrResultBody({ result }: { result: OcrExtraction }) {
  const { t } = useTranslation();

  // AI sibling unreachable → honest "unavailable", never a fabricated table.
  if (!result.available) {
    return (
      <p className="flex items-center gap-1.5 text-[12px] text-muted">
        <AlertTriangle size={13} aria-hidden="true" />
        {t('ocr.unavailable')}
      </p>
    );
  }

  return (
    <div className="flex select-none flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2 text-[11px] text-muted-2">
        {result.ocrEngine ? <span className="rounded bg-surface-sunk px-1.5 py-0.5">{result.ocrEngine}</span> : null}
        {typeof result.overallConfidence === 'number' ? (
          <span>{t('ocr.confidence', { pct: Math.round(result.overallConfidence * 100) })}</span>
        ) : null}
        {typeof result.abnormalCount === 'number' ? (
          <span className={result.abnormalCount > 0 ? 'text-warn' : ''}>
            {t('ocr.abnormalCount', { count: result.abnormalCount })}
          </span>
        ) : null}
      </div>

      {result.requiresHumanReview ? (
        <div className="flex items-center gap-2 rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2 text-[12px] font-medium text-warn">
          <AlertTriangle size={15} aria-hidden="true" />
          {t('ocr.requiresReview')}
        </div>
      ) : null}

      <section>
        <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('ocr.analytes')}</h3>
        {result.analytes.length === 0 ? (
          <p className="text-[12px] text-muted">{t('ocr.noAnalytes')}</p>
        ) : (
          <div className="overflow-hidden rounded-[var(--radius-sm)] border border-line">
            <table className="w-full text-left text-[12px]">
              <thead className="bg-surface-sunk text-[10px] uppercase tracking-wider text-muted-2">
                <tr>
                  <th className="px-3 py-1.5">{t('ocr.test')}</th>
                  <th className="px-3 py-1.5 text-right">{t('ocr.value')}</th>
                  <th className="px-3 py-1.5 text-right">{t('ocr.ref')}</th>
                  <th className="px-3 py-1.5 text-right">{t('ocr.flag')}</th>
                </tr>
              </thead>
              <tbody>
                {result.analytes.map((a, i) => (
                  <AnalyteRow key={`${a.test}-${i}`} analyte={a} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <p className="text-[11px] text-muted">{t('ocr.advisoryNote')}</p>
    </div>
  );
}

function AnalyteRow({ analyte }: { analyte: OcrAnalyte }) {
  const { t } = useTranslation();
  const tone = flagTone(analyte.flag);
  return (
    <tr className="border-t border-line">
      <td className="px-3 py-1.5 text-ink">{analyte.test}</td>
      <td className={`mono px-3 py-1.5 text-right ${tone}`}>
        {analyte.value}
        {analyte.unit ? ` ${analyte.unit}` : ''}
      </td>
      <td className="mono px-3 py-1.5 text-right text-muted">
        {analyte.refLow}–{analyte.refHigh}
      </td>
      <td className={`px-3 py-1.5 text-right capitalize ${tone}`}>
        {t(`ocr.flagLabel.${analyte.flag.toLowerCase()}`, { defaultValue: analyte.flag })}
      </td>
    </tr>
  );
}
