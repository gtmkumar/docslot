// AI Operations — recent OCR extractions (GET /ai/extractions). NON-PHI: header
// summaries only (id / source / status / confidence / abnormal-count / review),
// never the analyte values, so this is a cacheable query. Gated on
// docslot.report.read by the parent (the hook stays disabled otherwise).

import { useTranslation } from 'react-i18next';
import { AlertTriangle } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { shortDate } from '@/lib/format';
import { useAiExtractions } from '../api';
import type { OcrExtractionSummary } from '@/lib/mock/contracts';

export function ExtractionsSection() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useAiExtractions(20, true);

  return (
    <section aria-labelledby="ai-extractions-heading" className="flex flex-col gap-3">
      <h2 id="ai-extractions-heading" className="text-sm font-semibold text-ink">
        {t('ai.extractions.heading')}
      </h2>
      <Card>
        {isError ? (
          <EmptyState
            title={t('error.genericTitle')}
            description={t('error.genericBody')}
            actionLabel={t('common.retry')}
            onAction={() => void refetch()}
          />
        ) : isLoading || !data ? (
          <ul className="flex flex-col" role="status" aria-busy="true">
            {Array.from({ length: 4 }).map((_, i) => (
              <li key={i} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                <Skeleton className="h-3 flex-1" />
                <Skeleton className="h-5 w-20" />
              </li>
            ))}
          </ul>
        ) : !data.available ? (
          <EmptyState
            icon={<AlertTriangle size={22} aria-hidden="true" />}
            title={t('ai.extractions.unavailable')}
            description={t('ai.unavailableBody')}
          />
        ) : data.extractions.length === 0 ? (
          <EmptyState title={t('ai.extractions.empty')} />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[640px] text-left text-[12px]">
              <thead className="border-b border-line text-[10px] uppercase tracking-wider text-muted-2">
                <tr>
                  <th className="px-4 py-2">{t('ai.extractions.id')}</th>
                  <th className="px-4 py-2">{t('ai.extractions.sourceType')}</th>
                  <th className="px-4 py-2">{t('ai.extractions.status')}</th>
                  <th className="px-4 py-2 text-right">{t('ai.extractions.confidence')}</th>
                  <th className="px-4 py-2 text-right">{t('ai.extractions.abnormal')}</th>
                  <th className="px-4 py-2 text-right">{t('ai.extractions.created')}</th>
                </tr>
              </thead>
              <tbody>
                {data.extractions.map((row) => (
                  <ExtractionRow key={row.extractionId} row={row} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </section>
  );
}

function ExtractionRow({ row }: { row: OcrExtractionSummary }) {
  const { t } = useTranslation();
  return (
    <tr className="border-b border-line last:border-0">
      <td className="mono px-4 py-2 text-ink">{row.extractionId}</td>
      <td className="px-4 py-2 capitalize text-muted">{row.sourceType.replace(/_/g, ' ')}</td>
      <td className="px-4 py-2">
        <span
          className={`rounded-full px-2 py-0.5 text-[11px] font-medium ${
            row.requiresHumanReview ? 'bg-warn-soft text-warn' : 'bg-primary-soft text-primary'
          }`}
        >
          {t(`ai.extractions.statusLabel.${row.status}`, { defaultValue: row.status.replace(/_/g, ' ') })}
        </span>
      </td>
      <td className="mono px-4 py-2 text-right text-muted">
        {typeof row.overallConfidence === 'number' ? `${Math.round(row.overallConfidence * 100)}%` : '—'}
      </td>
      <td className={`mono px-4 py-2 text-right ${row.abnormalCount > 0 ? 'text-warn' : 'text-muted'}`}>
        {row.abnormalCount}
      </td>
      <td className="mono px-4 py-2 text-right text-muted-2">{shortDate(row.createdAt)}</td>
    </tr>
  );
}
