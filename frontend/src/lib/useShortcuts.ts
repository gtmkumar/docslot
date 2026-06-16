// Keyboard shortcut registration (REACT_SKILL pattern 11). A small hook that
// binds key → handler, skipping keystrokes typed into form fields and ignoring
// modifier combos (those belong to the command palette / browser). Callers
// register the set relevant to their route, so shortcuts are scoped, not global
// by accident.

import { useEffect } from 'react';

export type ShortcutMap = Record<string, (e: KeyboardEvent) => void>;

function isTypingTarget(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false;
  const tag = el.tagName.toLowerCase();
  return tag === 'input' || tag === 'textarea' || tag === 'select' || el.isContentEditable;
}

export function useShortcuts(map: ShortcutMap, enabled = true): void {
  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      if (isTypingTarget(e.target)) return;
      const handler = map[e.key];
      if (handler) {
        e.preventDefault();
        handler(e);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [map, enabled]);
}
