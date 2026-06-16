// Purpose-of-use gate. Clinical reads require a declared purpose (DPDP). Until
// the operator declares one, the records are LOCKED — no clinical fetch happens
// (the queries are disabled without a purpose). Once declared, the chosen purpose
// is attached to every clinical read (X-Purpose-Of-Use) and the access is logged.
//
// `value` is the active purpose (null = not declared). The parent owns the state
// so it resets on navigation away from the patient.

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Eye, Lock } from 'lucide-react';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { Select, labelClass } from '@/components/ui/Field';
import type { PurposeOfUse } from '@/lib/mock/contracts';

const PURPOSES: PurposeOfUse[] = ['treatment', 'follow_up', 'emergency', 'consultation', 'audit', 'patient_request', 'research'];

export function PurposeGate({ onDeclare }: { onDeclare: (p: PurposeOfUse) => void }) {
  const { t } = useTranslation();
  const [pick, setPick] = useState<PurposeOfUse>('treatment');

  return (
    <Card className="p-5">
      <div className="flex items-start gap-3">
        <span aria-hidden="true" className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-surface-sunk text-muted">
          <Lock size={20} />
        </span>
        <div className="min-w-0">
          <h2 className="text-base font-semibold text-ink">{t('clinical.purpose.gateTitle')}</h2>
          <p className="mt-1 max-w-xl text-[13px] text-muted">{t('clinical.purpose.gateBody')}</p>
        </div>
      </div>

      <div className="mt-4 flex flex-col gap-3 sm:flex-row sm:items-end">
        <div className="sm:w-64">
          <label htmlFor="pou" className={labelClass}>
            {t('clinical.purpose.pick')}
          </label>
          <Select id="pou" value={pick} onChange={(e) => setPick(e.target.value as PurposeOfUse)}>
            {PURPOSES.map((p) => (
              <option key={p} value={p}>
                {t(`clinical.purpose.${p}`)}
              </option>
            ))}
          </Select>
        </div>
        <Button variant="primary" size="md" onClick={() => onDeclare(pick)}>
          <Eye size={15} aria-hidden="true" />
          {t('clinical.purpose.declare')}
        </Button>
      </div>
    </Card>
  );
}

/** Compact banner shown once a purpose is declared, with a "change" affordance. */
export function PurposeBanner({ purpose, onChange }: { purpose: PurposeOfUse; onChange: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex items-center gap-2 rounded-[var(--radius-sm)] bg-info-soft px-3 py-2 text-[12px] text-info">
      <Eye size={14} aria-hidden="true" />
      <span className="flex-1">{t('clinical.purpose.declared', { purpose: t(`clinical.purpose.${purpose}`) })}</span>
      <button type="button" onClick={onChange} className="font-medium underline hover:no-underline focus-visible:outline-none">
        {t('clinical.purpose.change')}
      </button>
    </div>
  );
}
