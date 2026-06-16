// Avatar — initials chip with a token-driven tint. Color comes from a small
// fixed palette of token classes selected deterministically from the name, so
// avatars are stable and never use hex.

import { initials as toInitials } from '@/lib/format';

const TINTS = [
  'bg-primary-soft text-primary',
  'bg-accent-soft text-accent',
  'bg-info-soft text-info',
  'bg-warn-soft text-warn',
] as const;

const SIZES = {
  sm: 'h-8 w-8 text-[11px]',
  md: 'h-10 w-10 text-[13px]',
  lg: 'h-12 w-12 text-sm',
} as const;

export function Avatar({
  name,
  initials,
  size = 'md',
}: {
  name: string;
  /** Explicit initials override (e.g. prototype's two-letter codes). */
  initials?: string;
  size?: keyof typeof SIZES;
}) {
  const tint = TINTS[hash(name) % TINTS.length];
  const label = initials ?? toInitials(name);
  return (
    <span
      aria-hidden="true"
      className={`flex shrink-0 items-center justify-center rounded-full font-semibold ${tint} ${SIZES[size]}`}
    >
      {label}
    </span>
  );
}

function hash(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
  return h;
}
