import { randomBytes } from 'node:crypto';

/** Значення генерується один раз у головному процесі Playwright і через середовище доходить до воркерів. */
function once(name: string, make: () => string): string {
    return (process.env[name] ??= make());
}

/** Параметри прогону: облікові дані лише тестові, нові на кожен запуск. */
export const E2E = {
    url: 'http://127.0.0.1:8095',
    coordinator: {
        email: 'coordinator@e2e.test',
        password: once('PSY_E2E_PASSWORD', () => `e2e-${randomBytes(9).toString('base64url')}`),
    },
    mcpToken: once('PSY_E2E_MCP_TOKEN', () => `mcp-${randomBytes(18).toString('base64url')}`),
};
