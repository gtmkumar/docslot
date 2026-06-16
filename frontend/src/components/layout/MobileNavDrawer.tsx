// Off-canvas mobile nav drawer (<md). Radix Dialog gives focus-trap, Esc, and
// overlay-click close for free (the previous hand-rolled div had none). Reuses
// the SAME backend-driven <Sidebar> — no duplicate nav logic. The slide-in uses
// the shared slideInLeft keyframe; reduced-motion is honoured globally.

import * as Dialog from '@radix-ui/react-dialog';
import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { VisuallyHidden } from '@/components/ui/VisuallyHidden';
import { Sidebar } from './Sidebar';

export function MobileNavDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  // Opened from the topbar hamburger (not a Radix Trigger). `open` is controlled,
  // so capture the focused element via an effect and restore it on close (D6).
  const triggerRef = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (open) triggerRef.current = document.activeElement as HTMLElement | null;
  }, [open]);

  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay
          className="fixed inset-0 z-50 bg-ink/30 backdrop-blur-[1px] md:hidden"
          style={{ animation: 'fadeIn var(--dur-fast) ease' }}
        />
        <Dialog.Content
          aria-describedby={undefined}
          onCloseAutoFocus={(e) => {
            const trigger = triggerRef.current;
            if (trigger && document.contains(trigger)) {
              e.preventDefault();
              trigger.focus();
            }
          }}
          className="fixed inset-y-0 left-0 z-50 w-[80vw] max-w-[18rem] focus:outline-none md:hidden"
          style={{ animation: 'slideInLeft var(--dur-base) var(--motion)' }}
        >
          {/* Accessible name for the dialog; the visible brand sits inside Sidebar. */}
          <VisuallyHidden>
            <Dialog.Title>{t('app.workspace')}</Dialog.Title>
          </VisuallyHidden>
          {/* Same nav as the desktop rail; onNavigate closes the drawer on route change. */}
          <Sidebar onNavigate={onClose} />
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
