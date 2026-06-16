// Appearance control (D3): theme (light/dark) + density (comfortable/compact)
// toggles, wired to the existing useUI store actions (setTheme/setDensity). A
// disclosure popover keeps the topbar uncluttered; it closes on outside click +
// Esc and is keyboard reachable. Tokens only.

import { useEffect, useRef, useState } from 'react';
import { Monitor, Moon, Sun } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useUI, type Density, type Theme } from '@/stores/ui';

export function AppearanceMenu() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  const theme = useUI((s) => s.theme);
  const density = useUI((s) => s.density);
  const setTheme = useUI((s) => s.setTheme);
  const setDensity = useUI((s) => s.setDensity);

  // Close on outside click + Esc.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={t('theme.appearance')}
        onClick={() => setOpen((o) => !o)}
        className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] border border-line-strong text-ink transition-colors hover:bg-surface-sunk focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
      >
        {theme === 'dark' ? <Moon size={15} aria-hidden="true" /> : <Sun size={15} aria-hidden="true" />}
      </button>

      {open ? (
        <div
          role="menu"
          aria-label={t('theme.appearance')}
          className="absolute right-0 z-50 mt-2 w-56 rounded-[var(--radius)] border border-line bg-surface p-3 shadow-[var(--shadow-lg)]"
        >
          <fieldset className="mb-3">
            <legend className="mb-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              {t('theme.label')}
            </legend>
            <div className="grid grid-cols-2 gap-1.5">
              <Segment active={theme === 'light'} onClick={() => setTheme('light' as Theme)} icon={<Sun size={14} />} label={t('theme.light')} />
              <Segment active={theme === 'dark'} onClick={() => setTheme('dark' as Theme)} icon={<Moon size={14} />} label={t('theme.dark')} />
            </div>
          </fieldset>

          <fieldset>
            <legend className="mb-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">
              {t('theme.density')}
            </legend>
            <div className="grid grid-cols-2 gap-1.5">
              <Segment active={density === 'comfortable'} onClick={() => setDensity('comfortable' as Density)} icon={<Monitor size={14} />} label={t('theme.comfortable')} />
              <Segment active={density === 'compact'} onClick={() => setDensity('compact' as Density)} icon={<Monitor size={14} />} label={t('theme.compact')} />
            </div>
          </fieldset>
        </div>
      ) : null}
    </div>
  );
}

function Segment({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      role="menuitemradio"
      aria-checked={active}
      onClick={onClick}
      className={[
        'flex items-center justify-center gap-1.5 rounded-[var(--radius-sm)] border px-2 py-1.5 text-[12px] transition-colors',
        active ? 'border-primary bg-primary-soft text-primary' : 'border-line text-ink hover:bg-surface-sunk',
      ].join(' ')}
    >
      {icon}
      {label}
    </button>
  );
}
