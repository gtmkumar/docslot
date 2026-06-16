// Token-driven badges for the commission console: tier, status, verification,
// and the enforced PCPNDT exclusion. Always text (+ optional dot), never color
// alone (a11y). Tokens only.

import { ShieldCheck } from 'lucide-react';

const TONES: Record<string, string> = {
  // tier
  basic: 'bg-surface-sunk text-muted',
  silver: 'bg-info-soft text-info',
  gold: 'bg-warn-soft text-warn',
  platinum: 'bg-primary-soft text-primary',
  // status / state
  active: 'bg-primary-soft text-primary',
  inactive: 'bg-surface-sunk text-muted',
  blacklisted: 'bg-danger-soft text-danger',
  ok: 'bg-primary-soft text-primary',
  pending: 'bg-warn-soft text-warn',
  paid: 'bg-primary-soft text-primary',
  approved: 'bg-info-soft text-info',
  processing: 'bg-info-soft text-info',
  failed: 'bg-danger-soft text-danger',
  on_hold: 'bg-warn-soft text-warn',
  reversed: 'bg-danger-soft text-danger',
  denied: 'bg-danger-soft text-danger',
  flagged: 'bg-danger-soft text-danger',
};

export function CommissionBadge({ tone, label, dot = true }: { tone: string; label: string; dot?: boolean }) {
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-medium ${TONES[tone] ?? TONES.inactive}`}>
      {dot ? <span className={`h-1.5 w-1.5 rounded-full ${dotFor(tone)}`} aria-hidden="true" /> : null}
      {label}
    </span>
  );
}

function dotFor(tone: string): string {
  const t = TONES[tone] ?? TONES.inactive;
  if (t.includes('text-primary')) return 'bg-primary';
  if (t.includes('text-danger')) return 'bg-danger';
  if (t.includes('text-warn')) return 'bg-warn';
  if (t.includes('text-info')) return 'bg-info';
  return 'bg-muted-2';
}

/** Enforced PCPNDT-exclusion badge — shown as a guarantee, never a toggle. */
export function PndtBadge({ label }: { label: string }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-primary-soft px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider text-primary">
      <ShieldCheck size={11} aria-hidden="true" />
      {label}
    </span>
  );
}
