import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Бэкенд (.NET) в dev слушает http://localhost:5148 (см. Properties/launchSettings.json).
// Vite-dev отдаёт SPA на 5173 и проксирует серверные роуты на .NET, чтобы httpOnly-cookie
// (bark_at/bark_rt) ходили как same-origin (changeOrigin:false — сохраняем домен cookie).
const BACKEND = 'http://localhost:5148';
const backendRoutes = ['/api', '/login', '/register', '/forgot', '/logout', '/healthz'];

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: Object.fromEntries(
      backendRoutes.map((p) => [p, { target: BACKEND, changeOrigin: false }]),
    ),
  },
  build: {
    // Собранный бандл кладём в wwwroot — Microsoft.NET.Sdk.Web публикует его автоматически.
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
});
