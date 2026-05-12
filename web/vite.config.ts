import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

// During dev, the SPA runs on :5173 and proxies API + OTLP calls to the .NET host on :4318.
// On `yarn build`, the dist/ folder is consumed by Build.cs and copied into wwwroot/.
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
      '/api': 'http://localhost:4318',
      '/v1': 'http://localhost:4318',
      '/healthz': 'http://localhost:4318',
    },
  },
});
