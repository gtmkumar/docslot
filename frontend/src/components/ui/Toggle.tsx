// Accessible on/off switch (Radix isn't shipped for switches here, so this is a
// hand-rolled role="switch"). Token-only styles, keyboard-operable as a native
// button, reduced-motion respected globally (transitions are killed under
// prefers-reduced-motion in global.css). Zero hex.
//
// Pair it with a visible <label> via `id` (label htmlFor=id) OR pass `label` for an
// aria-label. Use `describedBy` to point at helper/warning text.

export function Toggle({
  checked,
  onChange,
  disabled,
  id,
  label,
  describedBy,
}: {
  checked: boolean;
  onChange: (next: boolean) => void;
  disabled?: boolean;
  id?: string;
  label?: string;
  describedBy?: string;
}) {
  return (
    <button
      type="button"
      role="switch"
      id={id}
      aria-checked={checked}
      aria-label={label}
      aria-describedby={describedBy}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={[
        'relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border transition-colors',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary',
        'disabled:cursor-not-allowed disabled:opacity-50',
        checked ? 'border-primary bg-primary' : 'border-line bg-surface-sunk',
      ].join(' ')}
    >
      <span
        aria-hidden="true"
        className={[
          'inline-block h-3.5 w-3.5 rounded-full bg-surface shadow-sm transition-transform',
          checked ? 'translate-x-4' : 'translate-x-0.5',
        ].join(' ')}
      />
    </button>
  );
}
