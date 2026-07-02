// A chip picker over a static suggestion list + free text. Used by Diagnosis and
// Investigations. Selected values render as removable chips; typing filters the
// suggestions into a listbox; Enter (or clicking a suggestion) adds; free text adds
// on Enter when nothing matches. Tokens only; keyboard + screen-reader friendly.

import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';

const DEVA = /[ऀ-ॿ]/;

type Tone = 'accent' | 'info' | 'primary';
const CHIP_TONE: Record<Tone, string> = {
  accent: 'bg-accent-soft text-accent',
  info: 'bg-info-soft text-info',
  primary: 'bg-primary-soft text-primary',
};

export function ChipTypeahead({
  options,
  value,
  onChange,
  placeholder,
  tone = 'accent',
  disabled = false,
  allowFreeText = true,
}: {
  options: string[];
  value: string[];
  onChange: (next: string[]) => void;
  placeholder: string;
  tone?: Tone;
  disabled?: boolean;
  allowFreeText?: boolean;
}) {
  const { t } = useTranslation();
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const listId = useId();
  const inputRef = useRef<HTMLInputElement>(null);

  const q = query.trim().toLowerCase();
  const matches = options
    .filter((o) => !value.includes(o) && (q === '' || o.toLowerCase().includes(q)))
    .slice(0, 8);

  const add = (item: string) => {
    const v = item.trim();
    if (!v || value.includes(v)) return;
    onChange([...value, v]);
    setQuery('');
    setOpen(false);
    inputRef.current?.focus();
  };

  const remove = (item: string) => onChange(value.filter((x) => x !== item));

  return (
    <div className="flex flex-col gap-2">
      {value.length > 0 ? (
        <ul className="flex flex-wrap gap-1.5">
          {value.map((v) => (
            <li key={v}>
              <span className={`inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-[12px] font-medium ${CHIP_TONE[tone]} ${DEVA.test(v) ? 'deva' : ''}`}>
                {v}
                {!disabled ? (
                  <button
                    type="button"
                    onClick={() => remove(v)}
                    aria-label={t('consult.removeItem', { item: v })}
                    className="rounded-full p-0.5 hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
                  >
                    <X size={12} aria-hidden="true" />
                  </button>
                ) : null}
              </span>
            </li>
          ))}
        </ul>
      ) : null}

      {!disabled ? (
        <div className="relative">
          <input
            ref={inputRef}
            type="text"
            role="combobox"
            aria-expanded={open && matches.length > 0}
            aria-controls={listId}
            aria-autocomplete="list"
            value={query}
            placeholder={placeholder}
            onChange={(e) => {
              setQuery(e.target.value);
              setOpen(true);
            }}
            onFocus={() => setOpen(true)}
            onBlur={() => setTimeout(() => setOpen(false), 120)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                if (matches.length > 0) add(matches[0]);
                else if (allowFreeText) add(query);
              }
            }}
            className="h-9 w-full rounded-[var(--radius-sm)] border border-line bg-surface px-3 text-[13px] text-ink placeholder:text-muted-2 outline-none focus:border-primary focus:ring-2 focus:ring-primary-soft"
          />
          {open && matches.length > 0 ? (
            <ul
              id={listId}
              role="listbox"
              className="absolute z-10 mt-1 max-h-60 w-full overflow-auto rounded-[var(--radius-sm)] border border-line bg-surface py-1 shadow-[var(--shadow-lg)]"
            >
              {matches.map((o) => (
                <li key={o}>
                  <button
                    type="button"
                    role="option"
                    aria-selected={false}
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={() => add(o)}
                    className={`flex w-full items-center px-3 py-2 text-left text-[13px] text-ink hover:bg-surface-sunk focus-visible:bg-surface-sunk focus-visible:outline-none ${DEVA.test(o) ? 'deva' : ''}`}
                  >
                    {o}
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
