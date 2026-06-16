// Empty state — illustration slot + one line + primary action (REACT_SKILL
// pattern 6). Every list in later waves reuses this.

import type { ReactNode } from 'react';
import { Button } from './Button';

interface EmptyStateProps {
  /** Illustration / icon slot. */
  icon?: ReactNode;
  title: string;
  description?: string;
  actionLabel?: string;
  onAction?: () => void;
}

export function EmptyState({ icon, title, description, actionLabel, onAction }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 px-6 py-12 text-center">
      {icon ? <div className="text-muted-2">{icon}</div> : null}
      <p className="text-sm font-medium text-ink">{title}</p>
      {description ? <p className="max-w-xs text-[13px] text-muted">{description}</p> : null}
      {actionLabel && onAction ? (
        <Button variant="primary" size="sm" onClick={onAction}>
          {actionLabel}
        </Button>
      ) : null}
    </div>
  );
}
