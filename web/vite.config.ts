import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// Dev: Vite on :5173 proxies /api to the ASP.NET Core API on :5180.
// Prod: `npm run build` emits into ../src/SecureFlow.Api/wwwroot so one `dotnet run` serves everything.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5180', changeOrigin: true },
    },
  },
  build: {
    outDir: '../src/SecureFlow.Api/wwwroot',
    emptyOutDir: true,
  },
})
