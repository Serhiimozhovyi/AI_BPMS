import { Page, expect, test } from '@playwright/test';

/**
 * F3-64: групи на сторінці сценаріїв. Api підмінено in-memory сховищем (`page.route`), яке переживає перезавантаження
 * сторінки, — тож «перезавантажити — сценарій у групі» перевіряє саме збережений стан.
 */

interface Workflow {
  id: string;
  name: string;
  active: boolean;
  version: number;
  updatedAt: string;
  groupId: string | null;
}

interface Group {
  id: string;
  name: string;
  sortOrder: number;
}

class FakeApi {
  workflows: Workflow[] = [
    { id: 'w1', name: 'Заявки з сайту', active: false, version: 3, updatedAt: '2026-10-23T10:00:00Z', groupId: null },
    { id: 'w2', name: 'Щоденний звіт', active: false, version: 1, updatedAt: '2026-10-22T10:00:00Z', groupId: null },
  ];
  groups: Group[] = [];

  private summary(g: Group) {
    return { ...g, workflowCount: this.workflows.filter((w) => w.groupId === g.id).length };
  }

  async install(page: Page): Promise<void> {
    await page.route('**/api/v1/**', async (route) => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      const method = request.method();
      if (path === '/api/v1/auth/me') return route.fulfill({ json: { id: 'u1', email: 'e2e@openbp.test', role: 'admin' } });
      if (path === '/api/v1/workflows' && method === 'GET') {
        return route.fulfill({ json: { items: this.workflows, page: 1, pageSize: 200, total: this.workflows.length } });
      }
      if (path === '/api/v1/workflow-groups' && method === 'GET') {
        return route.fulfill({ json: [...this.groups].sort((a, b) => a.sortOrder - b.sortOrder).map((g) => this.summary(g)) });
      }
      if (path === '/api/v1/workflow-groups' && method === 'POST') {
        const group: Group = { id: `g${this.groups.length + 1}`, name: (request.postDataJSON() as { name: string }).name, sortOrder: this.groups.length };
        this.groups.push(group);
        return route.fulfill({ status: 201, json: this.summary(group) });
      }
      const match = /^\/api\/v1\/workflows\/(w\d+)$/.exec(path);
      if (match && method === 'PATCH') {
        const workflow = this.workflows.find((w) => w.id === match[1])!;
        Object.assign(workflow, request.postDataJSON());
        return route.fulfill({ json: { ...workflow, versionId: 'v', definition: {} } });
      }
      return route.fulfill({ status: 404, json: { title: 'Not Found', status: 404 } });
    });
  }
}

test('створити групу, перетягнути в неї сценарій, перезавантажити — сценарій у групі', async ({ page }) => {
  const api = new FakeApi();
  await api.install(page);
  await page.goto('/workflows');
  await expect(page.locator('[data-workflow]')).toHaveCount(2);

  await page.getByTestId('new-group-button').click();
  await page.getByTestId('group-name').fill('Продажі');
  await page.getByTestId('group-submit').click();
  const sales = page.locator('[data-section="g1"]');
  await expect(sales.getByTestId('section-empty')).toBeVisible();

  // Перетягнути «Заявки з сайту» за ручку в порожню секцію групи.
  const handle = page.locator('[data-workflow="w1"]').getByTestId('row-handle');
  const from = (await handle.boundingBox())!;
  const to = (await sales.getByTestId('section-empty').boundingBox())!;
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(from.x + 20, from.y - 10, { steps: 5 });
  await page.mouse.move(to.x + 40, to.y + to.height / 2, { steps: 10 });
  await page.mouse.up();

  await expect(sales.locator('[data-workflow="w1"]')).toBeVisible();
  await expect(sales.getByTestId('section-count')).toHaveText('1');
  await expect.poll(() => api.workflows.find((w) => w.id === 'w1')?.groupId).toBe('g1');

  await page.reload();
  await expect(page.locator('[data-section="g1"] [data-workflow="w1"]')).toBeVisible();
  await expect(page.locator('[data-section="none"] [data-workflow="w2"]')).toBeVisible();

  // Перемістити з меню рядка назад «Без групи».
  await page.locator('[data-workflow="w1"]').getByTestId('row-menu').click();
  await page.getByTestId('move-button').click();
  await page.locator('[data-testid="move-to"][data-target="none"]').click();
  await expect(page.locator('[data-section="none"] [data-workflow="w1"]')).toBeVisible();
  await expect.poll(() => api.workflows.find((w) => w.id === 'w1')?.groupId).toBeNull();
});
