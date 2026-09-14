import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],

  // Relative asset paths: the same build is served by Vite in dev, by
  // ConnectionDoctor on Windows and macOS. Absolute "/asset"
  // URLs would tie the bundle to being mounted at the server root.
  base: './',
})
