import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'
import process from 'node:process'

// https://vite.dev/config/
export default defineConfig(({ mode }) => ({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: process.env.VITE_API_PROXY_TARGET || loadEnv(mode, process.cwd(), '').VITE_API_PROXY_TARGET || 'http://localhost:5009',
        changeOrigin: true,
        secure: false,
      }
    }
  }
}))
