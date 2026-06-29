// Create campaign slide-over. Name + bonus kind + value + window + budget. The
// bonus kind is restricted to the two supported kinds (flat_bonus_per_booking,
// percentage_multiplier) — tier_upgrade is not offered. Gated upstream by
// commission.campaign.manage. POST carries a stable Idempotency-Key.

import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useCreateCampaign } from '../api';
import type { CampaignBonusType } from '@/lib/mock/contracts';

const BONUS_TYPES: CampaignBonusType[] = ['flat_bonus_per_booking', 'percentage_multiplier'];

const schema = z
  .object({
    campaignName: z.string().trim().min(1, 'campaignName'),
    bonusValue: z.string().trim().optional().default(''),
    startsAt: z.string().trim().min(1, 'startsAt'),
    endsAt: z.string().trim().min(1, 'endsAt'),
    totalBudgetInr: z.string().trim().optional().default(''),
  })
  .refine((v) => v.endsAt >= v.startsAt, { path: ['endsAt'], message: 'endsAt' });
type CampaignForm = z.infer<typeof schema>;

const num = (s: string): number | null => (s.trim() === '' ? null : Number(s) || null);
// A date input value (YYYY-MM-DD) → an ISO instant at start-of-day IST (+05:30),
// so the campaign window is timezone-explicit (Asia/Kolkata).
const toIsoIst = (date: string): string => new Date(`${date}T00:00:00+05:30`).toISOString();

export function CreateCampaignPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreateCampaign();
  const [bonusType, setBonusType] = useState<CampaignBonusType>('flat_bonus_per_booking');

  const { register, handleSubmit, formState } = useForm<CampaignForm>({
    defaultValues: { campaignName: '', bonusValue: '', startsAt: '', endsAt: '', totalBudgetInr: '' },
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
      campaignName: values.campaignName,
      bonusType,
      bonusValue: num(values.bonusValue),
      startsAt: toIsoIst(values.startsAt),
      endsAt: toIsoIst(values.endsAt),
      totalBudgetInr: num(values.totalBudgetInr),
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('commission.campaigns.panel.created'));
    onClose();
  });

  const errKey = (k: keyof CampaignForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`commission.validation.${m}`) : undefined;
  };

  const formId = 'create-campaign-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('commission.campaigns.panel.eyebrow')}
      title={t('commission.campaigns.panel.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={create.isPending}>
            {t('commission.campaigns.panel.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('commission.campaigns.panel.campaignName')} htmlFor="cc-name" error={errKey('campaignName')}>
          <TextInput id="cc-name" autoFocus {...register('campaignName')} aria-invalid={Boolean(formState.errors.campaignName)} />
        </FieldShell>

        <div>
          <label htmlFor="cc-bonus" className={labelClass}>
            {t('commission.campaigns.panel.bonusType')}
          </label>
          <Select id="cc-bonus" value={bonusType} onChange={(e) => setBonusType(e.target.value as CampaignBonusType)}>
            {BONUS_TYPES.map((bt) => (
              <option key={bt} value={bt}>
                {t(`commission.campaigns.bonus.${bt}`)}
              </option>
            ))}
          </Select>
        </div>

        <FieldShell
          label={
            bonusType === 'percentage_multiplier'
              ? t('commission.campaigns.panel.bonusMultiplier')
              : t('commission.campaigns.panel.bonusFlat')
          }
          htmlFor="cc-value"
        >
          <TextInput id="cc-value" type="number" min={0} step="0.1" className="mono" {...register('bonusValue')} />
        </FieldShell>

        <div className="grid grid-cols-2 gap-3">
          <FieldShell label={t('commission.campaigns.panel.startsAt')} htmlFor="cc-starts" error={errKey('startsAt')}>
            <TextInput id="cc-starts" type="date" {...register('startsAt')} aria-invalid={Boolean(formState.errors.startsAt)} />
          </FieldShell>
          <FieldShell label={t('commission.campaigns.panel.endsAt')} htmlFor="cc-ends" error={errKey('endsAt')}>
            <TextInput id="cc-ends" type="date" {...register('endsAt')} aria-invalid={Boolean(formState.errors.endsAt)} />
          </FieldShell>
        </div>

        <FieldShell label={t('commission.campaigns.panel.totalBudget')} htmlFor="cc-budget" optional={t('commission.campaigns.panel.budgetOptional')}>
          <TextInput id="cc-budget" type="number" min={0} className="mono" {...register('totalBudgetInr')} />
          <p className="mt-1 text-[12px] text-muted">{t('commission.campaigns.panel.budgetHint')}</p>
        </FieldShell>
      </form>
    </SlideOver>
  );
}
