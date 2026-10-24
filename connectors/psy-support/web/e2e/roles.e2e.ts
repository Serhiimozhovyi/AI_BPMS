import { Page, expect, test } from '@playwright/test';
import { E2E } from './env';
import { Seed, seed } from './seed';

const SHOTS = process.env['PSY_E2E_SCREENSHOTS'] ?? 'test-results/screenshots';
const COORDINATOR_PAGES = ['texts', 'staff', 'counters', 'audit', 'users'];

let data: Seed;
let inviteLink = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async () => {
    data = await seed();
});

async function login(page: Page, email: string, password: string): Promise<void> {
    await page.goto('/login');
    await page.getByTestId('login-email').fill(email);
    await page.getByTestId('login-password').fill(password);
    await page.getByTestId('login-submit').click();
}

function row(page: Page, psychologist: string) {
    return page.getByTestId('bookings').locator('tbody tr', { hasText: psychologist });
}

test('неправильний пароль не впускає', async ({ page }) => {
    await login(page, E2E.coordinator.email, 'неправильний-пароль');
    await expect(page.getByTestId('login-error')).toBeVisible();
    await expect(page).toHaveURL(/\/login/);
    await page.screenshot({ path: `${SHOTS}/01-login.png` });
});

test('координатор бачить усі розділи й телефони всіх записів, видає посилання психологу', async ({
    page,
}) => {
    await login(page, E2E.coordinator.email, E2E.coordinator.password);
    await expect(page).toHaveURL(/\/schedule$/);
    await expect(page.getByTestId('current-role')).toHaveText('координатор');
    for (const p of COORDINATOR_PAGES) {
        await expect(page.getByTestId(`nav-${p}`)).toBeVisible();
    }
    await page.screenshot({ path: `${SHOTS}/02-coordinator-schedule.png` });

    await page.getByTestId('nav-bookings').click();
    await expect(row(page, data.own.psychologist)).toContainText(`+${data.own.phone}`);
    await expect(row(page, data.peer.psychologist)).toContainText(`+${data.peer.phone}`);
    await page.screenshot({ path: `${SHOTS}/03-coordinator-bookings.png` });

    await page.getByTestId('nav-staff').click();
    await page
        .getByTestId('accounts')
        .locator('tr', { hasText: data.psychologistAccount.email })
        .getByRole('button', { name: 'Нове посилання' })
        .click();
    const link = page.getByTestId('invite-link').locator('code');
    await expect(link).toContainText('/invite?token=');
    inviteLink = (await link.textContent())?.trim() ?? '';
    await page.screenshot({ path: `${SHOTS}/04-coordinator-staff.png` });
});

test('психолог задає пароль за посиланням і не бачить сторінок координатора та чужих телефонів', async ({
    page,
}) => {
    expect(inviteLink).not.toBe('');
    await page.goto(inviteLink);
    await page.getByTestId('invite-password').fill('пароль-психолога-1');
    await page.getByTestId('invite-repeat').fill('пароль-психолога-1');
    await page.screenshot({ path: `${SHOTS}/05-invite.png` });
    await page.getByTestId('invite-submit').click();

    await expect(page).toHaveURL(/\/schedule$/);
    await expect(page.getByTestId('current-role')).toHaveText('психолог');
    await expect(page.getByTestId('nav-schedule')).toBeVisible();
    for (const p of COORDINATOR_PAGES) {
        await expect(page.getByTestId(`nav-${p}`)).toHaveCount(0);
    }

    await page.getByTestId('nav-bookings').click();
    const own = row(page, data.own.psychologist);
    const peer = row(page, data.peer.psychologist);
    await expect(own).toContainText(`+${data.own.phone}`);
    await expect(own).toContainText(data.own.person);
    await expect(peer).toBeVisible();
    await expect(peer).not.toContainText(data.peer.phone);
    await expect(peer).not.toContainText(data.peer.person);
    await expect(page.locator('body')).not.toContainText(data.peer.phone);
    await page.screenshot({ path: `${SHOTS}/06-psychologist-bookings.png` });

    for (const p of COORDINATOR_PAGES) {
        await page.goto(`/${p}`);
        await expect(page).toHaveURL(/\/schedule$/);
    }
    // REST теж закритий, а не лише меню.
    const staff = await page.request.get('/api/v1/staff');
    expect(staff.status()).toBe(403);
});

test('після виходу сторінки недоступні', async ({ page }) => {
    await login(page, E2E.coordinator.email, E2E.coordinator.password);
    await expect(page).toHaveURL(/\/schedule$/);
    await page.getByTestId('logout').click();
    await expect(page).toHaveURL(/\/login/);
    await page.goto('/bookings');
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fbookings/);
});
