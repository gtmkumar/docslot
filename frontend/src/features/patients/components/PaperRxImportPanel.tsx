// Paper-prescription intake slide-over. Front desk photographs + transcribes a
// handwritten prescription (or patient-reported history) into a batch of
// UNVERIFIED external history records. ONE import POST with a single stable
// Idempotency-Key (React 19 Action). Gated upstream by the History tab launcher
// (docslot.medical_history.intake OR .create).
//
// PHI DISCIPLINE: transcribed titles/descriptions AND the scanned image bytes live
// ONLY in this form's local state and the POST body — never the URL, never a log.
// The panel is transient (not URL-addressable) like the sibling clinical panels.

import { useState, useActionState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { ImagePlus, Plus, Sparkles, X } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { usePermissions } from '@/lib/permissions';
import { useExtractPrescription, useImportMedicalHistory } from '../api';
import type {
  ImportMedicalHistoryRecord,
  ImportMedicalHistorySource,
  MedicalHistoryRecordType,
  MedicalHistorySeverity,
  PurposeOfUse,
} from '@/lib/mock/contracts';

const RECORD_TYPES: MedicalHistoryRecordType[] = [
  'medication',
  'allergy',
  'chronic_condition',
  'surgery',
  'vaccination',
  'family_history',
  'lifestyle',
];
const SEVERITIES: MedicalHistorySeverity[] = ['mild', 'moderate', 'severe', 'critical'];
/** ~4MB cap on the scanned image (raw file bytes) with a friendly error. */
const MAX_ATTACHMENT_BYTES = 4 * 1024 * 1024;
/** Below this the AI line is flagged "check this line" (warn outline). */
const LOW_CONFIDENCE = 0.6;

/** Coerce an OCR recordType token to the form enum (unknown → medication). */
function asRecordType(value: string): MedicalHistoryRecordType {
  return (RECORD_TYPES as string[]).includes(value) ? (value as MedicalHistoryRecordType) : 'medication';
}

interface DraftRecord {
  recordType: MedicalHistoryRecordType;
  title: string;
  description: string;
  severity: MedicalHistorySeverity | '';
  isCritical: boolean;
  /** Marks a line the OCR suggested (visual provenance) + its confidence (0..1). */
  aiSuggested?: boolean;
  confidence?: number | null;
}

const emptyRecord = (): DraftRecord => ({
  recordType: 'medication',
  title: '',
  description: '',
  severity: '',
  isCritical: false,
});

interface Attachment {
  fileName: string;
  contentType: string;
  /** Pure base64 (no data: prefix) for the POST body. */
  contentBase64: string;
  /** Full data URL for the inline preview thumbnail. */
  previewUrl: string;
}

export function PaperRxImportPanel({
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
  const { can } = usePermissions();
  const importBatch = useImportMedicalHistory(patientId);
  // OCR assist — threads the declared purpose to the extract call's X-Purpose-Of-Use.
  const extract = useExtractPrescription(patientId, purpose);
  // Same gate as the panel launcher (no new permission checks).
  const canExtract = can('docslot.medical_history.intake') || can('docslot.medical_history.create');

  const [source, setSource] = useState<ImportMedicalHistorySource>('paper_prescription');
  const [externalDoctorName, setExternalDoctorName] = useState('');
  const [recordedDate, setRecordedDate] = useState('');
  const [attachment, setAttachment] = useState<Attachment | null>(null);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [records, setRecords] = useState<DraftRecord[]>([emptyRecord()]);
  const [touched, setTouched] = useState(false);

  const filled = records.filter((r) => r.title.trim().length > 0);
  const recordsOk = filled.length > 0;

  const setRecord = (i: number, patch: Partial<DraftRecord>) =>
    setRecords((cur) => cur.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));

  const onFile = (file: File | undefined) => {
    setAttachmentError(null);
    if (!file) return;
    if (file.size > MAX_ATTACHMENT_BYTES) {
      setAttachmentError(t('clinical.history.import.attachmentTooLarge'));
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = typeof reader.result === 'string' ? reader.result : '';
      const base64 = dataUrl.includes(',') ? dataUrl.slice(dataUrl.indexOf(',') + 1) : '';
      setAttachment({ fileName: file.name, contentType: file.type || 'image/jpeg', contentBase64: base64, previewUrl: dataUrl });
    };
    reader.readAsDataURL(file);
  };

  // "Extract with AI": OCR the attached photo → APPEND suggested lines (marked
  // AI-suggested) + prefill EMPTY doctor/date fields. Human-in-the-loop: nothing
  // auto-saves; the import submit is untouched. Never blocks manual entry — an
  // unavailable service or error just toasts.
  const onExtract = async () => {
    if (!attachment) return;
    try {
      const res = await extract.mutateAsync({
        fileName: attachment.fileName,
        contentType: attachment.contentType,
        contentBase64: attachment.contentBase64,
      });
      if (!res.available) {
        toast.info(t('clinical.history.import.extractUnavailable'));
        return;
      }
      const aiLines: DraftRecord[] = res.records.map((r) => ({
        recordType: asRecordType(r.recordType),
        title: r.title,
        description: r.description ?? '',
        severity: '',
        isCritical: false,
        aiSuggested: true,
        confidence: r.confidence,
      }));
      if (aiLines.length === 0) {
        toast.info(t('clinical.history.import.extractUnavailable'));
        return;
      }
      // Keep any user-entered lines; drop a single pristine empty default; append AI.
      setRecords((cur) => {
        const meaningful = cur.filter((r) => r.title.trim().length > 0 || r.description.trim().length > 0);
        return [...meaningful, ...aiLines];
      });
      if (!externalDoctorName.trim() && res.externalDoctorName) setExternalDoctorName(res.externalDoctorName);
      if (!recordedDate && res.recordedDate) setRecordedDate(res.recordedDate);
      toast.success(t('clinical.history.import.extractedToast', { count: aiLines.length }));
    } catch (e) {
      toast.error(toUserError(e));
    }
  };

  const [, submit, isPending] = useActionState(async () => {
    setTouched(true);
    if (!recordsOk) return null;
    // ONE stable Idempotency-Key per submit invocation — a retry de-dupes server-side.
    const key = idempotencyKey();
    const payload: ImportMedicalHistoryRecord[] = filled.map((r) => ({
      recordType: r.recordType,
      title: r.title.trim(),
      description: r.description.trim() || null,
      severity: r.recordType === 'allergy' ? r.severity || null : null,
      isCritical: r.isCritical,
      startedDate: null,
    }));
    try {
      await importBatch.mutateAsync({
        source,
        externalDoctorName: externalDoctorName.trim() || null,
        recordedDate: recordedDate || null,
        attachment: attachment
          ? { fileName: attachment.fileName, contentType: attachment.contentType, contentBase64: attachment.contentBase64 }
          : null,
        records: payload,
        idempotencyKey: key,
      });
      toast.success(t('clinical.history.import.imported', { count: payload.length }));
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
    return null;
  }, null);

  const formId = 'paper-rx-import-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('clinical.history.import.title')}
      description={t('clinical.history.import.subtitle')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={isPending}>
            {t('clinical.history.import.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} action={submit} className="flex flex-col gap-4">
        <p className="rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2 text-[12px] text-warn">
          {t('clinical.history.import.unverifiedNotice')}
        </p>

        {/* Source: paper prescription vs patient reported. */}
        <div>
          <span className={labelClass}>{t('clinical.history.import.source')}</span>
          <div role="radiogroup" className="grid grid-cols-2 gap-2">
            <SourceOption on={source === 'paper_prescription'} onSelect={() => setSource('paper_prescription')} label={t('clinical.history.import.sourcePaper')} />
            <SourceOption on={source === 'patient_reported'} onSelect={() => setSource('patient_reported')} label={t('clinical.history.import.sourceReported')} />
          </div>
        </div>

        <FieldShell label={t('clinical.history.import.externalDoctor')} htmlFor="prx-doctor" optional={t('common.optional')}>
          <TextInput id="prx-doctor" value={externalDoctorName} onChange={(e) => setExternalDoctorName(e.target.value)} placeholder={t('clinical.history.import.externalDoctorPlaceholder')} />
        </FieldShell>

        <FieldShell label={t('clinical.history.import.recordedDate')} htmlFor="prx-date" optional={t('common.optional')}>
          <TextInput id="prx-date" type="date" value={recordedDate} onChange={(e) => setRecordedDate(e.target.value)} />
        </FieldShell>

        {/* Scanned image: file input → base64, with a preview thumbnail. */}
        <div>
          <span className={labelClass}>{t('clinical.history.import.attachment')}</span>
          {attachment ? (
            <div className="flex flex-col gap-2">
              <div className="flex items-center gap-3 rounded-[var(--radius-sm)] border border-line p-2.5">
                <img src={attachment.previewUrl} alt={t('clinical.history.import.attachmentPreviewAlt')} className="h-14 w-14 rounded-[var(--radius-sm)] border border-line object-cover" />
                <span className="min-w-0 flex-1 truncate text-[12px] text-ink">{attachment.fileName}</span>
                <button type="button" aria-label={t('clinical.history.import.attachmentRemove')} onClick={() => { setAttachment(null); setAttachmentError(null); }} className="rounded p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
                  <X size={15} aria-hidden="true" />
                </button>
              </div>
              {/* Extract with AI — only with a photo attached; advisory, review-first. */}
              {canExtract ? (
                <Button variant="subtle" size="sm" type="button" onClick={() => void onExtract()} disabled={extract.isPending}>
                  <Sparkles size={14} aria-hidden="true" />
                  {extract.isPending ? t('clinical.history.import.extracting') : t('clinical.history.import.extractAi')}
                </Button>
              ) : null}
            </div>
          ) : (
            <label className="flex cursor-pointer items-center gap-2 rounded-[var(--radius-sm)] border border-dashed border-line px-3 py-3 text-[13px] text-muted transition-colors hover:border-primary hover:text-ink focus-within:ring-2 focus-within:ring-primary">
              <ImagePlus size={16} aria-hidden="true" />
              {t('clinical.history.import.attachmentAdd')}
              <input type="file" accept="image/*" className="sr-only" onChange={(e) => onFile(e.target.files?.[0])} />
            </label>
          )}
          {attachmentError ? <p role="alert" className="mt-1 text-[12px] text-danger">{attachmentError}</p> : null}
        </div>

        {/* Transcribed records. */}
        <section>
          <div className="mb-2 flex items-center justify-between">
            <span className={labelClass}>{t('clinical.history.import.records')}</span>
            <Button variant="ghost" size="sm" type="button" onClick={() => setRecords((r) => [...r, emptyRecord()])}>
              <Plus size={13} aria-hidden="true" />
              {t('clinical.history.import.addRecord')}
            </Button>
          </div>
          <ul className="flex flex-col gap-3">
            {records.map((r, i) => {
              const lowConf = Boolean(r.aiSuggested && r.confidence != null && r.confidence < LOW_CONFIDENCE);
              return (
              <li key={i} className={`rounded-[var(--radius-sm)] border p-3 ${lowConf ? 'border-warn' : 'border-line'}`}>
                <div className="mb-2 flex items-center gap-2">
                  <Select value={r.recordType} onChange={(e) => setRecord(i, { recordType: e.target.value as MedicalHistoryRecordType })} className="flex-1">
                    {RECORD_TYPES.map((rt) => (
                      <option key={rt} value={rt}>
                        {t(`clinical.history.type.${rt}`)}
                      </option>
                    ))}
                  </Select>
                  {r.aiSuggested ? (
                    <span
                      title={t('clinical.history.import.aiTooltip', { pct: r.confidence != null ? Math.round(r.confidence * 100) : '—' })}
                      className="inline-flex shrink-0 items-center gap-1 rounded-full bg-accent-soft px-1.5 py-0.5 text-[10px] font-semibold text-accent"
                    >
                      <Sparkles size={10} aria-hidden="true" />
                      {t('clinical.history.import.aiChip')}
                    </span>
                  ) : null}
                  {records.length > 1 ? (
                    <button type="button" aria-label={t('clinical.history.import.removeRecord')} onClick={() => setRecords((cur) => cur.filter((_, idx) => idx !== i))} className="rounded p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
                      <X size={14} aria-hidden="true" />
                    </button>
                  ) : null}
                </div>
                {lowConf ? (
                  <p className="mb-2 text-[11px] font-medium text-warn">{t('clinical.history.import.aiLowConfidence')}</p>
                ) : null}
                <div className="flex flex-col gap-2">
                  <TextInput
                    value={r.title}
                    placeholder={t('clinical.history.import.recordTitlePlaceholder')}
                    onChange={(e) => setRecord(i, { title: e.target.value })}
                    aria-invalid={touched && !recordsOk && r.title.trim().length === 0}
                    aria-label={t('clinical.history.title')}
                  />
                  <TextArea
                    rows={2}
                    value={r.description}
                    placeholder={t('clinical.history.import.recordDescPlaceholder')}
                    onChange={(e) => setRecord(i, { description: e.target.value })}
                    aria-label={t('clinical.history.description')}
                  />
                  {r.recordType === 'allergy' ? (
                    <Select value={r.severity} onChange={(e) => setRecord(i, { severity: e.target.value as MedicalHistorySeverity | '' })} aria-label={t('clinical.history.severity')}>
                      <option value="">{t('clinical.history.severityNone')}</option>
                      {SEVERITIES.map((s) => (
                        <option key={s} value={s}>
                          {t(`clinical.history.sev.${s}`)}
                        </option>
                      ))}
                    </Select>
                  ) : null}
                  <label className="flex items-center gap-2.5 text-[13px] text-ink">
                    <input type="checkbox" checked={r.isCritical} onChange={(e) => setRecord(i, { isCritical: e.target.checked })} className="h-4 w-4 accent-[var(--danger)]" />
                    {t('clinical.history.criticalYes')}
                  </label>
                </div>
              </li>
              );
            })}
          </ul>
          {touched && !recordsOk ? <p role="alert" className="mt-1 text-[12px] text-danger">{t('clinical.history.import.validationRecords')}</p> : null}
        </section>
      </form>
    </SlideOver>
  );
}

/** A single source option rendered as an accessible radio. */
function SourceOption({ on, onSelect, label }: { on: boolean; onSelect: () => void; label: string }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={on}
      onClick={onSelect}
      className={[
        'rounded-[var(--radius-sm)] border px-3 py-2 text-[13px] transition-colors',
        on ? 'border-primary bg-primary text-bg' : 'border-line bg-surface text-ink hover:bg-surface-sunk',
      ].join(' ')}
    >
      {label}
    </button>
  );
}
