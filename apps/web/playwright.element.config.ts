import { defineConfig, devices } from '@playwright/test';

/**
 * E2E веб-компонента `<openbp-canvas>` (F4-13) на демо-сторінці `element-demo/`. Api підміняється в тестах
 * (`page.route`). Перед запуском збирається елемент: `npm run e2e:element`.
 */
const PORT = 4310;

export default defineConfig({
  testDir: './e2e-element',
  testMatch: '**/*.e2e.ts',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never', outputFolder: 'playwright-report-element' }]] : 'list',
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
    command: `node e2e-element/serve.mjs`,
    url: `http://localhost:${PORT}/element-demo/`,
    env: { PORT: String(PORT) },
    reuseExistingServer: !process.env['CI'],
    timeout: 30_000,
  },
});
