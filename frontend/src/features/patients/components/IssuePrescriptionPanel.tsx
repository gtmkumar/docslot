// Issue-prescription slide-over. Diagnosis/complaints + a medications list
// editor. Gated upstream by docslot.prescription.create. POST carries a stable
// Idempotency-Key. NOT URL-addressable (clinical write context).

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Plus, X } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/Button';
import { SlideOver } from '@/components/ui/SlideOver';
import { FieldShell, TextArea, TextInput, labelClass } from '@/components/ui/Field';
import { idempotencyKey } from '@/lib/api-client';
import { DOCTORS } from '@/lib/data';
import { useIssuePrescription } from '../api';
import type { Medication } from '@/lib/mock/contracts';

const emptyMed = (): Medication => ({ name: '', dose: '', frequency: '', duration: '' });

export function IssuePrescriptionPanel({ patientId, open, onClose }: { patientId: string; open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const issue = useIssuePrescription(patientId);

  const [diagnosis, setDiagnosis] = useState('');
  const [complaints, setComplaints] = useState('');
  const [advice, setAdvice] = useState('');
  const [meds, setMeds] = useState<Medication[]>([emptyMed()]);
  const [touched, setTouched] = useState(false);

  const filledMeds = meds.filter((m) => m.name.trim().length > 0);
  const diagnosisOk = diagnosis.trim().length > 0 || complaints.trim().length > 0;
  const medsOk = filledMeds.length > 0;

  const setMed = (i: number, patch: Partial<Medication>) =>
    setMeds((cur) => cur.map((m, idx) => (idx === i ? { ...m, ...patch } : m)));

  const onSubmit = async () => {
    setTouched(true);
    if (!diagnosisOk || !medsOk) return;
    await issue.mutateAsync({
      bookingId: crypto.randomUUID(),
      patientId,
      doctorId: DOCTORS[0].id,
      chiefComplaints: complaints || null,
      diagnosis: diagnosis || null,
      medications: filledMeds,
      advice: advice || null,
      idempotencyKey: idempotencyKey(),
    });
    toast.success(t('clinical.rx.issued'));
    onClose();
  };

  return (
    <SlideOver
      open={open}
      onClose={onClose}
      eyebrow={t('clinical.title')}
      title={t('clinical.rx.issueTitle')}
      footer={
        <>
          <Button variant="ghost" size="md" type="button" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="primary" size="md" type="button" disabled={issue.isPending} onClick={() => void onSubmit()}>
            {t('clinical.rx.submit')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <FieldShell label={t('clinical.rx.diagnosis')} htmlFor="rx-dx" error={touched && !diagnosisOk ? t('clinical.validation.diagnosis') : undefined}>
          <TextInput id="rx-dx" autoFocus value={diagnosis} onChange={(e) => setDiagnosis(e.target.value)} aria-invalid={touched && !diagnosisOk} />
        </FieldShell>

        <FieldShell label={t('clinical.rx.chiefComplaints')} htmlFor="rx-cc">
          <TextArea id="rx-cc" rows={2} value={complaints} onChange={(e) => setComplaints(e.target.value)} />
        </FieldShell>

        <section>
          <div className="mb-2 flex items-center justify-between">
            <span className={labelClass}>{t('clinical.rx.medications')}</span>
            <Button variant="ghost" size="sm" onClick={() => setMeds((m) => [...m, emptyMed()])}>
              <Plus size={13} aria-hidden="true" />
              {t('clinical.rx.addMed')}
            </Button>
          </div>
          <ul className="flex flex-col gap-2">
            {meds.map((m, i) => (
              <li key={i} className="rounded-[var(--radius-sm)] border border-line p-2.5">
                <div className="flex items-center gap-2">
                  <TextInput placeholder={t('clinical.rx.medName')} value={m.name} onChange={(e) => setMed(i, { name: e.target.value })} className="flex-1" />
                  {meds.length > 1 ? (
                    <button type="button" aria-label={t('common.close')} onClick={() => setMeds((cur) => cur.filter((_, idx) => idx !== i))} className="rounded p-1 text-muted hover:bg-surface-sunk hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
                      <X size={14} aria-hidden="true" />
                    </button>
                  ) : null}
                </div>
                <div className="mt-2 grid grid-cols-3 gap-2">
                  <TextInput placeholder={t('clinical.rx.med.dose')} value={m.dose} onChange={(e) => setMed(i, { dose: e.target.value })} className="text-[12px]" />
                  <TextInput placeholder={t('clinical.rx.med.frequency')} value={m.frequency} onChange={(e) => setMed(i, { frequency: e.target.value })} className="text-[12px]" />
                  <TextInput placeholder={t('clinical.rx.med.duration')} value={m.duration} onChange={(e) => setMed(i, { duration: e.target.value })} className="text-[12px]" />
                </div>
              </li>
            ))}
          </ul>
          {touched && !medsOk ? <p role="alert" className="mt-1 text-[12px] text-danger">{t('clinical.validation.medication')}</p> : null}
        </section>

        <FieldShell label={t('clinical.rx.advice')} htmlFor="rx-advice">
          <TextArea id="rx-advice" rows={2} value={advice} onChange={(e) => setAdvice(e.target.value)} />
        </FieldShell>
      </div>
    </SlideOver>
  );
}
