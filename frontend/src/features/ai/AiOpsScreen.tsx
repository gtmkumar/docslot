// AI Operations (/ai-ops) — an admin/ops surface for the two AI capabilities
// shipped in slices 11 & 14. NON-PHI summaries only: recent OCR extractions
// (header summaries) + RAG knowledge-base status (counts). The PHI actions
// (ask / extract for a specific patient) live on the patient clinical view, never
// here. Backend-driven nav is untouched — this screen never branches on role.
//
// Each section is gated on its own permission via usePermissions().can():
//   - extractions list → docslot.report.read
//   - RAG status       → docslot.medical_history.read
// A user with neither sees the not-authorized empty state.

import { useTranslation } from 'react-i18next';
import { ShieldX } from 'lucide-react';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { usePermissions } from '@/lib/permissions';
import { ExtractionsSection } from './components/ExtractionsSection';
import { RagStatusSection } from './components/RagStatusSection';

export function AiOpsScreen() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const canExtractions = can('docslot.report.read');
  const canRagStatus = can('docslot.medical_history.read');

  return (
    <section aria-labelledby="screen-heading" className="flex flex-col gap-6">
      <header>
        <h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">
          {t('ai.title')}
        </h1>
        <p className="mt-1 text-[13px] text-muted">{t('ai.subtitle')}</p>
      </header>

      {!canExtractions && !canRagStatus ? (
        <Card>
          <EmptyState
            icon={<ShieldX size={22} aria-hidden="true" />}
            title={t('ai.noAccessTitle')}
            description={t('ai.noAccessBody')}
          />
        </Card>
      ) : (
        <>
          {canExtractions ? <ExtractionsSection /> : null}
          {canRagStatus ? <RagStatusSection /> : null}
        </>
      )}
    </section>
  );
}
