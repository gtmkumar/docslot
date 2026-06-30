// RAG "Ask about this patient's history" slide-over (Slice 11). A question input +
// an advisory answer panel with citations into the patient's medical history.
//
// PHI discipline: the typed question is PHI. It lives in local component state only
// while being typed, is sent as the mutation VARIABLE (request body), and is NEVER
// placed in a query key, logged, persisted, or echoed back (the result never
// carries the question). The answer is PHI too — it lives only in the transient
// mutation result (a mutation, not a cached query). The call is patient-bound, so
// the declared purpose-of-use is forwarded as X-Purpose-Of-Use. A consent 403
// surfaces the contextual break-glass affordance.
//
// All states (REACT_SKILL): idle, loading (skeleton), available:false ("answer
// unavailable" — never fabricated), error, and consent-denied.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FileText, Send, Sparkles } from 'lucide-react';
import { SlideOver } from '@/components/ui/SlideOver';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { TextArea, labelClass } from '@/components/ui/Field';
import { usePatientRag } from '../ai';
import { ConsentBlocked, isConsentDenied } from './ConsentBlocked';
import type { PurposeOfUse, RagAnswer, RagCitation } from '@/lib/mock/contracts';

/** True when a string carries Devanagari, so the `deva` font class is applied. */
function isDeva(text: string): boolean {
  return /[ऀ-ॿ]/.test(text);
}

export function RagAskPanel({
  patientId,
  purpose,
  open,
  onClose,
}: {
  patientId: string;
  purpose: PurposeOfUse;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const rag = usePatientRag(patientId, purpose);
  // The question (PHI) is held locally only while typing — not lifted to a store.
  const [question, setQuestion] = useState('');
  const canAsk = question.trim().length > 2 && !rag.isPending;
  const consentDenied = rag.isError && isConsentDenied(rag.error);

  const onAsk = () => {
    if (!canAsk) return;
    rag.mutate(question.trim());
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('rag.title')}
      description={t('rag.subtitle')}
    >
      <div className="flex flex-col gap-4">
        <div>
          <label htmlFor="rag-question" className={labelClass}>
            {t('rag.questionLabel')}
          </label>
          <TextArea
            id="rag-question"
            rows={3}
            value={question}
            onChange={(e) => setQuestion(e.target.value)}
            placeholder={t('rag.placeholder')}
            className={question && isDeva(question) ? 'deva' : ''}
          />
          <p className="mt-1.5 text-[11px] text-muted-2">{t('rag.phiNote')}</p>
        </div>

        <div className="flex justify-end">
          <Button variant="primary" size="sm" type="button" onClick={onAsk} disabled={!canAsk}>
            <Send size={14} aria-hidden="true" />
            {rag.isPending ? t('rag.asking') : t('rag.ask')}
          </Button>
        </div>

        {consentDenied ? (
          <ConsentBlocked
            patientId={patientId}
            resourceType="medical_history"
            resourceId={null}
            onRetry={onAsk}
            inPanel
          />
        ) : rag.isError ? (
          <p className="flex items-center gap-1.5 text-[12px] text-danger">
            <Sparkles size={13} aria-hidden="true" />
            {t('rag.error')}
          </p>
        ) : rag.isPending ? (
          <div className="flex flex-col gap-2" role="status" aria-busy="true">
            <Skeleton className="h-16 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : rag.data ? (
          <RagAnswerBody answer={rag.data} />
        ) : (
          <p className="text-[12px] text-muted">{t('rag.idleHint')}</p>
        )}
      </div>
    </SlideOver>
  );
}

function RagAnswerBody({ answer }: { answer: RagAnswer }) {
  const { t } = useTranslation();

  // AI sibling unreachable → honest "unavailable", never a fabricated answer.
  if (!answer.available || !answer.answer) {
    return (
      <p className="flex items-center gap-1.5 text-[12px] text-muted">
        <Sparkles size={13} aria-hidden="true" />
        {t('rag.unavailable')}
      </p>
    );
  }

  return (
    <div className="flex select-none flex-col gap-4">
      <section className="rounded-[var(--radius-sm)] border border-line bg-surface-sunk p-3">
        <div className="mb-1 flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
          <Sparkles size={13} aria-hidden="true" />
          {t('rag.answer')}
          {answer.mode ? (
            <span className="ml-auto rounded-full bg-info-soft px-2 py-0.5 text-[10px] font-medium normal-case tracking-normal text-info">
              {t(`rag.mode.${answer.mode}`, { defaultValue: answer.mode })}
            </span>
          ) : null}
        </div>
        <p className={`text-[13px] leading-relaxed text-ink ${isDeva(answer.answer) ? 'deva' : ''}`}>{answer.answer}</p>
      </section>

      <section>
        <h3 className="mb-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{t('rag.citations')}</h3>
        {answer.citations.length === 0 ? (
          <p className="text-[12px] text-muted">{t('rag.noCitations')}</p>
        ) : (
          <ul className="flex flex-col gap-1.5">
            {answer.citations.map((c) => (
              <CitationRow key={c.historyId} citation={c} />
            ))}
          </ul>
        )}
      </section>

      <p className="text-[11px] text-muted">{t('rag.advisoryNote')}</p>
    </div>
  );
}

function CitationRow({ citation }: { citation: RagCitation }) {
  const title = citation.title ?? '—';
  return (
    <li className="flex items-start gap-2 rounded-[var(--radius-sm)] border border-line px-2.5 py-1.5">
      <span className="mt-0.5 text-muted-2" aria-hidden="true">
        <FileText size={13} />
      </span>
      <span className="min-w-0 flex-1">
        <span className={`block truncate text-[12px] font-medium text-ink ${isDeva(title) ? 'deva' : ''}`}>{title}</span>
        <span className="flex items-center gap-2 text-[11px] text-muted-2">
          {citation.recordType ? <span className="capitalize">{citation.recordType.replace(/_/g, ' ')}</span> : null}
          {citation.severity ? <span className="capitalize">{citation.severity}</span> : null}
        </span>
      </span>
      <span className="mono shrink-0 text-[11px] text-muted-2">{Math.round(citation.score * 100)}%</span>
    </li>
  );
}
