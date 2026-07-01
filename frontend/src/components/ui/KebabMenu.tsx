// Kebab (overflow) menu — a small accessible action menu for table rows. Follows
// the same disclosure pattern as AppearanceMenu (no new dependency: the repo ships
// Radix dialog/hover-card/tabs only, not dropdown-menu). Tokens only.
//
// A11y: trigger is aria-haspopup=menu with aria-expanded; the popover is role=menu
// with role=menuitem children. Opening focuses the first item; ArrowUp/ArrowDown
// cycle focus; Esc closes and returns focus to the trigger; outside-click / Tab
// close without stealing focus. Selecting an item closes the menu and runs its
// handler (which typically opens a focus-trapped slide-over that then owns focus).

import { useEffect, useRef, useState } from 'react';
import type { KeyboardEvent, ReactNode } from 'react';
import { EllipsisVertical } from 'lucide-react';

export interface KebabItem {
  key: string;
  label: string;
  icon?: ReactNode;
  onSelect: () => void;
  tone?: 'default' | 'danger';
}

export function KebabMenu({ label, items }: { label: string; items: KebabItem[] }) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  // Query the live DOM for menu items (avoids stale ref arrays across re-renders).
  const itemEls = () =>
    Array.from(rootRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? []);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    itemEls()[0]?.focus();
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  const close = (returnFocus: boolean) => {
    setOpen(false);
    if (returnFocus) triggerRef.current?.focus();
  };

  const move = (dir: 1 | -1) => {
    const els = itemEls();
    if (els.length === 0) return;
    const idx = els.findIndex((el) => el === document.activeElement);
    const next = (idx + dir + els.length) % els.length;
    els[next]?.focus();
  };

  const onMenuKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      close(true);
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      move(1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      move(-1);
    } else if (e.key === 'Tab') {
      close(false);
    }
  };

  return (
    <div ref={rootRef} className="relative shrink-0">
      <button
        ref={triggerRef}
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={label}
        onClick={(e) => {
          e.stopPropagation();
          setOpen((o) => !o);
        }}
        className="flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] border border-transparent text-muted-2 transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
      >
        <EllipsisVertical size={16} aria-hidden="true" />
      </button>

      {open ? (
        <div
          role="menu"
          aria-label={label}
          onKeyDown={onMenuKeyDown}
          className="absolute right-0 z-50 mt-1 min-w-44 rounded-[var(--radius)] border border-line bg-surface p-1 shadow-[var(--shadow-lg)]"
        >
          {items.map((item) => (
            <button
              key={item.key}
              type="button"
              role="menuitem"
              onClick={() => {
                close(false);
                item.onSelect();
              }}
              className={[
                'flex w-full items-center gap-2 rounded-[var(--radius-sm)] px-2.5 py-2 text-left text-[13px] transition-colors',
                'outline-none hover:bg-surface-sunk focus:bg-surface-sunk',
                item.tone === 'danger' ? 'text-danger' : 'text-ink',
              ].join(' ')}
            >
              {item.icon ? (
                <span aria-hidden="true" className="shrink-0">
                  {item.icon}
                </span>
              ) : null}
              <span className="min-w-0 flex-1 truncate">{item.label}</span>
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
