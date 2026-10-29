import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { Page, expect, test } from '@playwright/test';

/**
 * F8-20: кабінет психолога й координатора в шаблоні `psy-support`. Сторінки й оболонка беруться з самого бандла шаблону,
 * Api підміняється (як в інших e2e), тож тест ловить розбіжність між шаблоном і інтерфейсом. Знімки екранів — `SHOTS=1`.
 */
interface Bundle {
  project: { app: { title: string; icon: string; home: string; timezone: string; nav: { label: string; pages: string[] }[] } };
  tables: { name: string; columns: { name: string; type: string; values?: string[] }[] }[];
  pages: { key: string; name: string; kind: string; roles: string[]; definition: Record<string, any>; sortOrder: number }[];
}

const bundle = JSON.parse(readFileSync(resolve(process.cwd(), '../../examples/projects/psy-support/bundle.json'), 'utf8')) as Bundle;
const SHOTS = process.env['SHOTS'] ? resolve(process.cwd(), '../../examples/projects/psy-support/screenshots') : null;
const NOW = '2026-11-04T10:00:00Z';
const at = (hours: number) => new Date(new Date(NOW).getTime() + hours * 3_600_000).toISOString();

const row = (key: string, data: Record<string, unknown>, display: Record<string, string> = {}, actions: number[] = []) => ({ key, data, display, actions });

const BOOKINGS = [
  row('b1', { starts_at: at(2), psychologist_id: 'p1', client_name: 'Марія Коваль', client_phone: '380671234567', status: 'active' }, { psychologist_id: 'Олена Бондар' }, [0]),
  row('b2', { starts_at: at(26), psychologist_id: 'p1', client_name: 'Іван Петренко', client_phone: '380501112233', status: 'active' }, { psychologist_id: 'Олена Бондар' }, [0]),
  row('b3', { starts_at: at(-30), psychologist_id: 'p2', client_name: 'Оксана Мельник', client_phone: '380931112233', status: 'cancelled', cancelled_by: 'user' }, { psychologist_id: 'Ірина Шевчук' }),
];
const EVENTS = [
  row('e1', { starts_at: at(72), title: 'Лекція «Як упоратися зі стресом»', format: 'online', registered: 2, capacity: 8, status: 'open' }, {}, [0, 1]),
  row('e2', { starts_at: at(240), title: 'Група підтримки', format: 'offline', registered: 0, capacity: 0, status: 'draft' }, {}, [0]),
];
const SLOTS = [
  { key: 's1', start: at(1), minutes: 50, format: 'online', status: 'closed' },
  { key: 's2', start: at(3), minutes: 50, format: 'offline', status: 'open' },
  { key: 's3', start: at(25), minutes: 50, format: 'online', status: 'open' },
];
const slotEvents = () =>
  SLOTS.map((s) => ({
    start: s.start,
    end: new Date(new Date(s.start).getTime() + s.minutes * 60_000).toISOString(),
    row: row(s.key, { starts_at: s.start, duration_min: s.minutes, format: s.format, status: s.status, psychologist_id: 'p1' }, { psychologist_id: 'Олена Бондар' }),
  }));

const rowsFor: Record<string, ReturnType<typeof row>[]> = {
  bookings: BOOKINGS,
  events: EVENTS,
  bst: [row('t1', { starts_at: at(96), psychologist_id: 'p1', format: 'offline', registered: 3, capacity: 8, status: 'open' }, { psychologist_id: 'Олена Бондар' }, [0, 1])],
  templates: [row('tp1', { psychologist_id: 'p1', weekday: 2, time: '10:00', duration_min: 50, format: 'online' }, { psychologist_id: 'Олена Бондар' })],
  materials: [row('m1', { title: 'Дихальні вправи', status: 'published', file_type: 'document', views: 14, published_at: at(-100) })],
  psychologists: [row('p1', { name: 'Олена Бондар', email: 'olena@firma.ua', phone: '380671234567', active: true })],
  users: [row('u1', { name: 'Марія Коваль', phone: '380671234567', registered_at: at(-200) })],
  journal: [row('j1', { at: at(-5), actor: 'bot', action: 'book-slot', entity: 'bookings', entity_id: 'b1' })],
};

/** Підпис колонки, як його віддає Api: з визначення сторінки, інакше назва колонки. */
function labelOf(definition: Record<string, any>, column: string): string {
  const all = [...(definition['columns'] ?? []), ...(definition['fields'] ?? [])] as { column: string; label?: string }[];
  return all.find((c) => c.column === column)?.label ?? column;
}

