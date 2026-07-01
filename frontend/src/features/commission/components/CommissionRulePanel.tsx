// Create commission rule slide-over. Flat / percentage / tiered with caps,
// floors, priority, monthly per-partner cap, first-booking-only. The PCPNDT
// exclusion is shown as ALWAYS enforced (a note, not a toggle). Gated upstream by
// commission.rules.create. POST carries a stable Idempotency-Key.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { ShieldCheck } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useCreateCommissionRule } from '../api';
import type { CalcType } from '@/lib/mock/contracts';

const schema = z.object({
  ruleName: z.string().trim().min(1, 'ruleName'),
  ruleKey: z.string().trim().regex(/^[a-z][a-z0-9_]*$/, 'ruleKey'),
  flatAmountInr: z.string().trim().optional().default(''),
  percentage: z.string().trim().optional().default(''),
  minCommissionInr: z.string().trim().optional().default(''),
  maxCommissionInr: z.string().trim().optional().default(''),
  maxMonthlyPerBrokerInr: z.string().trim().optional().default(''),
  priority: z.string().trim().optional().default('10'),
});
type RuleForm = z.infer<typeof schema>;

// Empty → null (unset); otherwise the parsed number, PRESERVING a legitimate 0.
// The old `Number(s) || null` coerced an entered 0 to null (0 is falsy), silently
// dropping a 0 cap/floor/amount as "unset" (#57).
const num = (s: string): number | null => {
  const trimmed = s.trim();
  if (trimmed === '') return null;
  const n = Number(trimmed);
  return Number.isFinite(n) ? n : null;
};

export function CommissionRulePanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreateCommissionRule();
  const [calcType, setCalcType] = useState<CalcType>('flat');
  const [firstBookingOnly, setFirstBookingOnly] = useState(false);

  const { register, handleSubmit, formState } = useForm<RuleForm>({
    defaultValues: { ruleName: '', ruleKey: '', flatAmountInr: '', percentage: '', minCommissionInr: '', maxCommissionInr: '', maxMonthlyPerBrokerInr: '', priority: '10' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    await create.mutateAsync({
      ruleName: values.ruleName,
      ruleKey: values.ruleKey,
      calcType,
      flatAmountInr: calcType === 'flat' ? num(values.flatAmountInr) : null,
      percentage: calcType === 'percentage' ? num(values.percentage) : null,
      minCommissionInr: num(values.minCommissionInr),
      maxCommissionInr: num(values.maxCommissionInr),
      maxMonthlyPerBrokerInr: num(values.maxMonthlyPerBrokerInr),
      priority: num(values.priority) ?? 10,
      firstBookingOnly,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('commission.rules.panel.created'));
    onClose();
  });

  const errKey = (k: keyof RuleForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`commission.validation.${m}`) : undefined;
  };

  const formId = 'commission-rule-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('commission.rules.panel.eyebrow')}
      title={t('commission.rules.panel.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={create.isPending}>
            {t('commission.rules.panel.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('commission.rules.panel.ruleName')} htmlFor="cr-name" error={errKey('ruleName')}>
          <TextInput id="cr-name" autoFocus {...register('ruleName')} aria-invalid={Boolean(formState.errors.ruleName)} />
        </FieldShell>

        <FieldShell label={t('commission.rules.panel.ruleKey')} htmlFor="cr-key" error={errKey('ruleKey')}>
          <TextInput id="cr-key" className="mono" {...register('ruleKey')} aria-invalid={Boolean(formState.errors.ruleKey)} />
        </FieldShell>

        <div>
          <label htmlFor="cr-calc" className={labelClass}>
            {t('commission.rules.panel.calcType')}
          </label>
          <Select id="cr-calc" value={calcType} onChange={(e) => setCalcType(e.target.value as CalcType)}>
            <option value="flat">{t('commission.rules.calc.flat')}</option>
            <option value="percentage">{t('commission.rules.calc.percentage')}</option>
            <option value="tiered_table">{t('commission.rules.calc.tiered_table')}</option>
          </Select>
        </div>

        {calcType === 'flat' ? (
          <FieldShell label={t('commission.rules.panel.flatAmount')} htmlFor="cr-flat">
            <TextInput id="cr-flat" type="number" min={0} className="mono" {...register('flatAmountInr')} />
          </FieldShell>
        ) : calcType === 'percentage' ? (
          <FieldShell label={t('commission.rules.panel.percentage')} htmlFor="cr-pct">
            <TextInput id="cr-pct" type="number" min={0} max={100} className="mono" {...register('percentage')} />
          </FieldShell>
        ) : null}

        <div className="grid grid-cols-2 gap-3">
          <FieldShell label={t('commission.rules.panel.minCommission')} htmlFor="cr-min">
            <TextInput id="cr-min" type="number" min={0} className="mono" {...register('minCommissionInr')} />
          </FieldShell>
          <FieldShell label={t('commission.rules.panel.maxCommission')} htmlFor="cr-max">
            <TextInput id="cr-max" type="number" min={0} className="mono" {...register('maxCommissionInr')} />
          </FieldShell>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <FieldShell label={t('commission.rules.panel.maxMonthly')} htmlFor="cr-monthly">
            <TextInput id="cr-monthly" type="number" min={0} className="mono" {...register('maxMonthlyPerBrokerInr')} />
          </FieldShell>
          <FieldShell label={t('commission.rules.panel.priority')} htmlFor="cr-prio">
            <TextInput id="cr-prio" type="number" min={0} className="mono" {...register('priority')} />
          </FieldShell>
        </div>

        <label className="flex items-center gap-2.5 rounded-[var(--radius-sm)] border border-line px-3 py-2.5">
          <input type="checkbox" checked={firstBookingOnly} onChange={(e) => setFirstBookingOnly(e.target.checked)} className="h-4 w-4 accent-[var(--primary)]" />
          <span className="text-[13px] text-ink">{t('commission.rules.panel.firstBookingOnly')}</span>
        </label>

        {/* PCPNDT exclusion — enforced, never a toggle. */}
        <div className="flex items-center gap-2 rounded-[var(--radius-sm)] bg-primary-soft px-3 py-2.5 text-[12px] text-primary">
          <ShieldCheck size={15} aria-hidden="true" />
          {t('commission.rules.panel.pndtNote')}
        </div>
      </form>
    </SlideOver>
  );
}
