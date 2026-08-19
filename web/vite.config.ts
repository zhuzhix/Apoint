import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5222',
      '/hubs': {
        target: 'http://127.0.0.1:5222',
        ws: true,
      },
    },
  },
  build: {
    outDir: '../src/AStockMonitor.Api/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
  },
})