function viewOf(key: string, canEdit = false) {
  const page = bundle.pages.find((p) => p.key === key)!;
  const table = bundle.tables.find((t) => t.name === page.definition['table']);
  const filterOptions: Record<string, { value: string; label: string }[]> = {};
  for (const f of (page.definition['filters'] ?? []) as { column: string; kind: string; labels?: Record<string, string> }[]) {
    const column = table?.columns.find((c) => c.name === f.column);
    if (column?.values) filterOptions[f.column] = column.values.map((v) => ({ value: v, label: f.labels?.[v] ?? v }));
    else if (f.kind === 'select') filterOptions[f.column] = [{ value: 'u1', label: 'Олена Бондар' }];
  }
  const properties: Record<string, unknown> = {};
  if (page.definition['keyValue']) {
    for (const [name, title] of Object.entries(page.definition['keyValue'].labels as Record<string, string>)) properties[name] = { type: 'string', title };
  }
  return {
    page: { id: key, key, name: page.name, kind: page.kind, definition: page.definition, roles: page.roles, sortOrder: page.sortOrder, updatedAt: '2026-11-04T00:00:00Z' },
    tableId: 't-' + key,
    columns: (table?.columns ?? []).map((c) => ({ column: c.name, label: labelOf(page.definition, c.name), type: c.type })),
    schema: page.kind === 'form' ? { type: 'object', properties } : null,
    canEdit,
    timezone: 'Europe/Kyiv',
    filterOptions,
  };
}

async function mockApi(page: Page, appRole: 'psychologist' | 'coordinator') {
  const visible = bundle.pages.filter((p) => p.roles.includes(appRole));
  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u9', email: 'olena@firma.ua', role: 'viewer' } });
    if (path === '/api/v1/workflow-groups') {
      return route.fulfill({
        json: [{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 13, role: 'viewer', appRole, app: bundle.project.app }],
      });
    }
    if (path === '/api/v1/projects/p1/pages') {
      return route.fulfill({ json: visible.map((p) => ({ id: p.key, key: p.key, name: p.name, kind: p.kind, definition: p.definition, roles: p.roles, sortOrder: p.sortOrder, updatedAt: '' })) });
    }
    const match = /^\/api\/v1\/projects\/p1\/pages\/([^/]+)(?:\/(.*))?$/.exec(path);
    if (!match) return route.fulfill({ json: [] });
    const [, key, rest] = match;
    if (!visible.some((p) => p.key === key)) return route.fulfill({ status: 404, json: { title: 'Не знайдено' } });
    if (!rest) return route.fulfill({ json: viewOf(key) });
    if (rest === 'rows') {
      const items = rowsFor[key] ?? [];
      return route.fulfill({ json: { items, page: 1, pageSize: 50, total: items.length, filters: key === 'bookings' ? { status: { values: ['active'] } } : key === 'events' ? { status: { values: ['open', 'draft'] } } : null } });
    }
    if (rest === 'calendar') return route.fulfill({ json: { from: '', to: '', events: slotEvents(), truncated: false } });
    if (rest.startsWith('rows/e1/expand/')) {
      return route.fulfill({
        json: {
          label: 'Учасники',
          columns: [{ column: 'client_name', label: 'Ім’я', type: 'string' }],
          rows: [row('r1', { client_name: 'Марія Коваль', status: 'active' }, {}, [0]), row('r2', { client_name: 'Петро Іваненко', status: 'cancelled' })],
        },
      });
    }
    if (rest === 'dashboard') {
      const blocks = (bundle.pages.find((p) => p.key === 'today')!.definition['blocks'] as { kind: string; label?: string; width?: number; text?: string; definition?: Record<string, any>; when?: string; actions?: unknown[] }[])
        .map((b, index) => ({ b, index }))
        .filter(({ b }) => !b.when || b.when.includes(appRole))
        .map(({ b, index }) => {
          const base = { index, kind: b.kind, label: b.label ?? null, width: b.width ?? 12 };
          if (b.kind === 'text') return { ...base, text: 'Вітаємо, olena@firma.ua! Нижче — найближче в розкладі, записах і подіях.' };
          if (b.kind === 'actions') return { ...base, actions: b.actions!.map((_, i) => i) };
          if (b.kind === 'calendar') return { ...base, source: 'schedule', view: viewOf('schedule'), events: { from: '', to: '', events: slotEvents().slice(0, 2), truncated: false } };
          if (b.kind === 'tiles') return { ...base, source: 'metrics', metrics: { tiles: [{ key: 'booked', label: 'Активних записів', value: 12 }, { label: 'Заповненість слотів, %', value: 64.3 }], charts: [] } };
          const table = b.definition!['table'] as string;
          const items = table === 'bookings' ? BOOKINGS.slice(0, 2) : table === 'events' ? EVENTS.slice(0, 1) : rowsFor['bst'];
          const own = bundle.tables.find((t) => t.name === table)!;
          const fake = {
            ...viewOf('bookings'),
            page: { ...viewOf('bookings').page, definition: b.definition },
            columns: own.columns.map((c) => ({ column: c.name, label: labelOf(b.definition!, c.name), type: c.type })),
          };
          return { ...base, view: fake, rows: { items, page: 1, pageSize: 5, total: items.length } };
        });
      return route.fulfill({ json: { blocks } });
    }
    return route.fulfill({ json: viewOf(key) });
  });
}

