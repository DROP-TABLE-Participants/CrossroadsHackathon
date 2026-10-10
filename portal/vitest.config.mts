import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tsconfigPaths from 'vite-tsconfig-paths'

export default defineConfig({
  plugins: [tsconfigPaths(), react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./vitest.setup.ts'],
    include: ['tests/int/**/*.int.spec.ts'],
    // Integration files intentionally exercise process-wide environment,
    // module mocks, clocks, and transactional fakes. Running files in
    // parallel makes those shared boundaries nondeterministic under load.
    fileParallelism: false,
    maxWorkers: 1,
  },
})
