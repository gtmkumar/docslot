// Card primitive. Near-flat (shadow-sm max) per design DNA — depth via border +
// surface, not elevation. 12px radius (--radius).
//
// `tone` owns the background/border/text so consumers never fight the base class:
//   surface  (default) → light card on cream
//   emphasis           → dark ink card with light text (the Live Queue card,
//                        prototype image.png). Using a tone (not an appended
//                        `bg-ink`) avoids the Tailwind v4 source-order conflict
//                        where a hardcoded `bg-surface` would win and render the
//                        dark card invisible in both light and dark themes.

import type { HTMLAttributes, ReactNode } from 'react';

export type CardTone = 'surface' | 'emphasis';

interface CardProps extends HTMLAttributes<HTMLDivElement> {
  tone?: CardTone;
  children: ReactNode;
}

const TONES: Record<CardTone, string> = {
  surface: 'border-line bg-surface text-ink',
  // ink-2 border keeps a visible edge in dark theme where bg-ink ≈ page bg.
  emphasis: 'border-ink-2 bg-ink text-bg',
};

export function Card({ tone = 'surface', className = '', children, ...rest }: CardProps) {
  return (
    <div
      className={`rounded-[var(--radius)] border shadow-[var(--shadow-sm)] ${TONES[tone]} ${className}`}
      {...rest}
    >
      {children}
    </div>
  );
}
