// ProgressBar — token-colored track + fill. `colorKey` maps to a token class so
// callers (department load, agent funnel) never pass a hex. Accessible via
// role="progressbar" with value semantics.

const FILL: Record<string, string> = {
  primary: 'bg-primary',
  accent: 'bg-accent',
  info: 'bg-info',
  warn: 'bg-warn',
  muted: 'bg-muted',
  whatsapp: 'bg-whatsapp',
};

export function ProgressBar({
  value,
  max = 100,
  colorKey = 'primary',
  label,
  className = '',
}: {
  value: number;
  max?: number;
  colorKey?: string;
  /** Accessible label describing what is progressing. */
  label?: string;
  className?: string;
}) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0;
  return (
    <div
      role="progressbar"
      aria-valuenow={value}
      aria-valuemin={0}
      aria-valuemax={max}
      aria-label={label}
      className={`h-1.5 w-full overflow-hidden rounded-full bg-surface-sunk ${className}`}
    >
      <div
        className={`h-full rounded-full ${FILL[colorKey] ?? FILL.primary} transition-[width] duration-[var(--dur-base)] [transition-timing-function:var(--motion)]`}
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}
