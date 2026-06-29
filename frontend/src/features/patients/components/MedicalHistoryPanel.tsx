// Medical-history create / edit slide-over (Phase-3 slice 4). One panel serves
// both: no `entry` → create; `entry` → edit (and, via the Active toggle, retire).
//
// PHI DISCIPLINE: title + description are decrypted PHI. They live ONLY in this
// form's local state and the POST/PUT body — never the URL, never a log. The panel
// is transient (not URL-addressable) like the sibling clinical panels.
//
// React 19 idiom: the submit is an Action (`useActionState` + <form action={…}>),
// so the footer button reflects pending state and a stable Idempotency-Key is
// generated ONCE per submit invocation (retries de-dupe server-side).

import { useState } from 'react';
import { useActionState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useCreateMedicalHistory, useUpdateMedicalHistory } from '../api';
import type {
  MedicalHistory,
  MedicalHistoryRecordType,
  MedicalHistorySeverity,
  PurposeOfUse,
} from '@/lib/mock/contracts';

const RECORD_TYPES: MedicalHistoryRecordType[] = [
  'allergy',
  'chronic_condition',
  'surgery',
  'medication',
  'vaccination',
  'family_history',
  'lifestyle',
];
const SEVERITIES: MedicalHistorySeverity[] = ['mild', 'moderate', 'severe', 'critical'];

/** The READ shape's recordType is a free string; coerce it back to a form enum
 *  for the edit dropdown, falling back to the first option for an unknown token. */
function asRecordType(value: string): MedicalHistoryRecordType {
  return (RECORD_TYPES as string[]).includes(value)
    ? (value as MedicalHistoryRecordType)
    : RECORD_TYPES[0];
}

/** The READ shape's severity is a nullable free string; coerce it to the form enum
 *  ('' = not specified) so EDIT pre-fills it instead of wiping it on submit. */
function asSeverity(value: string | null | undefined): MedicalHistorySeverity | '' {
  return value && (SEVERITIES as string[]).includes(value) ? (value as MedicalHistorySeverity) : '';
}

