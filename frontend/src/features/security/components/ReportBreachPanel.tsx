// Report-a-breach slide-over (DPDP §8(6)). Nature + description + affected count
// + severity → creates a breach record and starts the 72h DPB clock. Gated
// upstream by platform.breach.read. POST carries a stable Idempotency-Key.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useReportBreach } from '../api';

const schema = z.object({
  breachType: z.string().trim().min(1, 'nature'),
  description: z.string().trim().min(1, 'description'),
  affectedRecordCount: z.string().trim().optional().default(''),
});
type BreachForm = z.infer<typeof schema>;

type Severity = 'low' | 'medium' | 'high' | 'critical';

export function ReportBreachPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const report = useReportBreach();
  const [severity, setSeverity] = useState<Severity>('medium');

  const { register, handleSubmit, formState } = useForm<BreachForm>({
    defaultValues: { breachType: '', description: '', affectedRecordCount: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    await report.mutateAsync({
      breachType: values.breachType,
      severity,
      description: values.description,
      affectedRecordCount: Number(values.affectedRecordCount) || 0,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('security.breach.submitted'));
    onClose();
  });

  const errKey = (k: keyof BreachForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`security.validation.${m}`) : undefined;
  };

  const sevs: Severity[] = ['low', 'medium', 'high', 'critical'];
  const formId = 'report-breach-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('security.breach.eyebrow')}
      title={t('security.breach.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={report.isPending}>
            {t('security.breach.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <p className="rounded-[var(--radius-sm)] bg-warn-soft px-3 py-2 text-[12px] text-warn">{t('security.breach.info')}</p>

        <FieldShell label={t('security.breach.nature')} htmlFor="br-type" error={errKey('breachType')}>
          <TextInput id="br-type" autoFocus className="mono" placeholder={t('security.breach.naturePlaceholder')} {...register('breachType')} aria-invalid={Boolean(formState.errors.breachType)} />
        </FieldShell>

        <FieldShell label={t('security.breach.description')} htmlFor="br-desc" error={errKey('description')}>
          <TextArea id="br-desc" rows={3} placeholder={t('security.breach.descriptionPlaceholder')} {...register('description')} aria-invalid={Boolean(formState.errors.description)} />
        </FieldShell>

        <FieldShell label={t('security.breach.affected')} htmlFor="br-affected">
          <TextInput id="br-affected" type="number" min={0} className="mono" {...register('affectedRecordCount')} />
        </FieldShell>

        <div>
          <span className={labelClass}>{t('security.breach.severity')}</span>
          <div role="radiogroup" aria-label={t('security.breach.severity')} className="grid grid-cols-4 gap-2">
            {sevs.map((s) => (
              <button
                key={s}
                type="button"
                role="radio"
                aria-checked={severity === s}
                onClick={() => setSeverity(s)}
                className={[
                  'rounded-[var(--radius-sm)] border px-2 py-2 text-[12px] capitalize transition-colors',
                  severity === s ? 'border-primary bg-primary text-bg' : 'border-line text-ink hover:bg-surface-sunk',
                ].join(' ')}
              >
                {t(`security.breach.sev${s.charAt(0).toUpperCase()}${s.slice(1)}`)}
              </button>
            ))}
          </div>
        </div>
      </form>
    </SlideOver>
  );
}
