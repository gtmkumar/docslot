// Raise-dispute slide-over. Reason + description for an attribution. Gated
// upstream by commission.dispute.raise. POST carries a stable Idempotency-Key.

import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useRaiseDispute } from '../api';

const schema = z.object({
  disputeReason: z.string().trim().min(1, 'reason'),
  description: z.string().trim().min(1, 'description'),
});
type DisputeForm = z.infer<typeof schema>;

export function RaiseDisputePanel({ attributionId, open, onClose }: { attributionId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const raise = useRaiseDispute();

  const { register, handleSubmit, formState } = useForm<DisputeForm>({
    defaultValues: { disputeReason: '', description: '' },
    resolver: async (values) => {
      const parsed = schema.safeParse(values);
      if (parsed.success) return { values: parsed.data, errors: {} };
      const errors: Record<string, { type: string; message: string }> = {};
      for (const i of parsed.error.issues) errors[String(i.path[0])] ??= { type: 'zod', message: i.message };
      return { values: {}, errors };
    },
  });

  const onSubmit = handleSubmit(async (values) => {
    await raise.mutateAsync({ attributionId, disputeReason: values.disputeReason, description: values.description, idempotencyKey: idempotencyKey() });
    toast.success(t('commission.disputes.raise.raised'));
    onClose();
  });

  const errKey = (k: keyof DisputeForm) => {
    const m = formState.errors[k]?.message;
    return m ? t(`commission.validation.${m}`) : undefined;
  };

  const formId = 'raise-dispute-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('commission.disputes.raise.eyebrow')}
      title={t('commission.disputes.raise.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={raise.isPending}>
            {t('commission.disputes.raise.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <FieldShell label={t('commission.disputes.raise.reason')} htmlFor="rd-reason" error={errKey('disputeReason')}>
          <TextInput id="rd-reason" autoFocus className="mono" placeholder={t('commission.disputes.raise.reasonPlaceholder')} {...register('disputeReason')} aria-invalid={Boolean(formState.errors.disputeReason)} />
        </FieldShell>
        <FieldShell label={t('commission.disputes.raise.description')} htmlFor="rd-desc" error={errKey('description')}>
          <TextArea id="rd-desc" rows={3} placeholder={t('commission.disputes.raise.descriptionPlaceholder')} {...register('description')} aria-invalid={Boolean(formState.errors.description)} />
        </FieldShell>
      </form>
    </SlideOver>
  );
}
