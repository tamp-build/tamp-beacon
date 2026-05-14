import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

// During dev the SPA runs on :5173 and proxies anything that hits the
// ASP.NET Core backend on :8080. The build step emits to dist/, which
// Build.cs copies into src/Tamp.Beacon/wwwroot/ for the production bundle.
const BACKEND = 'http://localhost:8080';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, 'src'),
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': BACKEND,
      '/v1': BACKEND,
      '/healthz': BACKEND,
      '/readyz': BACKEND,
      '/setup': BACKEND,
      '/break-glass': BACKEND,
      '/logout': BACKEND,
      '/me': BACKEND,
      '/signin': BACKEND,
    },
  },
});
