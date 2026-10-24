import { randomBytes } from 'node:crypto';
import { defineConfig, devices } from '@playwright/test';
import { E2E } from './e2e/env';

/**
 * E2E адмінки проти справжнього бекенду F3-34 (`--http`), який віддає зібрану SPA з `wwwroot`.
 * Потрібні `npm run build` і Postgres (`PSY_E2E_POSTGRES`, за замовчуванням локальний openbp/openbp);
 * кожен прогін — у новій базі. Запуск: `npm run e2e`.
 */
const postgres =
    process.env['PSY_E2E_POSTGRES'] ?? 'Host=localhost;Port=5432;Username=openbp;Password=openbp';

export default defineConfig({
    testDir: './e2e',
    testMatch: '**/*.e2e.ts',
    // Один бекенд і одна база на всі тести.
    workers: 1,
    forbidOnly: !!process.env['CI'],
    retries: 0,
    timeout: 60_000,
    reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
    use: {
        baseURL: E2E.url,
        viewport: { width: 1440, height: 900 },
        trace: 'retain-on-failure',
    },
    projects: [
        {
            name: 'chromium',
            use: {
                ...devices['Desktop Chrome'],
                viewport: { width: 1440, height: 900 },
                launchOptions: process.env['PLAYWRIGHT_CHROMIUM_PATH']
                    ? { executablePath: process.env['PLAYWRIGHT_CHROMIUM_PATH'] }
                    : {},
            },
        },
    ],
    webServer: {
        command: `dotnet run --project ../src/OpenBP.Connectors.PsySupport --no-launch-profile -- --http ${E2E.url}`,
        url: `${E2E.url}/login`,
        env: {
            ConnectionStrings__PsySupport: `${postgres};Database=psy_e2e_${randomBytes(4).toString('hex')}`,
            PSY_ADMIN_EMAIL: E2E.coordinator.email,
            PSY_ADMIN_PASSWORD: E2E.coordinator.password,
            PSY_MCP_TOKEN: E2E.mcpToken,
            Logging__LogLevel__Default: 'Warning',
        },
        reuseExistingServer: false,
        timeout: 180_000,
        stdout: 'pipe',
        stderr: 'pipe',
    },
});