async function shot(page: Page, name: string) {
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true });
}

test('психолог: вхід у кабінет, головна «Сьогодні», розклад, записи й меню лише його розділів', async ({ page }) => {
  await page.clock.setFixedTime(new Date(NOW));
  await mockApi(page, 'psychologist');
  await page.goto('/');

  await expect(page).toHaveURL(/\/projects\/p1\/app\/today$/);
  await expect(page.getByTestId('shell-title')).toHaveText('Кабінет психологічної підтримки');
  await expect(page.locator('[data-testid="shell-nav"] a')).toHaveText(['Сьогодні', 'Розклад', 'Записи', 'Події', 'Сеанси БСТ', 'Шаблони розкладу', 'Матеріали']);
  await expect(page.getByTestId('shell-group')).toHaveText(['Записи', 'Розклад і матеріали']);
  await expect(page.getByTestId('block-text')).toContainText('Вітаємо, olena@firma.ua');
  await expect(page.locator('[data-block-action]')).toHaveText(['Додати слот', 'Згенерувати слоти з шаблонів']);
  await expect(page.locator('[data-testid="dash-grid"]')).toContainText('Мої найближчі записи');
  await expect(page.locator('[data-testid="dash-grid"]')).not.toContainText('Показники');
  await shot(page, 'psychologist-today');

  await page.locator('[data-page="schedule"]').click();
  await expect(page.getByTestId('cal-grid')).toBeVisible();
  await expect(page.locator('[data-event="s1"]')).toContainText('Онлайн');
  await expect(page.locator('[data-event="s2"]')).toContainText('Наживо');
  await shot(page, 'psychologist-schedule');

  await page.locator('[data-page="bookings"]').click();
  await expect(page.locator('[data-row="b1"]')).toContainText('Марія Коваль');
  await expect(page.locator('[data-row="b1"]')).toContainText('+38 (067) 123-45-67');
  await expect(page.locator('[data-row="b1"]')).toContainText('Активний');
  await shot(page, 'psychologist-bookings');

  // Координаторських сторінок психологу не видно.
  await page.goto('/projects/p1/app/texts');
  await expect(page.getByTestId('app-page-title')).toHaveCount(0);
});

test('координатор: меню керування, події з учасниками під рядком, форма текстів', async ({ page }) => {
  await page.clock.setFixedTime(new Date(NOW));
  await mockApi(page, 'coordinator');
  await page.goto('/');

  await expect(page).toHaveURL(/\/projects\/p1\/app\/today$/);
  await expect(page.locator('[data-testid="shell-nav"] a')).toHaveText([
    'Сьогодні', 'Розклад', 'Записи', 'Події', 'Сеанси БСТ', 'Шаблони розкладу', 'Матеріали', 'Психологи', 'Тексти й налаштування', 'Користувачі бота', 'Журнал', 'Показники',
  ]);
  await expect(page.locator('[data-testid="dash-grid"]')).toContainText('Активних записів');
  await shot(page, 'coordinator-today');

  await page.locator('[data-page="events"]').click();
  await expect(page.locator('[data-row="e1"]')).toContainText('Лекція «Як упоратися зі стресом»');
  await expect(page.locator('[data-row="e1"]')).toContainText('2 / 8');
  await page.locator('[data-expand-toggle]').first().click();
  await expect(page.getByText('Марія Коваль')).toBeVisible();
  await expect(page.getByText('Петро Іваненко')).toBeVisible();
  await shot(page, 'coordinator-events');

  await page.locator('[data-page="texts"]').click();
  await expect(page.getByLabel('Привітання')).toBeVisible();
  await expect(page.getByLabel('Ліміт активних записів на людину')).toBeVisible();
  await shot(page, 'coordinator-texts');
});
