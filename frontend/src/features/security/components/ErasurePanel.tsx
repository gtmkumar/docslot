// DPDP §12 right-to-erasure slide-over — IRREVERSIBLE cryptographic erasure.
//
// Conservative-by-design (this destroys the subject's encryption keys):
//  - A prominent danger warning explains the consequences.
//  - Submit is DISABLED until: a valid subject phone, a reason, AND the user has
//    typed the exact confirmation word ("ERASE"). No one-click path.
//  - On success the deletion certificate is handed to the one-time
//    `deletionCertificate` panel (never cached, never URL-addressable).
//
// Gated upstream by platform.deletion.certify. POST carries a stable Idempotency-Key.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ShieldX } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { useUI } from '@/stores/ui';
import { useEraseSubject } from '../api';

export function ErasurePanel({ requestId, open, onClose }: { requestId?: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const erase = useEraseSubject();
  const openPanel = useUI((s) => s.openPanel);

  const [phone, setPhone] = useState('');
  const [reason, setReason] = useState('');
  const [confirm, setConfirm] = useState('');

  const confirmWord = t('security.erase.confirmWord');
  const phoneOk = /^\+?[0-9\s-]{8,16}$/.test(phone.trim());
  const reasonOk = reason.trim().length > 0;
  const confirmOk = confirm.trim() === confirmWord;
  const canSubmit = phoneOk && reasonOk && confirmOk && !erase.isPending;

  const onErase = async () => {
    if (!canSubmit) return;
    const result = await erase.mutateAsync({
      deletionRequestId: requestId ?? crypto.randomUUID(),
      subjectPhone: phone.trim(),
      idempotencyKey: idempotencyKey(),
    });
    // Hand the certificate to the one-time reveal panel (never cached/URL'd).
    openPanel({ type: 'deletionCertificate', result });
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('security.erase.eyebrow')}
      title={t('security.erase.title')}
      description={t('security.erase.warningBody')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          {/* Danger + disabled until typed confirmation — no accidental fire. */}
          <Button variant="danger" size="md" type="button" disabled={!canSubmit} onClick={() => void onErase()}>
            {t('security.erase.erase')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {/* Strong irreversible warning. */}
        <div className="flex items-start gap-2.5 rounded-[var(--radius)] border border-danger-soft bg-danger-soft px-3 py-3 text-danger">
          <ShieldX size={18} className="mt-0.5 shrink-0" aria-hidden="true" />
          <div>
            <p className="text-[13px] font-semibold">{t('security.erase.warningTitle')}</p>
            <p className="mt-1 text-[12px]">{t('security.erase.warningBody')}</p>
          </div>
        </div>

        <FieldShell label={t('security.erase.subjectPhone')} htmlFor="er-phone">
          <TextInput id="er-phone" type="tel" inputMode="tel" autoFocus className="mono" placeholder="+91 98765 43210" value={phone} onChange={(e) => setPhone(e.target.value)} aria-invalid={phone.length > 0 && !phoneOk} />
        </FieldShell>

        <FieldShell label={t('security.erase.reason')} htmlFor="er-reason">
          <TextArea id="er-reason" rows={2} placeholder={t('security.erase.reasonPlaceholder')} value={reason} onChange={(e) => setReason(e.target.value)} aria-invalid={reason.length > 0 && !reasonOk} />
        </FieldShell>

        <FieldShell label={t('security.erase.confirmLabel')} htmlFor="er-confirm">
          <TextInput
            id="er-confirm"
            className="mono"
            autoComplete="off"
            placeholder={t('security.erase.confirmPlaceholder')}
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            aria-invalid={confirm.length > 0 && !confirmOk}
          />
        </FieldShell>
      </div>
    </SlideOver>
  );
}
