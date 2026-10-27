import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` proxies API calls to the local edge (docker compose), so the dev server
// behaves like the deployed SPA: same paths, same origin for the browser.
const edge = 'http://localhost:8080';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '^/(auth|destinations|files|jobs|config\\.js)': edge,
      '/ws': { target: edge.replace('http', 'ws'), ws: true },
    },
  },
  worker: { format: 'es' },
});
