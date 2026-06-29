// Generate referral link slide-over (Care Partner self-service). The portal
// resolves the partner + tenant from the JWT, so the only input is an optional
// campaign tag. Gated upstream by commission.broker.generate_link_self. POST
// carries a stable Idempotency-Key.

import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextInput } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useCreateReferralLink } from '../api';

interface LinkForm {
  campaignName: string;
}

export function GenerateLinkPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const create = useCreateReferralLink();
  const { register, handleSubmit } = useForm<LinkForm>({ defaultValues: { campaignName: '' } });

  const onSubmit = handleSubmit(async (values) => {
    await create.mutateAsync({
      campaignName: values.campaignName.trim() || null,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('portal.links.panel.created'));
    onClose();
  });

  const formId = 'generate-link-form';
  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('portal.links.panel.eyebrow')}
      title={t('portal.links.panel.title')}
      description={t('portal.links.panel.desc')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="submit" form={formId} disabled={create.isPending}>
            {t('portal.links.panel.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} className="flex flex-col gap-4" onSubmit={onSubmit}>
        <p className="text-[13px] text-muted">{t('portal.links.panel.desc')}</p>
        <FieldShell label={t('portal.links.panel.campaign')} htmlFor="gl-campaign" optional={t('portal.links.panel.campaignOptional')}>
          <TextInput id="gl-campaign" autoFocus placeholder={t('portal.links.panel.campaignPlaceholder')} {...register('campaignName')} />
        </FieldShell>
      </form>
    </SlideOver>
  );
}
