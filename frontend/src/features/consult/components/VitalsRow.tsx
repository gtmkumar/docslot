// Compact vitals row (BP / Pulse / Temp / SpO2 / Weight). Standard clinical PHI —
// editable by draft/create holders. Tokens only; each input labelled with its unit.

import { useTranslation } from 'react-i18next';
import type { VitalsForm } from '../model';

const CELL = 'flex flex-col gap-1';
const INPUT =
  'h-10 w-full rounded-[var(--radius-sm)] border border-line bg-surface px-2.5 text-[14px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft';
const LABEL = 'text-[10px] font-semibold uppercase tracking-wider text-muted-2';

export function VitalsRow({
  value,
  onChange,
  disabled = false,
}: {
  value: VitalsForm;
  onChange: (patch: Partial<VitalsForm>) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  const cells: { key: keyof VitalsForm; labelKey: string; unitKey: string; mode: 'text' | 'decimal' }[] = [
    { key: 'bp', labelKey: 'consult.vitals.bp', unitKey: 'consult.vitals.bpUnit', mode: 'text' },
    { key: 'pulse', labelKey: 'consult.vitals.pulse', unitKey: 'consult.vitals.pulseUnit', mode: 'decimal' },
    { key: 'temp', labelKey: 'consult.vitals.temp', unitKey: 'consult.vitals.tempUnit', mode: 'decimal' },
    { key: 'spo2', labelKey: 'consult.vitals.spo2', unitKey: 'consult.vitals.spo2Unit', mode: 'decimal' },
    { key: 'weight', labelKey: 'consult.vitals.weight', unitKey: 'consult.vitals.weightUnit', mode: 'decimal' },
  ];
  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
      {cells.map((c) => (
        <label key={c.key} className={CELL}>
          <span className={LABEL}>
            {t(c.labelKey)} <span className="text-muted-2/70 normal-case">{t(c.unitKey)}</span>
          </span>
          <input
            type="text"
            inputMode={c.mode}
            disabled={disabled}
            value={value[c.key]}
            onChange={(e) => onChange({ [c.key]: e.target.value } as Partial<VitalsForm>)}
            aria-label={`${t(c.labelKey)} ${t(c.unitKey)}`}
            className={`mono ${INPUT} disabled:cursor-not-allowed disabled:opacity-60`}
          />
        </label>
      ))}
    </div>
  );
}
