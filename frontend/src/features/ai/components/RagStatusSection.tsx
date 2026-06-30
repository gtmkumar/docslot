// AI Operations — RAG knowledge-base status (GET /ai/rag/status). NON-PHI:
// operational counts (embeddings / patients-indexed) + per-KB document counts, so
// this is a cacheable query. Gated on docslot.medical_history.read by the parent
// (the hook stays disabled otherwise).

import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, Database, Layers, Users } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { useRagStatus } from '../api';

export function RagStatusSection() {
  const { t } = useTranslation();
  const { data, isLoading, isError, refetch } = useRagStatus(true);

  return (
    <section aria-labelledby="ai-rag-heading" className="flex flex-col gap-3">
      <h2 id="ai-rag-heading" className="text-sm font-semibold text-ink">
        {t('ai.rag.heading')}
      </h2>

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
        <div className="grid gap-3 sm:grid-cols-2" role="status" aria-busy="true">
          <Skeleton className="h-20 w-full" />
          <Skeleton className="h-20 w-full" />
        </div>
      ) : !data.available ? (
        <Card>
          <EmptyState
            icon={<AlertTriangle size={22} aria-hidden="true" />}
            title={t('ai.rag.unavailable')}
            description={t('ai.unavailableBody')}
          />
        </Card>
      ) : (
        <div className="flex flex-col gap-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <StatCard icon={<Database size={16} aria-hidden="true" />} label={t('ai.rag.embeddings')} value={data.embeddings ?? 0} />
            <StatCard icon={<Users size={16} aria-hidden="true" />} label={t('ai.rag.patientsIndexed')} value={data.patientsIndexed ?? 0} />
          </div>

          <Card>
            <div className="border-b border-line px-4 py-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              {t('ai.rag.knowledgeBases')}
            </div>
            {data.knowledgeBases.length === 0 ? (
              <EmptyState title={t('ai.rag.noKbs')} />
            ) : (
              <ul className="flex flex-col">
                {data.knowledgeBases.map((kb) => (
                  <li key={kb.kbKey} className="flex items-center gap-3 border-b border-line px-4 py-3 last:border-0">
                    <span className="text-muted-2" aria-hidden="true">
                      <Layers size={15} />
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[13px] font-medium text-ink">{kb.name}</span>
                      <span className="mono block text-[11px] text-muted-2">{kb.kbKey}</span>
                    </span>
                    <span className="mono shrink-0 text-[12px] text-muted">
                      {t('ai.rag.docCount', { count: kb.documentCount })}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      )}
    </section>
  );
}

function StatCard({ icon, label, value }: { icon: ReactNode; label: string; value: number }) {
  return (
    <Card className="flex items-center gap-3 p-4">
      <span className="flex h-9 w-9 items-center justify-center rounded-full bg-surface-sunk text-muted" aria-hidden="true">
        {icon}
      </span>
      <div>
        <p className="text-xl font-semibold text-ink">{value.toLocaleString('en-IN')}</p>
        <p className="text-[12px] text-muted">{label}</p>
      </div>
    </Card>
  );
}
