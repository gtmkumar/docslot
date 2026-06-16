// Token-driven badges for the security console: severity, generic status dot,
// and a "sensitive"/"irreversible" marker for dangerous actions. Always text
// (+ optional dot) — never color alone (a11y). Tokens only.

import { ShieldAlert } from 'lucide-react';

const TONES: Record<string, string> = {
  // severity
  low: 'bg-surface-sunk text-muted',
  medium: 'bg-warn-soft text-warn',
  high: 'bg-accent-soft text-accent',
  critical: 'bg-danger-soft text-danger',
  // generic states
  ok: 'bg-primary-soft text-primary',
  due_soon: 'bg-warn-soft text-warn',
  overdue: 'bg-danger-soft text-danger',
  reported: 'bg-primary-soft text-primary',
  resolved: 'bg-surface-sunk text-muted',
  open: 'bg-warn-soft text-warn',
  pending: 'bg-warn-soft text-warn',
  processing: 'bg-info-soft text-info',
  completed: 'bg-primary-soft text-primary',
  rejected: 'bg-surface-sunk text-muted',
};

const DOTS: Record<string, string> = {
  low: 'bg-muted-2',
  medium: 'bg-warn',
  high: 'bg-accent',
  critical: 'bg-danger',
  ok: 'bg-primary',
  due_soon: 'bg-warn',
  overdue: 'bg-danger',
  reported: 'bg-primary',
  resolved: 'bg-muted-2',
  open: 'bg-warn',
  pending: 'bg-warn',
  processing: 'bg-info',
  completed: 'bg-primary',
  rejected: 'bg-muted-2',
};

export function SecurityBadge({ tone, label, dot = true }: { tone: string; label: string; dot?: boolean }) {
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-medium ${TONES[tone] ?? TONES.low}`}>
      {dot ? <span className={`h-1.5 w-1.5 rounded-full ${DOTS[tone] ?? DOTS.low}`} aria-hidden="true" /> : null}
      {label}
    </span>
  );
}

/** Marks a destructive/irreversible action. `tone='danger'` for irreversible. */
export function SensitiveTag({ label, tone = 'warn' }: { label: string; tone?: 'warn' | 'danger' }) {
  const cls = tone === 'danger' ? 'bg-danger-soft text-danger' : 'bg-warn-soft text-warn';
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider ${cls}`}>
      <ShieldAlert size={11} aria-hidden="true" />
      {label}
    </span>
  );
}
