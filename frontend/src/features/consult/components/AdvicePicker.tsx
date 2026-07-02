// Advice — quick-add chips + removable selected chips + a free-text area (the
// doctor can type anything, incl. Hindi, rendered with the Devanagari font). All
// lines are stored newline-joined in the draft's `advice`.

import { useTranslation } from 'react-i18next';
import { Plus, X } from 'lucide-react';
import { TextArea } from '@/components/ui/Field';
import { ADVICE_CHIPS } from '../constants';

const DEVA = /[ऀ-ॿ]/;

export function AdvicePicker({
  chips,
  text,
  onChangeChips,
  onChangeText,
  disabled = false,
}: {
  chips: string[];
  text: string;
  onChangeChips: (next: string[]) => void;
  onChangeText: (next: string) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  const add = (item: string) => {
    if (chips.includes(item)) return;
    onChangeChips([...chips, item]);
  };
  const remove = (item: string) => onChangeChips(chips.filter((c) => c !== item));
  const quick = ADVICE_CHIPS.filter((a) => !chips.includes(a));

  return (
    <div className="flex flex-col gap-2.5">
      {chips.length > 0 ? (
        <ul className="flex flex-wrap gap-1.5">
          {chips.map((c) => (
            <li key={c}>
              <span className={`inline-flex items-center gap-1 rounded-full bg-primary-soft px-2.5 py-1 text-[12px] font-medium text-primary ${DEVA.test(c) ? 'deva' : ''}`}>
                {c}
                {!disabled ? (
                  <button
                    type="button"
                    onClick={() => remove(c)}
                    aria-label={t('consult.removeItem', { item: c })}
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
        <>
          <TextArea
            rows={2}
            value={text}
            onChange={(e) => onChangeText(e.target.value)}
            placeholder={t('consult.advice.placeholder')}
            aria-label={t('consult.advice.freeText')}
            className={DEVA.test(text) ? 'deva' : ''}
          />
          {quick.length > 0 ? (
            <ul className="flex flex-wrap gap-1.5">
              {quick.map((a) => (
                <li key={a}>
                  <button
                    type="button"
                    onClick={() => add(a)}
                    className={`inline-flex items-center gap-1 rounded-full border border-line bg-surface px-2.5 py-1 text-[12px] text-muted transition-colors hover:border-primary-soft hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ${DEVA.test(a) ? 'deva' : ''}`}
                  >
                    <Plus size={11} aria-hidden="true" />
                    {a}
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </>
      ) : null}
    </div>
  );
}
