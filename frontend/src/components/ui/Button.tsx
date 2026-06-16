// Button primitive. Variants map to design tokens — zero hex literals.
//  primary  → teal (--primary), the only filled CTA
//  ghost    → transparent, bordered, ink text
//  danger   → terracotta (--accent / danger) for destructive actions
//  subtle   → sunk surface, low-emphasis

import type { ButtonHTMLAttributes, ReactNode } from 'react';

export type ButtonVariant = 'primary' | 'ghost' | 'danger' | 'subtle';
export type ButtonSize = 'sm' | 'md';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  children: ReactNode;
}

const BASE =
  'inline-flex items-center justify-center gap-2 rounded-[var(--radius-sm)] font-medium ' +
  'transition-colors duration-[var(--dur-fast)] [transition-timing-function:var(--motion)] ' +
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 ' +
  'focus-visible:ring-offset-bg disabled:opacity-50 disabled:pointer-events-none select-none';

const VARIANTS: Record<ButtonVariant, string> = {
  primary: 'bg-primary text-bg hover:bg-primary-2',
  ghost: 'bg-transparent text-ink border border-line-strong hover:bg-surface-sunk',
  danger: 'bg-accent text-bg hover:opacity-90',
  subtle: 'bg-surface-sunk text-ink hover:bg-line',
};

const SIZES: Record<ButtonSize, string> = {
  sm: 'h-8 px-3 text-[13px]',
  md: 'h-10 px-4 text-sm',
};

export function Button({ variant = 'ghost', size = 'md', className = '', children, ...rest }: ButtonProps) {
  return (
    <button className={`${BASE} ${VARIANTS[variant]} ${SIZES[size]} ${className}`} {...rest}>
      {children}
    </button>
  );
}
