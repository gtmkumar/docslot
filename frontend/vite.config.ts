import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import path from 'node:path';

// React Compiler is mandated by REACT_SKILL.md (auto-memoization).
// If the beta plugin ever blocks install/build, drop the babel block — the app
// runs without it; you just lose automatic memoization.
export default defineConfig({
  plugins: [
    react({
      babel: {
        plugins: [['babel-plugin-react-compiler', { target: '19' }]],
      },
    }),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, 'src'),
    },
  },
  build: {
    rollupOptions: {
      output: {
        // Split the large, slow-changing vendor libraries into their own chunks.
        // This keeps the app/main chunk small (no single >500kB chunk) and lets
        // the browser cache vendor code across app deploys. Route + panel chunks
        // are produced automatically by the React.lazy boundaries.
        // react/react-dom intentionally stay in the entry chunk (the React
        // Compiler runtime is referenced app-wide; splitting them produced an
        // empty chunk). The remaining heavy libs split cleanly.
        manualChunks: {
          tanstack: ['@tanstack/react-router', '@tanstack/react-query'],
          radix: ['@radix-ui/react-dialog', '@radix-ui/react-tabs', '@radix-ui/react-hover-card'],
          i18n: ['i18next', 'react-i18next'],
          forms: ['react-hook-form', 'zod'],
          icons: ['lucide-react'],
        },
      },
    },
  },
  // Dev: proxy /api → the .NET API (localhost:5054) so the SPA calls it
  // same-origin (api-client BASE_URL defaults to /api/v1). Avoids CORS entirely;
  // no backend change needed. Override the target with VITE_API_PROXY if the API
  // runs elsewhere.
  server: {
    port: 5173,
    // Fail loudly if 5173 is already taken instead of silently drifting to a new
    // port (5174, 5199, …). A drifted port leaves stale browser tabs pointed at a
    // dead server → "Failed to fetch dynamically imported module" on every lazy
    // route. strictPort forces a clean "port in use" error so you kill the old
    // server (or free the port) rather than chasing a phantom second instance.
    strictPort: true,
    proxy: {
      '/api': {
        target: process.env.VITE_API_PROXY ?? 'http://localhost:5054',
        changeOrigin: true,
      },
    },
  },
});
