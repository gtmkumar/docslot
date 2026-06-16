// DPDP §11 data-export slide-over. Enter subject phone + purpose → generate a
// FHIR-R4 bundle. The result (record count + checksum + a download handle) is
// shown inline; the bundle CONTENTS are never rendered (they contain the
// subject's data). Gated upstream by platform.export_requests.process. POST
// carries a stable Idempotency-Key.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Download, FileCheck2 } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useExportSubject } from '../api';
import type { DataExportResult } from '@/lib/mock/contracts';

const schema = z.object({
  subjectPhone: z.string().trim().regex(/^\+?[0-9\s-]{8,16}$/, 'phone'),
  purpose: z.string().trim().min(1, 'reason'),
});
type ExportForm = z.infer<typeof schema>;

export function DataExportPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const exportSubject = useExportSubject();
  const [result, setResult] = useState<DataExportResult | null>(null);

  const { register, handleSubmit, formState } = useForm<ExportForm>({
    defaultValues: { subjectPhone: '', purpose: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    const r = await exportSubject.mutateAsync({ subjectPhone: values.subjectPhone, idempotencyKey: idempotencyKey() });
    setResult(r);
  });

  const errKey = (k: keyof ExportForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`security.validation.${m}`) : undefined;
  };

  const formId = 'data-export-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('security.export.eyebrow')}
      title={t('security.export.title')}
      description={t('security.export.info')}
      footer={
        result ? (
          <Button variant="primary" size="md" onClick={onClose}>
            {t('security.export.done')}
          </Button>
        ) : (
          <>
            <Button variant="ghost" size="md" type="button" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button variant="primary" size="md" type="submit" form={formId} disabled={exportSubject.isPending}>
              {t('security.export.generate')}
            </Button>
          </>
        )
      }
    >
      {result ? (
        <div className="flex flex-col gap-4">
          <div className="flex items-center gap-2 rounded-[var(--radius-sm)] bg-primary-soft px-3 py-2.5 text-[13px] text-primary">
            <FileCheck2 size={16} aria-hidden="true" />
            {t('security.export.readyTitle')}
          </div>
          <dl className="grid grid-cols-1 gap-3 rounded-[var(--radius)] border border-line p-3">
            <div>
              <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('security.export.records')}</dt>
              <dd className="mono mt-0.5 text-[13px] text-ink">{t('security.export.records', { count: result.recordCount })}</dd>
            </div>
            <div>
              <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('security.export.checksum')}</dt>
              <dd className="mono mt-0.5 break-all text-[12px] text-ink">{result.checksum}</dd>
            </div>
          </dl>
          {/* Download triggers the bundle fetch by token; contents never shown here. */}
          <Button variant="ghost" size="md">
            <Download size={15} aria-hidden="true" />
            {t('security.export.download')} ({result.format})
          </Button>
        </div>
      ) : (
        <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
          <p className="rounded-[var(--radius-sm)] bg-info-soft px-3 py-2 text-[12px] text-info">{t('security.export.info')}</p>
          <FieldShell label={t('security.export.subjectPhone')} htmlFor="ex-phone" error={errKey('subjectPhone')}>
            <TextInput id="ex-phone" type="tel" inputMode="tel" autoFocus className="mono" placeholder="+91 98765 43210" {...register('subjectPhone')} aria-invalid={Boolean(formState.errors.subjectPhone)} />
            <p className="mt-1 text-[12px] text-muted">{t('security.export.subjectPhoneHint')}</p>
          </FieldShell>
          <FieldShell label={t('security.export.purpose')} htmlFor="ex-purpose" error={errKey('purpose')}>
            <TextArea id="ex-purpose" rows={2} placeholder={t('security.export.purposePlaceholder')} {...register('purpose')} aria-invalid={Boolean(formState.errors.purpose)} />
          </FieldShell>
        </form>
      )}
    </SlideOver>
  );
}
