// Record break-glass access slide-over. Emergency access requires a MANDATORY
// justification — submit is disabled until it's entered. A prominent warning
// states the access is logged, the patient is notified, and it is queued for
// admin review. Gated upstream by docslot.medical_access.break_glass. POST
// carries a stable Idempotency-Key.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { KeyRound } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useRecordBreakGlass } from '../api';

export function BreakGlassPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const record = useRecordBreakGlass();

  const [resourceType, setResourceType] = useState('medical_record');
  const [resourceId, setResourceId] = useState('');
  const [justification, setJustification] = useState('');

  const justificationOk = justification.trim().length > 0;
  const canSubmit = justificationOk && resourceType.trim().length > 0 && !record.isPending;

  const onSubmit = async () => {
    if (!canSubmit) return;
    await record.mutateAsync({
      resourceType: resourceType.trim(),
      resourceId: resourceId.trim() || crypto.randomUUID(),
      justification: justification.trim(),
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('security.breakGlass.submitted'));
    onClose();
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('security.breakGlass.eyebrow')}
      title={t('security.breakGlass.title')}
      description={t('security.breakGlass.warning')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          {/* Disabled until a justification is entered — no silent emergency access. */}
          <Button variant="primary" size="md" type="button" disabled={!canSubmit} onClick={() => void onSubmit()}>
            {t('security.breakGlass.submit')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <div className="flex items-start gap-2.5 rounded-[var(--radius)] border border-warn-soft bg-warn-soft px-3 py-3 text-warn">
          <KeyRound size={18} className="mt-0.5 shrink-0" aria-hidden="true" />
          <p className="text-[12px]">{t('security.breakGlass.warning')}</p>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <FieldShell label={t('security.breakGlass.resourceType')} htmlFor="bg-type">
            <TextInput id="bg-type" className="mono" placeholder={t('security.breakGlass.resourceTypePlaceholder')} value={resourceType} onChange={(e) => setResourceType(e.target.value)} />
          </FieldShell>
          <FieldShell label={t('security.breakGlass.resourceId')} htmlFor="bg-id">
            <TextInput id="bg-id" className="mono" value={resourceId} onChange={(e) => setResourceId(e.target.value)} />
          </FieldShell>
        </div>

        <FieldShell label={t('security.breakGlass.justification')} htmlFor="bg-just">
          <TextArea
            id="bg-just"
            rows={3}
            autoFocus
            placeholder={t('security.breakGlass.justificationPlaceholder')}
            value={justification}
            onChange={(e) => setJustification(e.target.value)}
            aria-invalid={justification.length > 0 && !justificationOk}
          />
          <p className={`mt-1 text-[11px] ${justificationOk ? 'text-muted' : 'text-warn'}`}>{t('security.breakGlass.justificationRequired')}</p>
        </FieldShell>
      </div>
    </SlideOver>
  );
}
