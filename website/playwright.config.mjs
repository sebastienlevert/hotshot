import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: 'list',
  use: {
    baseURL: process.env.HOTSHOT_TEST_URL ?? 'http://127.0.0.1:4173/hotshot/',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    browserName: 'chromium',
  },
  webServer: process.env.HOTSHOT_TEST_URL ? undefined : {
    command: 'node src/server.mjs',
    url: 'http://127.0.0.1:4173/hotshot/',
    reuseExistingServer: !process.env.CI,
    timeout: 30000,
  },
});
