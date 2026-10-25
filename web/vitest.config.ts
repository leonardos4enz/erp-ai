import { defineConfig } from 'vitest/config';

// Spec 030 (H4): pruebas unitarias de la lógica de la SPA, sin DOM (npm test).
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
