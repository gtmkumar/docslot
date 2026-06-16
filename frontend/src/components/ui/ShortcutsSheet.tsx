// Keyboard shortcuts cheatsheet (REACT_SKILL pattern 11). Opened with `?`.
// Rendered as a right-side sheet (consistent with the slide-over modality) so it
// is focus-trapped and Esc-dismissable via Radix Dialog. Lists global + queue
// shortcuts, all bilingual.

import { SlideOver } from './SlideOver';
import { useTranslation } from 'react-i18next';

interface Row {
  keys: string[];
  labelKey: string;
}

const GLOBAL: Row[] = [
  { keys: ['⌘', 'K'], labelKey: 'shortcuts.openPalette' },
  { keys: ['?'], labelKey: 'shortcuts.openCheatsheet' },
  { keys: ['N'], labelKey: 'shortcuts.newWalkIn' },
  { keys: ['Esc'], labelKey: 'shortcuts.close' },
];

const QUEUE: Row[] = [
  { keys: ['J', 'K'], labelKey: 'shortcuts.navRows' },
  { keys: ['Enter'], labelKey: 'shortcuts.openManage' },
];

export function ShortcutsSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <SlideOver open={open} onClose={onClose} title={t('shortcuts.title')}>
      <div className="flex flex-col gap-5">
        <Section title={t('shortcuts.sectionGlobal')} rows={GLOBAL} />
        <Section title={t('shortcuts.sectionQueue')} rows={QUEUE} />
      </div>
    </SlideOver>
  );
}

function Section({ title, rows }: { title: string; rows: Row[] }) {
  const { t } = useTranslation();
  return (
    <section>
      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{title}</h3>
      <ul className="flex flex-col gap-1.5">
        {rows.map((r) => (
          <li key={r.labelKey} className="flex items-center justify-between gap-3">
            <span className="text-[13px] text-ink">{t(r.labelKey)}</span>
            <span className="flex items-center gap-1">
              {r.keys.map((k) => (
                <kbd
                  key={k}
                  className="mono rounded border border-line bg-surface-sunk px-1.5 py-0.5 text-[11px] text-muted"
                >
                  {k}
                </kbd>
              ))}
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
