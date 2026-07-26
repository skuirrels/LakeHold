import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env['LAKEHOLD_E2E_BASE_URL'] ?? 'http://127.0.0.1:5399';

export default defineConfig({
  testDir: './e2e',
  outputDir: '../../output/playwright/test-results',
  fullyParallel: false,
  forbidOnly: Boolean(process.env['CI']),
  retries: process.env['CI'] ? 2 : 0,
  // Every journey talks to the same seeded demo catalog. Serial execution keeps backup,
  // maintenance, and query assertions deterministic instead of relying on the Duckling gate.
  workers: 1,
  reporter: [['list'], ['html', { outputFolder: '../../output/playwright/report', open: 'never' }]],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
