// Register Care Partner slide-over. phone-canonical + type + PAN (masked,
// sensitive — never displayed in full anywhere) + tier + GST. Gated upstream by
// commission.broker.invite. POST carries a stable Idempotency-Key. The register
// result returns NO PAN, so there is no show-once step.

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useRegisterBroker } from '../api';
import type { BrokerType, TierLevel } from '@/lib/mock/contracts';

const BROKER_TYPES: BrokerType[] = ['medical_rep', 'corporate_hr', 'insurance_panel', 'aggregator_agent', 'community_worker', 'hotel_concierge', 'individual', 'platform_partner'];
const TIERS: TierLevel[] = ['basic', 'silver', 'gold', 'platinum'];

const schema = z.object({
  phone: z.string().trim().regex(/^\+?[0-9\s-]{8,16}$/, 'phone'),
  fullName: z.string().trim().min(1, 'name'),
  email: z.string().trim().optional().default(''),
  brokerType: z.enum(['medical_rep', 'corporate_hr', 'insurance_panel', 'aggregator_agent', 'community_worker', 'hotel_concierge', 'individual', 'platform_partner']),
  pan: z
    .string()
    .trim()
    .optional()
    .default('')
    .refine((v) => v === '' || /^[A-Z]{5}[0-9]{4}[A-Z]$/.test(v), 'pan'),
  gstNumber: z.string().trim().optional().default(''),
  tierLevel: z.enum(['basic', 'silver', 'gold', 'platinum']),
});
type RegisterForm = z.infer<typeof schema>;

export function RegisterBrokerPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const register_ = useRegisterBroker();

  const { register, handleSubmit, formState } = useForm<RegisterForm>({
    defaultValues: { phone: '', fullName: '', email: '', brokerType: 'medical_rep', pan: '', gstNumber: '', tierLevel: 'basic' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    await register_.mutateAsync({
      phone: values.phone,
      fullName: values.fullName,
      email: values.email || null,
      brokerType: values.brokerType,
      pan: values.pan || null,
      gstNumber: values.gstNumber || null,
      tierLevel: values.tierLevel,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('commission.register.registered'));
    onClose();
  });

  const errKey = (k: keyof RegisterForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`commission.validation.${m}`) : undefined;
  };

  const formId = 'register-broker-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('commission.register.eyebrow')}
      title={t('commission.register.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={register_.isPending}>
            {t('commission.register.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('commission.register.phone')} htmlFor="rb-phone" error={errKey('phone')}>
          <TextInput id="rb-phone" type="tel" inputMode="tel" autoFocus className="mono" placeholder="+91 98765 43210" {...register('phone')} aria-invalid={Boolean(formState.errors.phone)} />
        </FieldShell>

        <FieldShell label={t('commission.register.fullName')} htmlFor="rb-name" error={errKey('fullName')}>
          <TextInput id="rb-name" {...register('fullName')} aria-invalid={Boolean(formState.errors.fullName)} />
        </FieldShell>

        <FieldShell label={t('commission.register.email')} htmlFor="rb-email">
          <TextInput id="rb-email" type="email" {...register('email')} />
        </FieldShell>

        <div className="grid grid-cols-2 gap-3">
          <div>
            <label htmlFor="rb-type" className={labelClass}>
              {t('commission.register.type')}
            </label>
            <Select id="rb-type" {...register('brokerType')}>
              {BROKER_TYPES.map((bt) => (
                <option key={bt} value={bt}>
                  {t(`commission.type.${bt}`)}
                </option>
              ))}
            </Select>
          </div>
          <div>
            <label htmlFor="rb-tier" className={labelClass}>
              {t('commission.register.tier')}
            </label>
            <Select id="rb-tier" {...register('tierLevel')}>
              {TIERS.map((tier) => (
                <option key={tier} value={tier}>
                  {t(`commission.tier.${tier}`)}
                </option>
              ))}
            </Select>
          </div>
        </div>

        {/* PAN — sensitive. Masked-style input; stored encrypted; NEVER displayed
            in full elsewhere. */}
        <FieldShell label={t('commission.register.pan')} htmlFor="rb-pan" error={errKey('pan')}>
          <TextInput id="rb-pan" className="mono uppercase" autoComplete="off" placeholder="ABCDE1234F" maxLength={10} {...register('pan')} aria-invalid={Boolean(formState.errors.pan)} />
          <p className="mt-1 text-[12px] text-muted">{t('commission.register.panHint')}</p>
        </FieldShell>

        <FieldShell label={t('commission.register.gst')} htmlFor="rb-gst" optional={t('commission.register.gstOptional')}>
          <TextInput id="rb-gst" className="mono uppercase" {...register('gstNumber')} />
        </FieldShell>
      </form>
    </SlideOver>
  );
}
