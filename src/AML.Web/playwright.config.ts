import { defineConfig } from '@playwright/test';

// E2E tests expect the API on http://localhost:5080 (Development, demo data) — see README "End-to-end tests".
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  workers: 1,
  reporter: [['list'], ['json', { outputFile: 'e2e-results/results.json' }]],
  use: {
    baseURL: 'http://localhost:5173',
    launchOptions: { executablePath: process.env.CHROMIUM_PATH ?? '/opt/pw-browsers/chromium-1194/chrome-linux/chrome' },
    trace: 'retain-on-failure',
  },
  webServer: { command: 'npm run dev', url: 'http://localhost:5173', reuseExistingServer: true, timeout: 120_000 },
});
