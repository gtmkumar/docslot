/// <reference types="vite/client" />

// Typed env vars consumed by the app (see lib/api-client.ts).
interface ImportMetaEnv {
  readonly VITE_API_BASE_URL?: string;
  readonly VITE_DEV_BEARER?: string;
  /**
   * Truthy → the app talks to the LIVE .NET API (via lib/backend). Unset/falsy →
   * the mock seam (default, unchanged). Toggle with `VITE_USE_REAL_API=1`.
   */
  readonly VITE_USE_REAL_API?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
