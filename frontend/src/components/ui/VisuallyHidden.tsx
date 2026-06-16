// VisuallyHidden — content available to screen readers but visually hidden.
// Used for accessible dialog titles that aren't shown in the design (e.g. the
// command palette, whose "title" is its input placeholder).

import type { ReactNode } from 'react';

export function VisuallyHidden({ children }: { children: ReactNode }) {
  return <span className="sr-only">{children}</span>;
}
