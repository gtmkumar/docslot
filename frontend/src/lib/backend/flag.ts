// Single source of truth for the live-vs-mock decision.
//
// VITE_USE_REAL_API truthy → the app talks to the LIVE .NET API (the real
// implementations in ./real). Unset/falsy → the mock seam (lib/mock), unchanged.
//
// Vite inlines import.meta.env at build time, so this constant is statically
// known per build; the default (flag off) keeps the byte-for-byte mock app.

const raw = import.meta.env.VITE_USE_REAL_API;

/** True when the live .NET API should be used instead of the mock seam. */
export const USE_REAL_API: boolean =
  raw === '1' || raw === 'true' || raw === 'yes' || raw === 'on';