export function MedicalHistoryPanel({
  patientId,
  purpose,
  entry,
  open,
  onClose,
}: {
  patientId: string;
  purpose: PurposeOfUse;
  entry?: MedicalHistory;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const isEdit = Boolean(entry);
  const create = useCreateMedicalHistory(patientId);
  const update = useUpdateMedicalHistory(patientId);

  // `purpose` is carried so the panel can be reached only inside a purpose-declared
  // session; it is referenced to keep the contract explicit (the write itself is
  // gated by permission + consent server-side, not by the purpose header).
  void purpose;

  const [recordType, setRecordType] = useState<MedicalHistoryRecordType>(
    entry ? asRecordType(entry.recordType) : 'allergy',
  );
  const [title, setTitle] = useState(entry?.title ?? '');
  const [description, setDescription] = useState(entry?.description ?? '');
  const [severity, setSeverity] = useState<MedicalHistorySeverity | ''>(asSeverity(entry?.severity));
  const [isCritical, setIsCritical] = useState(entry?.isCritical ?? false);
  // Only the edit flow exposes Active/Retired (isActive=false retires the record).
  const [isActive, setIsActive] = useState(entry?.isActive ?? true);
  const [touched, setTouched] = useState(false);

  const titleOk = title.trim().length > 0;

  const [, submit, isPending] = useActionState(async () => {
    setTouched(true);
    if (!titleOk) return null;
    const key = idempotencyKey();
    try {
      if (entry) {
        await update.mutateAsync({
          historyId: entry.historyId,
          recordType,
          title: title.trim(),
          description: description.trim() || null,
          severity: severity || null,
          // The form doesn't edit these, but the PUT treats missing as null (= data
          // loss), so carry the read values back so they're PRESERVED.
          icd10Code: entry.icd10Code,
          startedDate: entry.startedDate,
          endedDate: entry.endedDate,
          isActive,
          isCritical,
          idempotencyKey: key,
        });
        toast.success(isActive ? t('clinical.history.updated') : t('clinical.history.retired'));
      } else {
        await create.mutateAsync({
          recordType,
          title: title.trim(),
          description: description.trim() || null,
          severity: severity || null,
          isCritical,
          idempotencyKey: key,
        });
        toast.success(t('clinical.history.created'));
      }
      onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
    return null;
  }, null);

  const formId = 'medical-history-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={isEdit ? t('clinical.history.editTitle') : t('clinical.history.addTitle')}
      description={isEdit ? t('clinical.history.editTitle') : t('clinical.history.addTitle')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={isPending}>
            {isEdit ? t('clinical.history.save') : t('clinical.history.add')}
          </Button>
        </>
      }
    >
      <form id={formId} action={submit} className="flex flex-col gap-4">
        <FieldShell label={t('clinical.history.recordType')} htmlFor="mh-type">
          <Select
            id="mh-type"
            value={recordType}
            onChange={(e) => setRecordType(e.target.value as MedicalHistoryRecordType)}
          >
            {RECORD_TYPES.map((rt) => (
              <option key={rt} value={rt}>
                {t(`clinical.history.type.${rt}`)}
              </option>
            ))}
          </Select>
        </FieldShell>

        <FieldShell
          label={t('clinical.history.title')}
          htmlFor="mh-title"
          error={touched && !titleOk ? t('clinical.history.validationTitle') : undefined}
        >
          <TextInput
            id="mh-title"
            autoFocus
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            aria-invalid={touched && !titleOk}
          />
        </FieldShell>

        <FieldShell label={t('clinical.history.description')} htmlFor="mh-desc" optional={t('common.optional')}>
          <TextArea id="mh-desc" rows={3} value={description} onChange={(e) => setDescription(e.target.value)} />
        </FieldShell>

        <FieldShell label={t('clinical.history.severity')} htmlFor="mh-sev" optional={t('common.optional')}>
          <Select
            id="mh-sev"
            value={severity}
            onChange={(e) => setSeverity(e.target.value as MedicalHistorySeverity | '')}
          >
            <option value="">{t('clinical.history.severityNone')}</option>
            {SEVERITIES.map((s) => (
              <option key={s} value={s}>
                {t(`clinical.history.sev.${s}`)}
              </option>
            ))}
          </Select>
        </FieldShell>

        {/* Critical flag — a two-state segmented control (no Switch primitive). */}
        <div>
          <span className={labelClass}>{t('clinical.history.critical')}</span>
          <Toggle
            on={isCritical}
            onToggle={setIsCritical}
            onLabel={t('clinical.history.criticalYes')}
            offLabel={t('clinical.history.criticalNo')}
          />
        </div>

        {/* Active/Retired — edit-only. isActive=false retires the record. */}
        {isEdit ? (
          <div>
            <span className={labelClass}>{t('clinical.history.status')}</span>
            <Toggle
              on={isActive}
              onToggle={setIsActive}
              onLabel={t('clinical.history.active')}
              offLabel={t('clinical.history.inactive')}
            />
            {!isActive ? (
              <p className="mt-1 text-[11px] text-warn">{t('clinical.history.retireHint')}</p>
            ) : null}
          </div>
        ) : null}
      </form>
    </SlideOver>
  );
}

/** A two-option segmented control rendered as an accessible radiogroup. */
function Toggle({
  on,
  onToggle,
  onLabel,
  offLabel,
}: {
  on: boolean;
  onToggle: (next: boolean) => void;
  onLabel: string;
  offLabel: string;
}) {
  return (
    <div role="radiogroup" className="grid grid-cols-2 gap-2">
      <button
        type="button"
        role="radio"
        aria-checked={on}
        onClick={() => onToggle(true)}
        className={[
          'rounded-[var(--radius-sm)] border px-3 py-2 text-[13px] transition-colors',
          on ? 'border-primary bg-primary text-bg' : 'border-line bg-surface text-ink hover:bg-surface-sunk',
        ].join(' ')}
      >
        {onLabel}
      </button>
      <button
        type="button"
        role="radio"
        aria-checked={!on}
        onClick={() => onToggle(false)}
        className={[
          'rounded-[var(--radius-sm)] border px-3 py-2 text-[13px] transition-colors',
          !on ? 'border-primary bg-primary text-bg' : 'border-line bg-surface text-ink hover:bg-surface-sunk',
        ].join(' ')}
      >
        {offLabel}
      </button>
    </div>
  );
}
