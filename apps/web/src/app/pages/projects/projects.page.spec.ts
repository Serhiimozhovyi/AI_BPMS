import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService, UserInfo } from '../../auth/auth.service';
import { ProjectsPage } from './projects.page';

const base = '/api/v1/workflow-groups';

describe('ProjectsPage', () => {
  async function setup(role: UserInfo['role']) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'u@openbp.test', role });
    const fixture = TestBed.createComponent(ProjectsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne({ method: 'GET', url: base }).flush([
      { id: 'p2', name: 'Продажі', sortOrder: 1, workflowCount: 0, role: 'viewer', description: '', icon: '' },
      { id: 'p1', name: 'Психологи', sortOrder: 0, workflowCount: 4, role: 'editor', description: 'Бот підтримки', icon: 'psychology' },
    ]);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує картки проєктів з описом, кількістю сценаріїв і роллю', async () => {
    const { host } = await setup('editor');
    const cards = [...host.querySelectorAll('[data-testid="project-card"]')];
    expect(cards.map((c) => c.getAttribute('data-project'))).toEqual(['p1', 'p2']);
    expect(cards[0].textContent).toContain('Бот підтримки');
    expect(cards[0].textContent).toContain('psychology');
    expect(cards[0].textContent).toContain('4');
    expect(cards[0].getAttribute('href')).toBe('/projects/p1');
    expect(cards[1].textContent).toContain('Viewer');
  });

  it('створює проєкт з картки й відкриває його', async () => {
    const { fixture, http, host } = await setup('editor');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    host.querySelector<HTMLButtonElement>('[data-testid="project-new"]')!.click();
    await render(fixture);
    const name = host.querySelector<HTMLInputElement>('[data-testid="project-create-name"]')!;
    name.value = 'HR';
    name.dispatchEvent(new Event('input'));
    host.querySelector<HTMLFormElement>('[data-testid="project-create-form"]')!.dispatchEvent(new Event('submit'));
    const post = http.expectOne({ method: 'POST', url: base });
    expect(post.request.body).toEqual({ name: 'HR' });
    post.flush({ id: 'p3', name: 'HR', sortOrder: 2, workflowCount: 0 });
    http.expectOne({ method: 'GET', url: base }).flush([]);
    await render(fixture);
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p3']);
  });

  it('viewer не бачить «Створити проєкт»', async () => {
    const { host } = await setup('viewer');
    expect(host.querySelector('[data-testid="project-new"]')).toBeNull();
  });

  describe('імпорт шаблону (F8-06)', () => {
    const bundle = { formatVersion: 1, project: { name: 'Психологи' }, tables: [], pages: [], workflows: [] };

    function pickFile(host: HTMLElement, content: string) {
      const input = host.querySelector<HTMLInputElement>('[data-testid="project-import-file"]')!;
      const file = new File([content], 'bundle.json', { type: 'application/json' });
      Object.defineProperty(input, 'files', { value: [file], configurable: true });
      input.dispatchEvent(new Event('change'));
    }

    async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
      for (let i = 0; i < 5; i++) {
        await new Promise((r) => setTimeout(r));
        await render(fixture);
      }
    }

    it('імпортує файл і показує результат з посиланням на проєкт', async () => {
      const { fixture, http, host } = await setup('editor');
      pickFile(host, JSON.stringify(bundle));
      await settle(fixture);
      const post = http.expectOne({ method: 'POST', url: '/api/v1/projects/import' });
      expect(post.request.body).toEqual({ bundle, mapping: { connectors: {}, credentials: {}, workflows: {} } });
      post.flush({
        projectId: 'p9', name: 'Психологи', projectCreated: true, tablesCreated: ['users'], tablesExtended: [], rowsInserted: {},
        pagesCreated: ['list'], pagesUpdated: [], workflowsCreated: ['bot'], workflowsUpdated: [], missingVariables: ['region'],
      });
      // Список на сторінці і контекст проєктів у шапці перечитуються окремо.
      const reloads = http.match({ method: 'GET', url: base });
      expect(reloads).toHaveLength(2);
      reloads.forEach((r) => r.flush([]));
      await settle(fixture);
      const result = host.querySelector('[data-testid="import-result"]')!;
      expect(result.textContent).toContain('Психологи');
      expect(host.querySelector('[data-testid="import-open"]')?.getAttribute('href')).toBe('/projects/p9');
      expect(host.querySelector('[data-testid="import-variables"]')?.textContent).toContain('region');
    });

    it('без відповідника просить id і повторює імпорт із mapping', async () => {
      const { fixture, http, host } = await setup('editor');
      pickFile(host, JSON.stringify(bundle));
      await settle(fixture);
      http.expectOne({ method: 'POST', url: '/api/v1/projects/import' }).flush(
        { code: 'IMPORT_UNMAPPED', unmapped: [{ kind: 'credential', ref: 'c-src', name: 'Telegram', type: 'telegram.bot', reason: 'not_found' }] },
        { status: 422, statusText: 'Unprocessable Entity' },
      );
      await settle(fixture);
      const field = host.querySelector<HTMLInputElement>('[data-testid="unmapped-field"]')!;
      expect(field.getAttribute('data-ref')).toBe('c-src');
      field.value = 'c-target';
      field.dispatchEvent(new Event('input'));
      host.querySelector<HTMLFormElement>('[data-testid="import-unmapped"]')!.dispatchEvent(new Event('submit'));
      const retry = http.expectOne({ method: 'POST', url: '/api/v1/projects/import' });
      expect(retry.request.body.mapping).toEqual({ connectors: {}, credentials: { 'c-src': 'c-target' }, workflows: {} });
      retry.flush({
        projectId: 'p9', name: 'Психологи', projectCreated: true, tablesCreated: [], tablesExtended: [], rowsInserted: {},
        pagesCreated: [], pagesUpdated: [], workflowsCreated: [], workflowsUpdated: [], missingVariables: [],
      });
      // Список на сторінці і контекст проєктів у шапці перечитуються окремо.
      const reloads = http.match({ method: 'GET', url: base });
      expect(reloads).toHaveLength(2);
      reloads.forEach((r) => r.flush([]));
      await settle(fixture);
      expect(host.querySelector('[data-testid="import-unmapped"]')).toBeNull();
      expect(host.querySelector('[data-testid="import-result"]')).not.toBeNull();
    });

    it('не шаблон — повідомлення без запиту; viewer кнопки не бачить', async () => {
      const { fixture, http, host } = await setup('editor');
      pickFile(host, '{"hello": 1}');
      await settle(fixture);
      http.expectNone('/api/v1/projects/import');
      expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('Це не шаблон проєкту');

      TestBed.resetTestingModule();
      const viewer = await setup('viewer');
      expect(viewer.host.querySelector('[data-testid="project-import"]')).toBeNull();
    });
  });
});
