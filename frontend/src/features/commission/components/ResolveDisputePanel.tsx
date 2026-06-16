// Resolve-dispute slide-over. Outcome + notes + optional clawback adjustment.
// Gated upstream by commission.dispute.resolve. POST carries a stable
// Idempotency-Key.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, Select, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useResolveDispute } from '../api';
import type { DisputeStatus } from '@/lib/mock/contracts';

const OUTCOMES: DisputeStatus[] = ['resolved_broker_wins', 'resolved_tenant_wins', 'resolved_compromise', 'closed_no_action'];

export function ResolveDisputePanel({ disputeId, open, onClose }: { disputeId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const resolve = useResolveDispute();
  const [outcome, setOutcome] = useState<DisputeStatus>('resolved_compromise');
  const [notes, setNotes] = useState('');
  const [clawback, setClawback] = useState('');

  const onSubmit = async () => {
    await resolve.mutateAsync({
      disputeId,
      status: outcome,
      resolutionNotes: notes || null,
      amountAdjustmentInr: clawback.trim() === '' ? null : Number(clawback) || null,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('commission.disputes.resolvePanel.resolved'));
    onClose();
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('commission.disputes.resolvePanel.eyebrow')}
      title={t('commission.disputes.resolvePanel.title')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="button" disabled={resolve.isPending} onClick={() => void onSubmit()}>
            {t('commission.disputes.resolvePanel.submit')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <div>
          <label htmlFor="rsv-outcome" className={labelClass}>
            {t('commission.disputes.resolvePanel.outcome')}
          </label>
          <Select id="rsv-outcome" value={outcome} onChange={(e) => setOutcome(e.target.value as DisputeStatus)}>
            {OUTCOMES.map((o) => (
              <option key={o} value={o}>
                {t(`commission.disputes.status.${o}`)}
              </option>
            ))}
          </Select>
        </div>

        <FieldShell label={t('commission.disputes.resolvePanel.notes')} htmlFor="rsv-notes">
          <TextArea id="rsv-notes" rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} placeholder={t('commission.disputes.resolvePanel.notesPlaceholder')} />
        </FieldShell>

        <FieldShell label={t('commission.disputes.resolvePanel.clawback')} htmlFor="rsv-claw">
          <TextInput id="rsv-claw" type="number" className="mono" value={clawback} onChange={(e) => setClawback(e.target.value)} />
          <p className="mt-1 text-[12px] text-muted">{t('commission.disputes.resolvePanel.clawbackHint')}</p>
        </FieldShell>
      </div>
    </SlideOver>
  );
}
