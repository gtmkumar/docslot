// SlideOver — the PRIMARY CRUD modality (REACT_SKILL pattern 1).
// Right-side Radix Dialog: focus-trapped, Esc + overlay close, returns focus to
// the trigger on close (Radix handles both), 420px desktop / full-width mobile.
// Animations reuse the global slideInRight / fadeIn keyframes and the
// `panel-closing` exit classes already shipped in global.css. Reduced-motion is
// honoured globally. URL-addressability is owned by SlideOverHost, which maps the
// `?panel=…` search param to this component's open state.

import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { useEffect, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { VisuallyHidden } from './VisuallyHidden';

interface SlideOverProps {
  open: boolean;
  onClose: () => void;
  /** Visible title (h2). Required for the accessible dialog name. */
  title: string;
  /**
   * Accessible description (announced by screen readers after the title). When
   * omitted we explicitly opt out (`aria-describedby={undefined}`) so Radix emits
   * no "missing Description" warning. Pass a string to add a visually-hidden one.
   */
  description?: string;
  /** Small uppercase eyebrow above the title (e.g. "New walk-in"). */
  eyebrow?: string;
  /** Header region between the title and the body — e.g. a stepper. */
  headerExtra?: ReactNode;
  /** Sticky footer — typically Cancel / Continue. */
  footer?: ReactNode;
  children: ReactNode;
}

const DESCRIPTION_ID = 'slideover-desc';

export function SlideOver({ open, onClose, title, description, eyebrow, headerExtra, footer, children }: SlideOverProps) {
  const { t } = useTranslation();
  // Panels open programmatically from the UI store (not via a Radix
  // Dialog.Trigger), so Radix has no trigger element to restore focus to on
  // close. `open` is a controlled prop, so Radix never fires onOpenChange(true)
  // for these opens — we capture the focused element via an effect when `open`
  // flips true, then restore it in onCloseAutoFocus (D6: focus returns to trigger).
  const triggerRef = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (open) triggerRef.current = document.activeElement as HTMLElement | null;
  }, [open]);

  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay
          className="fixed inset-0 z-40 bg-ink/30 backdrop-blur-[1px]"
          style={{ animation: 'fadeIn var(--dur-fast) ease' }}
        />
        <Dialog.Content
          aria-describedby={description ? DESCRIPTION_ID : undefined}
          onCloseAutoFocus={(e) => {
            // Restore focus to the element that opened the panel, if still in DOM.
            const trigger = triggerRef.current;
            if (trigger && document.contains(trigger)) {
              e.preventDefault();
              trigger.focus();
            }
          }}
          className="fixed inset-y-0 right-0 z-50 flex w-full max-w-[420px] flex-col border-l border-line bg-surface shadow-[var(--shadow-lg)] focus:outline-none"
          style={{ animation: 'slideInRight var(--dur-base) var(--motion)' }}
        >
          {description ? (
            <VisuallyHidden>
              <Dialog.Description id={DESCRIPTION_ID}>{description}</Dialog.Description>
            </VisuallyHidden>
          ) : null}
          <header className="flex items-start justify-between gap-4 border-b border-line px-5 py-4">
            <div className="min-w-0">
              {eyebrow ? (
                <p className="mb-0.5 text-[11px] font-semibold uppercase tracking-wider text-muted-2">{eyebrow}</p>
              ) : null}
              <Dialog.Title className="truncate text-base font-semibold text-ink">{title}</Dialog.Title>
            </div>
            <Dialog.Close
              aria-label={t('common.close')}
              className="rounded-[var(--radius-sm)] p-1 text-muted transition-colors hover:bg-surface-sunk hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
            >
              <X size={18} aria-hidden="true" />
            </Dialog.Close>
          </header>

          {headerExtra ? <div className="border-b border-line px-5 py-3">{headerExtra}</div> : null}

          <div className="flex-1 overflow-y-auto px-5 py-4">{children}</div>

          {footer ? (
            <footer className="flex items-center justify-end gap-2 border-t border-line bg-bg-2 px-5 py-3">
              {footer}
            </footer>
          ) : null}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
