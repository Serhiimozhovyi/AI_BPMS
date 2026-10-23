import { randomBytes } from 'node:crypto';
import { defineConfig, devices } from '@playwright/test';
import { E2E_ADMIN } from './e2e-live/env';

/**
 * Живі E2E (F2-20): SPA (dev-сервер з proxy.conf.json) проти справжніх OpenBP.Api + OpenBP.Worker і FakeServer-моків
 * 1С/Telegram. Потрібні зібране рішення (`dotnet build`) і Postgres (рядок підключення — `ConnectionStrings__Default`,
 * за замовчуванням локальний `scripts/dev.sh up`). Запуск: `npm run e2e:live`.
 */
const PORT = 4301;

const backendEnv: Record<string, string> = {
  ConnectionStrings__Default:
    process.env['ConnectionStrings__Default'] ?? 'Host=localhost;Port=5432;Database=openbp;Username=openbp;Password=openbp',
  OPENBP_ENCRYPTION_KEY: process.env['OPENBP_ENCRYPTION_KEY'] ?? randomBytes(32).toString('base64'),
  OPENBP_ADMIN_EMAIL: E2E_ADMIN.email,
  OPENBP_ADMIN_PASSWORD: E2E_ADMIN.password,
  ASPNETCORE_ENVIRONMENT: 'Development',
  DOTNET_ENVIRONMENT: 'Development',
  // Логи Api/Worker у висновку Playwright — лише попередження й помилки.
  Logging__LogLevel__Default: 'Warning',
};

export default defineConfig({
  testDir: './e2e-live',
  testMatch: '**/*.live.ts',
  // Один бекенд і одна БД на всі тести.
  workers: 1,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  timeout: 120_000,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never', outputFolder: 'playwright-report-live' }]] : 'list',
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
  webServer: [
    {
      command: 'node e2e-live/backend.mjs',
      url: 'http://localhost:5080/health',
      env: backendEnv,
      reuseExistingServer: false,
      timeout: 180_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: `npx ng serve --port ${PORT} --proxy-config proxy.conf.json`,
      url: `http://localhost:${PORT}`,
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
    },
  ],
});
