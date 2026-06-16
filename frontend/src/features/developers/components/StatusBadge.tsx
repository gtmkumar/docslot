// Token-driven status badge for the developer portal (client status, webhook
// active state, delivery status). Always text (+ a colored dot) — never color
// alone (a11y). Color via token classes only.

const TONES: Record<string, string> = {
  approved: 'bg-primary-soft text-primary',
  active: 'bg-primary-soft text-primary',
  success: 'bg-primary-soft text-primary',
  pending: 'bg-warn-soft text-warn',
  processing: 'bg-info-soft text-info',
  suspended: 'bg-surface-sunk text-muted',
  inactive: 'bg-surface-sunk text-muted',
  failed: 'bg-danger-soft text-danger',
  abandoned: 'bg-danger-soft text-danger',
};

const DOTS: Record<string, string> = {
  approved: 'bg-primary',
  active: 'bg-primary',
  success: 'bg-primary',
  pending: 'bg-warn',
  processing: 'bg-info',
  suspended: 'bg-muted-2',
  inactive: 'bg-muted-2',
  failed: 'bg-danger',
  abandoned: 'bg-danger',
};

export function StatusBadge({ tone, label }: { tone: string; label: string }) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-medium ${TONES[tone] ?? TONES.inactive}`}
    >
      <span className={`h-1.5 w-1.5 rounded-full ${DOTS[tone] ?? DOTS.inactive}`} aria-hidden="true" />
      {label}
    </span>
  );
}
