// Medications editor — the composer's core. A formulary search adds a COMPLETE line
// in one click (strength/form/default dose/timing/duration); each line then exposes
// morning-noon-night steppers, SOS + weekly toggles, a food-timing select, a
// duration select and delete. Free-text drugs (not in the formulary) add a sensible
// blank line. Tokens only; React Compiler on (no manual memo).

import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Plus, Search, Trash2 } from 'lucide-react';
import { Select } from '@/components/ui/Field';
import type { MedTiming, StructuredMedication } from '@/lib/mock/contracts';
import { FORMULARY, blankMedication, fromFormulary } from '../constants';

const TIMINGS: MedTiming[] = ['after_food', 'before_food', 'empty_stomach', 'anytime'];
const DURATIONS = [3, 5, 7, 10, 14, 21, 30, 56, 90];

export function MedicationsEditor({
  value,
  onChange,
  disabled = false,
}: {
  value: StructuredMedication[];
  onChange: (next: StructuredMedication[]) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const listId = useId();
  const inputRef = useRef<HTMLInputElement>(null);

  const q = query.trim().toLowerCase();
  const matches = q === '' ? FORMULARY : FORMULARY.filter((f) => f.name.toLowerCase().includes(q) || f.generic.toLowerCase().includes(q));

  const addItem = (med: StructuredMedication) => {
    onChange([...value, med]);
    setQuery('');
    setOpen(false);
    inputRef.current?.focus();
  };

  const patch = (i: number, p: Partial<StructuredMedication>) => onChange(value.map((m, idx) => (idx === i ? { ...m, ...p } : m)));
  const patchDose = (i: number, key: 'morning' | 'noon' | 'night') =>
    onChange(value.map((m, idx) => (idx === i ? { ...m, dose: { ...m.dose, [key]: (m.dose[key] + 1) % 4 } } : m)));
  const remove = (i: number) => onChange(value.filter((_, idx) => idx !== i));

  return (
    <div className="flex flex-col gap-3">
      {/* Formulary search */}
      {!disabled ? (
        <div className="relative">
          <Search size={15} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-2" aria-hidden="true" />
          <input
            ref={inputRef}
            type="text"
            role="combobox"
            aria-expanded={open && matches.length > 0}
            aria-controls={listId}
            aria-autocomplete="list"
            value={query}
            placeholder={t('consult.med.searchPlaceholder')}
            onChange={(e) => {
              setQuery(e.target.value);
              setOpen(true);
            }}
            onFocus={() => setOpen(true)}
            onBlur={() => setTimeout(() => setOpen(false), 150)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && query.trim()) {
                e.preventDefault();
                if (matches.length > 0) addItem(fromFormulary(matches[0]));
                else addItem(blankMedication(query.trim()));
              }
            }}
            className="h-10 w-full rounded-[var(--radius-sm)] border border-line bg-surface pl-9 pr-3 text-[13px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
          />
          {open && matches.length > 0 ? (
            <ul id={listId} role="listbox" className="absolute z-10 mt-1 max-h-72 w-full overflow-auto rounded-[var(--radius-sm)] border border-line bg-surface py-1 shadow-[var(--shadow-lg)]">
              {matches.map((f) => (
                <li key={f.id}>
                  <button
                    type="button"
                    role="option"
                    aria-selected={false}
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={() => addItem(fromFormulary(f))}
                    className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-surface-sunk focus-visible:bg-surface-sunk focus-visible:outline-none"
                  >
                    <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-[var(--radius-sm)] bg-primary-soft text-[11px] font-semibold text-primary">℞</span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[13px] font-medium text-ink">
                        {f.name} <span className="font-normal text-muted">· {f.generic}</span>
                      </span>
                      <span className="mono block truncate text-[11px] text-muted-2">
                        {f.strength} {f.form} · {f.sos ? t('consult.dose.sos') : f.weekly ? t('consult.dose.weekly') : `${f.dose.morning}-${f.dose.noon}-${f.dose.night}`} · {t(`consult.timing.${f.timing}`)}
                        {f.durationDays ? ` · ${t('consult.durationValue', { count: f.durationDays })}` : ''}
                      </span>
                    </span>
                    <Plus size={16} className="shrink-0 text-primary" aria-hidden="true" />
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}

      {/* Lines */}
      {value.length === 0 ? (
        <p className="rounded-[var(--radius-sm)] border border-dashed border-line px-3 py-6 text-center text-[13px] text-muted-2">
          {t('consult.med.none')}
        </p>
      ) : (
        <ol className="flex flex-col gap-2.5">
          {value.map((m, i) => (
            <li key={i} className="rounded-[var(--radius)] border border-line bg-surface p-3">
              <div className="flex items-start gap-2">
                <span className="mono mt-2 shrink-0 text-[12px] font-semibold text-muted-2">{i + 1}.</span>
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <input
                      type="text"
                      value={m.name}
                      disabled={disabled}
                      onChange={(e) => patch(i, { name: e.target.value })}
                      aria-label={t('consult.med.name')}
                      className="h-9 min-w-0 flex-1 rounded-[var(--radius-sm)] border border-line bg-surface px-2.5 text-[13px] font-medium text-ink outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft disabled:opacity-60"
                    />
                    <input
                      type="text"
                      value={m.strength ?? ''}
                      disabled={disabled}
                      onChange={(e) => patch(i, { strength: e.target.value || null })}
                      placeholder={t('consult.med.strength')}
                      aria-label={t('consult.med.strength')}
                      className="mono h-9 w-24 shrink-0 rounded-[var(--radius-sm)] border border-line bg-surface px-2 text-[12px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft disabled:opacity-60"
                    />
                    {!disabled ? (
                      <button
                        type="button"
                        onClick={() => remove(i)}
                        aria-label={t('consult.med.remove', { name: m.name })}
                        className="shrink-0 rounded-[var(--radius-sm)] p-2 text-muted-2 transition-colors hover:bg-danger-soft hover:text-danger focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                      >
                        <Trash2 size={15} aria-hidden="true" />
                      </button>
                    ) : null}
                  </div>

                  <div className="mt-2 flex flex-wrap items-center gap-2">
                    {/* Morning-noon-night steppers */}
                    <div className="inline-flex overflow-hidden rounded-[var(--radius-sm)] border border-line" role="group" aria-label={t('consult.med.dose')}>
                      {(['morning', 'noon', 'night'] as const).map((slot) => {
                        const n = m.dose[slot];
                        const activeSlot = n > 0 && !m.sos && !m.weekly;
                        return (
                          <button
                            key={slot}
                            type="button"
                            disabled={disabled || m.sos}
                            onClick={() => patchDose(i, slot)}
                            aria-label={`${t(`consult.med.${slot}`)}: ${n}`}
                            className={`flex w-12 flex-col items-center border-r border-line py-1 text-center transition-colors last:border-r-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-primary disabled:opacity-50 ${activeSlot ? 'bg-primary text-bg' : 'bg-surface text-ink hover:bg-surface-sunk'}`}
                          >
                            <span className="mono text-[13px] font-semibold leading-none">{n}</span>
                            <span className="mt-0.5 text-[9px] uppercase tracking-wide opacity-80">{t(`consult.med.${slot}Short`)}</span>
                          </button>
                        );
                      })}
                    </div>

                    <ToggleChip active={m.sos} disabled={disabled} onClick={() => patch(i, { sos: !m.sos })} label={t('consult.dose.sos')} tone="accent" />
                    <ToggleChip active={m.weekly} disabled={disabled} onClick={() => patch(i, { weekly: !m.weekly })} label={t('consult.dose.weekly')} tone="info" />

                    <Select
                      value={m.timing}
                      disabled={disabled}
                      onChange={(e) => patch(i, { timing: e.target.value as MedTiming })}
                      aria-label={t('consult.med.timing')}
                      className="!h-9 !w-auto !py-0 text-[12px]"
                    >
                      {TIMINGS.map((tm) => (
                        <option key={tm} value={tm}>
                          {t(`consult.timing.${tm}`)}
                        </option>
                      ))}
                    </Select>

                    <Select
                      value={m.durationDays ?? ''}
                      disabled={disabled}
                      onChange={(e) => patch(i, { durationDays: e.target.value ? Number(e.target.value) : null })}
                      aria-label={t('consult.med.duration')}
                      className="!h-9 !w-auto !py-0 text-[12px]"
                    >
                      <option value="">{t('consult.med.noDuration')}</option>
                      {(m.durationDays != null && !DURATIONS.includes(m.durationDays) ? [m.durationDays, ...DURATIONS] : DURATIONS).map((d) => (
                        <option key={d} value={d}>
                          {t('consult.durationValue', { count: d })}
                        </option>
                      ))}
                    </Select>
                  </div>

                  {m.sos ? (
                    <input
                      type="text"
                      value={m.instructions ?? ''}
                      disabled={disabled}
                      onChange={(e) => patch(i, { instructions: e.target.value || null })}
                      placeholder={t('consult.med.instructionsPlaceholder')}
                      aria-label={t('consult.med.instructions')}
                      className="mt-2 h-9 w-full rounded-[var(--radius-sm)] border border-line bg-surface px-2.5 text-[12px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft disabled:opacity-60"
                    />
                  ) : null}
                </div>
              </div>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function ToggleChip({ active, onClick, label, tone, disabled }: { active: boolean; onClick: () => void; label: string; tone: 'accent' | 'info'; disabled?: boolean }) {
  const activeCls = tone === 'accent' ? 'border-accent bg-accent-soft text-accent' : 'border-info bg-info-soft text-info';
  return (
    <button
      type="button"
      aria-pressed={active}
      disabled={disabled}
      onClick={onClick}
      className={`inline-flex h-9 items-center rounded-[var(--radius-sm)] border px-2.5 text-[12px] font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:opacity-50 ${active ? activeCls : 'border-line bg-surface text-muted hover:text-ink'}`}
    >
      {label}
    </button>
  );
}
