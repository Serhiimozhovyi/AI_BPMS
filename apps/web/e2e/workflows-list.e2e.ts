import { Page, expect, test } from '@playwright/test';

/**
 * F3-80: список сценаріїв — колонки «Тригер» і «Останній запуск», меню «⋯», діалог створення. Api підмінено
 * in-memory сховищем (`page.route`), яке переживає перезавантаження сторінки.
 */

interface Workflow {
  id: string;
  name: string;
  active: boolean;
  version: number;
  updatedAt: string;
  groupId: string | null;
  publishedVersion: number | null;
  triggerType: string | null;
  lastExecution: { id: string; status: string; startedAt: string | null } | null;
}

class FakeApi {
  workflows: Workflow[] = [
    {
      id: 'w1',
      name: 'Заявки з сайту',
      active: false,
      version: 3,
      updatedAt: '2026-10-23T10:00:00Z',
      groupId: null,
      publishedVersion: 2,
      triggerType: 'core.schedule',
      lastExecution: { id: 'e1', status: 'error', startedAt: '2026-10-27T08:30:00Z' },
    },
    {
      id: 'w2',
      name: 'Щоденний звіт',
      active: false,
      version: 1,
      updatedAt: '2026-10-22T10:00:00Z',
      groupId: null,
      publishedVersion: null,
      triggerType: null,
      lastExecution: null,
    },
  ];
  deleted: string[] = [];

  async install(page: Page): Promise<void> {
    await page.route('**/api/v1/**', async (route) => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      const method = request.method();
      if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
      if (path === '/api/v1/workflows' && method === 'GET') {
        return route.fulfill({ json: { items: this.workflows, page: 1, pageSize: 200, total: this.workflows.length } });
      }
      if (path === '/api/v1/workflows' && method === 'POST') {
        const created: Workflow = {
          id: 'w9',
          name: (request.postDataJSON() as { name: string }).name,
          active: false,
          version: 1,
          updatedAt: '2026-10-27T12:00:00Z',
          groupId: null,
          publishedVersion: null,
          triggerType: null,
          lastExecution: null,
        };
        this.workflows.unshift(created);
        return route.fulfill({ status: 201, json: { ...created, versionId: 'v9', definition: {} } });
      }
      if (path === '/api/v1/workflow-groups' && method === 'GET') return route.fulfill({ json: [] });
      const match = /^\/api\/v1\/workflows\/(w\d+)$/.exec(path);
      if (match && method === 'DELETE') {
        this.deleted.push(match[1]);
        this.workflows = this.workflows.filter((w) => w.id !== match[1]);
        return route.fulfill({ status: 204 });
      }
      return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
    });
  }
}

test('у списку видно тригер і останній запуск, а створення сценарію йде через діалог у редактор', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto('/workflows');

  const first = page.locator('[data-workflow="w1"]');
  await expect(first.getByTestId('trigger-cell')).toContainText('За розкладом');
  await expect(first.getByTestId('last-run-state')).toHaveText('Помилка');
  await expect(first.getByTestId('last-run-at')).toContainText('27.10.2026');
  const second = page.locator('[data-workflow="w2"]');
  await expect(second.getByTestId('last-run-cell')).toContainText('Не запускався');
  // Без тригера активувати не можна: перемикач вимкнено з поясненням.
  await expect(second.getByTestId('active-toggle-hint')).toHaveAttribute('title', /додайте тригер/);

  await page.getByTestId('create-button').click();
  await page.getByTestId('create-name').fill('Нові замовлення');
  await page.getByTestId('create-submit').click();
  await expect(page).toHaveURL(/\/workflows\/w9$/);
});

test('меню «⋯» → «Видалити» просить підтвердження і прибирає сценарій', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto('/workflows');
  const row = page.locator('[data-workflow="w2"]');
  await expect(row).toBeVisible();

  await row.getByTestId('row-menu').click();
  await page.getByTestId('delete-button').click();
  await expect(row).toContainText('Видалити сценарій разом з історією виконань?');
  await row.getByTestId('delete-confirm').click();

  await expect(row).toHaveCount(0);
  expect(api.deleted).toEqual(['w2']);
  await page.reload();
  await expect(page.locator('[data-workflow]')).toHaveCount(1);
});
