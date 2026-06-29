// Clinical break-glass (emergency access) slide-over (Phase-3 slice 4). Opened
// when a clinical read is blocked by consent (a 403). Gated UPSTREAM by
// docslot.medical_access.break_glass — the affordance that opens this panel is
// only rendered when can() is true. NEVER a role check in JSX.
//
// The resourceType + resourceId are DERIVED FROM CONTEXT (the read that was
// blocked), not typed free-hand. The clinician must enter a justification of at
// least 10 characters; submit stays disabled until that's met (no silent
// emergency access). A prominent warning states the access is logged, the patient
// is notified, and it is queued for admin review.
//
// On success the mutation invalidates the patient's clinical namespace, so the
// gated read re-fetches and — now that a grant exists — succeeds. POST carries a
// stable Idempotency-Key.

import { useActionState, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { KeyRound } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { toUserError } from '@/lib/backend';
import { useUI, type Panel } from '@/stores/ui';
import { useBreakGlass } from '../api';
import type { BreakGlassResourceType } from '@/lib/mock/contracts';

const MIN_JUSTIFICATION = 10;

export function ClinicalBreakGlassPanel({
  patientId,
  resourceType,
  resourceId,
  reopen,
  open,
  onClose,
}: {
  patientId: string;
  resourceType: BreakGlassResourceType;
  resourceId: string | null;
  /** The gated detail panel to restore on success, so the now-unblocked read
   *  re-runs in place. Omitted when the trigger was a screen-level list. */
  reopen?: Panel;
  open: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const grant = useBreakGlass(patientId);
  const openPanel = useUI((s) => s.openPanel);
  const [justification, setJustification] = useState('');
  const justificationOk = justification.trim().length >= MIN_JUSTIFICATION;

  const [, submit, isPending] = useActionState(async () => {
    if (!justificationOk) return null;
    try {
      await grant.mutateAsync({
        patientId,
        resourceType,
        resourceId,
        justification: justification.trim(),
        idempotencyKey: idempotencyKey(),
      });
      toast.success(t('clinical.breakGlass.granted'));
      // Restore the gated detail panel (its read is invalidated, so it re-fetches
      // and now succeeds) or just close when the trigger was a screen-level list.
      if (reopen) openPanel(reopen);
      else onClose();
    } catch (e) {
      toast.error(toUserError(e));
    }
    return null;
  }, null);

  const formId = 'clinical-break-glass-form';

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('clinical.breakGlass.title')}
      description={t('clinical.breakGlass.warning')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          {/* Disabled until a >=10-char justification is entered — no silent access. */}
          <Button variant="danger" size="md" type="submit" form={formId} disabled={!justificationOk || isPending}>
            <KeyRound size={15} aria-hidden="true" />
            {t('clinical.breakGlass.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} action={submit} className="flex flex-col gap-4">
        <div className="flex items-start gap-2.5 rounded-[var(--radius)] border border-warn-soft bg-warn-soft px-3 py-3 text-warn">
          <KeyRound size={18} className="mt-0.5 shrink-0" aria-hidden="true" />
          <p className="text-[12px]">{t('clinical.breakGlass.warning')}</p>
        </div>

        {/* The blocked resource — DERIVED FROM CONTEXT, shown read-only (no free entry). */}
        <dl className="grid grid-cols-2 gap-3 rounded-[var(--radius-sm)] border border-line p-3">
          <div>
            <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('clinical.breakGlass.resource')}</dt>
            <dd className="mt-0.5 text-[13px] text-ink">{t(`clinical.breakGlass.resourceType.${resourceType}`)}</dd>
          </div>
          <div>
            <dt className="text-[11px] uppercase tracking-wider text-muted-2">{t('clinical.breakGlass.scope')}</dt>
            <dd className="mt-0.5 text-[13px] text-ink">
              {resourceId ? t('clinical.breakGlass.scopeRecord') : t('clinical.breakGlass.scopePatient')}
            </dd>
          </div>
        </dl>

        <FieldShell label={t('clinical.breakGlass.justification')} htmlFor="cbg-just">
          <TextArea
            id="cbg-just"
            rows={3}
            autoFocus
            placeholder={t('clinical.breakGlass.justificationPlaceholder')}
            value={justification}
            onChange={(e) => setJustification(e.target.value)}
            aria-invalid={justification.length > 0 && !justificationOk}
          />
          <p className={`mt-1 text-[11px] ${justificationOk ? 'text-muted' : 'text-warn'}`}>
            {t('clinical.breakGlass.justificationRequired', { min: MIN_JUSTIFICATION })}
          </p>
        </FieldShell>
      </form>
    </SlideOver>
  );
}
