import { Page, expect, test } from '@playwright/test';

/** F8-17: календар застосунку — створення запису кліком по клітинці, зміна запису з календаря, клавіатура. */
const USER = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

const CALENDAR_PAGE = {
  id: 'pg1',
  key: 'schedule',
  name: 'Розклад',
  kind: 'calendar',
  definition: {
    table: 'slots',
    calendar: {
      start: 'starts_at',
      duration: 'duration_min',
      title: { column: 'note' },
      color: { column: 'status', format: 'badge', colors: { open: 'green', booked: 'blue' } },
      fromHour: 8,
      toHour: 20,
      open: { page: 'slot-form', mode: 'drawer' },
    },
    create: { page: 'slot-form', mode: 'drawer' },
  },
  roles: [],
  sortOrder: 0,
  updatedAt: '2026-11-04T00:00:00Z',
};

const FORM_PAGE = { ...CALENDAR_PAGE, id: 'pg2', key: 'slot-form', name: 'Слот', kind: 'form', definition: { table: 'slots', onSubmit: { operation: 'upsert' } }, sortOrder: 1 };

interface Slot {
  key: string;
  starts_at: string;
  note: string;
}

async function mockApi(page: Page, slots: Slot[], submitted: unknown[]) {
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    if (path === '/api/v1/auth/me') return route.fulfill({ json: USER });
    if (path === '/api/v1/workflow-groups') {
      return route.fulfill({ json: [{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 0, role: 'admin' }] });
    }
    if (path === '/api/v1/projects/p1/pages') return route.fulfill({ json: [CALENDAR_PAGE, FORM_PAGE] });
    if (path === '/api/v1/projects/p1/pages/schedule') {
      return route.fulfill({
        json: {
          page: CALENDAR_PAGE,
          tableId: 't1',
          columns: [{ column: 'note', label: 'Нотатка', type: 'string' }],
          schema: null,
          canEdit: true,
          timezone: 'Europe/Kyiv',
        },
      });
    }
    if (path === '/api/v1/projects/p1/pages/schedule/calendar') {
      return route.fulfill({
        json: {
          from: url.searchParams.get('from'),
          to: url.searchParams.get('to'),
          events: slots.map((s) => ({
            start: s.starts_at,
            end: new Date(new Date(s.starts_at).getTime() + 3_600_000).toISOString(),
            row: { key: s.key, data: { note: s.note, status: 'open' }, display: {}, actions: [] },
          })),
          truncated: false,
        },
      });
    }
    if (path === '/api/v1/projects/p1/pages/slot-form') {
      return route.fulfill({
        json: {
          page: FORM_PAGE,
          tableId: 't1',
          columns: [],
          schema: { type: 'object', properties: { starts_at: { type: 'string', format: 'date-time', title: 'Початок' }, note: { type: 'string', title: 'Нотатка' } } },
          canEdit: true,
          timezone: 'Europe/Kyiv',
        },
      });
    }
    if (path.startsWith('/api/v1/projects/p1/pages/slot-form/rows/')) {
      const slot = slots.find((s) => s.key === path.split('/').pop())!;
      return route.fulfill({ json: { row: { key: slot.key, data: { starts_at: slot.starts_at, note: slot.note }, display: {}, actions: [] }, related: [] } });
    }
    if (path === '/api/v1/projects/p1/pages/slot-form/submit' && method === 'POST') {
      const body = request.postDataJSON() as { values: { starts_at: string; note: string }; rowKey: string | null };
      submitted.push(body);
      const existing = slots.find((s) => s.key === body.rowKey);
      if (existing) Object.assign(existing, { note: body.values.note, starts_at: body.values.starts_at ?? existing.starts_at });
      else slots.push({ key: `s${slots.length + 1}`, starts_at: body.values.starts_at, note: body.values.note });
      return route.fulfill({ json: { row: null, then: null } });
    }
    return route.fulfill({ json: [] });
  });
}

test('календар: створення з клітинки, зміна з події, клавіатура', async ({ page }) => {
  const slots: Slot[] = [{ key: 's1', starts_at: '2026-11-04T08:00:00Z', note: 'Консультація' }];
  const submitted: { values: { starts_at: string; note: string }; rowKey: string | null }[] = [];
  await page.clock.setFixedTime(new Date('2026-11-04T10:00:00Z'));
  await mockApi(page, slots, submitted);
  await page.goto('/projects/p1/app/schedule');

  await expect(page.getByTestId('app-page-title')).toHaveText('Розклад');
  await expect(page.locator('[data-day="2026-11-04"] [data-event="s1"]')).toContainText('Консультація');

  // Клавіатура: фокус на клітинку, стрілка вправо, Enter відкриває форму з часом.
  const slot = page.locator('[data-slot="2026-11-04T14:00"]');
  await slot.focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('[data-slot="2026-11-05T14:00"]')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('app-drawer')).toBeVisible();
  await expect(page.locator('input[type="datetime-local"]')).toHaveValue('2026-11-05T14:00');
  await page.getByLabel('Нотатка').fill('Нова зустріч');
  await page.getByTestId('app-submit').click();
  await expect(page.getByTestId('app-drawer')).toHaveCount(0);
  expect(submitted[0].values.starts_at).toBe('2026-11-05T12:00:00.000Z');
  await expect(page.locator('[data-day="2026-11-05"] [data-event]')).toContainText('Нова зустріч');

  // Зміна: клік по події відкриває форму з її часом у поясі застосунку.
  await page.locator('[data-event="s1"]').click();
  await expect(page.locator('input[type="datetime-local"]')).toHaveValue('2026-11-04T10:00');
  await page.getByLabel('Нотатка').fill('Перенесена консультація');
  await page.getByTestId('app-submit').click();
  await expect(page.getByTestId('app-drawer')).toHaveCount(0);
  expect(submitted[1].rowKey).toBe('s1');
  await expect(page.locator('[data-event="s1"]')).toContainText('Перенесена консультація');

  // Вигляд «Місяць» і повернення до дня.
  await page.locator('[data-cal-view="month"]').click();
  await expect(page.getByTestId('cal-range')).toContainText('листопад 2026');
  await page.locator('[data-day-open="2026-11-05"]').click();
  await expect(page.getByTestId('cal-grid')).toHaveAttribute('data-view', 'day');
});
