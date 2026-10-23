import { defineConfig, devices } from '@playwright/test';

/**
 * E2E-сценарії SPA (Playwright). Api підміняється в самих тестах (`page.route`), тож потрібен лише dev-сервер.
 * Локально: `npm run e2e` (браузер — `npx playwright install chromium` або `PLAYWRIGHT_CHROMIUM_PATH`).
 */
const PORT = 4300;

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://localhost:${PORT}`,
    viewport: { width: 1440, height: 900 },
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
        launchOptions: process.env['PLAYWRIGHT_CHROMIUM_PATH'] ? { executablePath: process.env['PLAYWRIGHT_CHROMIUM_PATH'] } : {},
      },
    },
  ],
  webServer: {
    command: `npx ng serve --port ${PORT}`,
    url: `http://localhost:${PORT}`,
    reuseExistingServer: !process.env['CI'],
    timeout: 180_000,
  },
});
