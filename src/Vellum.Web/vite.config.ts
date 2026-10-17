import { defineConfig, searchForWorkspaceRoot } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { TanStackRouterVite } from '@tanstack/router-plugin/vite'
import path from 'path'

export default defineConfig({
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: [],
  },
  plugins: [
    TanStackRouterVite({ routesDirectory: './src/routes' }),
    react(),
    tailwindcss(),
  ],
  server: {
    // The sample project (examples/orders) is bundled from the root of the repository.
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), path.resolve(__dirname, '../../examples')] },
    // VELLUM_API moves the proxy to an API on another port.
    proxy: {
      '/api': process.env.VELLUM_API ?? 'http://localhost:5195',
    },
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
      '@examples': path.resolve(__dirname, '../../examples'),
    },
  },
  build: {
    outDir: '../Vellum/wwwroot',
    emptyOutDir: true,
  },
})
