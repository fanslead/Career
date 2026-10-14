import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const apiTarget = process.env.P0_API_PROXY_TARGET ?? 'http://127.0.0.1:5270';

export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    proxy: {
      '/health': apiTarget,
      '/api': apiTarget,
    },
  },
});
