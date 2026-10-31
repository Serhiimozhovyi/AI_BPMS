import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectPage } from '../../projects/project-pages-api.service';
import { ProjectAppShell } from '../../workflows/workflows-api.service';
import { ProjectAppPage } from './project-app.page';

const page = (key: string, name: string): ProjectPage => ({
  id: key,
  key,
  name,
  kind: 'list',
  definition: { table: 't' },
  roles: [],
  sortOrder: 0,
  updatedAt: '2026-10-28T00:00:00Z',
});

describe('ProjectAppPage (F8-04)', () => {
  async function setup(role: 'editor' | 'viewer', firstChild: unknown = null, app: ProjectAppShell | null = null) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { firstChild, parent: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }) } } } },
      ],
    });
    TestBed.inject(ProjectContextService).projects.set([{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 0, role, app }]);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(ProjectAppPage);
    fixture.detectChanges();
    return { fixture, navigate, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('меню сторінок і перехід на першу сторінку', async () => {
    const { fixture, http, host, navigate } = await setup('viewer');
    http.expectOne('/api/v1/projects/p1/pages').flush([page('slots', 'Слоти'), page('bookings', 'Записи')]);
    await render(fixture);
    expect([...host.querySelectorAll('[data-tab]')].map((a) => a.textContent?.trim())).toEqual(['Слоти', 'Записи']);
    expect(navigate).toHaveBeenCalledWith(['slots'], expect.objectContaining({ replaceUrl: true }));
  });

  it('вибрана сторінка не перекидається на першу', async () => {
    const { http, navigate } = await setup('viewer', { snapshot: { url: [{ path: 'slots' }] } });
    http.expectOne('/api/v1/projects/p1/pages').flush([page('slots', 'Слоти')]);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('без сторінок: editor бачить підказку про MCP, решта — що сторінок немає', async () => {
    const editor = await setup('editor');
    editor.http.expectOne('/api/v1/projects/p1/pages').flush([]);
    await render(editor.fixture);
    expect(editor.host.querySelector('[data-testid="app-empty"]')?.textContent).toContain('upsert_project_page');

    TestBed.resetTestingModule();
    const viewer = await setup('viewer');
    viewer.http.expectOne('/api/v1/projects/p1/pages').flush([]);
    await render(viewer.fixture);
    expect(viewer.host.querySelector('[data-testid="app-empty"]')?.textContent).toContain('Для вашої ролі');
  });

  it('editor бачить «Нова сторінка», а в конструкторі посилання ховається', async () => {
    const editor = await setup('editor', { snapshot: { url: [{ path: 'slots' }] } });
    editor.http.expectOne('/api/v1/projects/p1/pages').flush([page('slots', 'Слоти')]);
    await render(editor.fixture);
    expect(editor.host.querySelector('[data-testid="app-new-page"]')).not.toBeNull();

    TestBed.resetTestingModule();
    const builder = await setup('editor', { snapshot: { url: [{ path: '_builder' }] } });
    builder.http.expectOne('/api/v1/projects/p1/pages').flush([page('slots', 'Слоти')]);
    await render(builder.fixture);
    expect(builder.host.querySelector('[data-testid="app-new-page"]')).toBeNull();

    TestBed.resetTestingModule();
    const viewer = await setup('viewer', { snapshot: { url: [{ path: 'slots' }] } });
    viewer.http.expectOne('/api/v1/projects/p1/pages').flush([page('slots', 'Слоти')]);
    await render(viewer.fixture);
    expect(viewer.host.querySelector('[data-testid="app-new-page"]')).toBeNull();
  });

  describe('оболонка застосунку (F8-18)', () => {
    const shell: ProjectAppShell = {
      title: 'Кабінет',
      icon: 'calendar_month',
      home: 'today',
      nav: [
        { label: '', pages: ['today', 'schedule'] },
        { label: 'Записи', pages: ['bookings'] },
        { label: 'Службове', pages: ['hidden-for-role'] },
      ],
    };
    const pages = [page('bookings', 'Записи'), page('today', 'Сьогодні'), page('schedule', 'Мій розклад'), page('settings', 'Налаштування')];

    it('показує назву, значок і меню за групами лише з видимих сторінок, а сторінки не з меню — не показує', async () => {
      const { fixture, http, host } = await setup('viewer', null, shell);
      http.expectOne('/api/v1/projects/p1/pages').flush(pages);
      await render(fixture);
      expect(host.querySelector('[data-testid="shell-title"]')?.textContent).toBe('Кабінет');
      expect(host.querySelector('[data-testid="shell-icon"]')?.textContent).toBe('calendar_month');
      expect([...host.querySelectorAll('[data-testid="shell-group"]')].map((g) => g.textContent)).toEqual(['Записи']);
      expect([...host.querySelectorAll('[data-page]')].map((a) => a.getAttribute('data-page'))).toEqual(['today', 'schedule', 'bookings']);
      expect(host.querySelector('[data-testid="app-nav"]')).toBeNull();
    });

    it('відкриває головну сторінку оболонки', async () => {
      const { fixture, http, navigate } = await setup('viewer', null, shell);
      http.expectOne('/api/v1/projects/p1/pages').flush(pages);
      await render(fixture);
      expect(navigate).toHaveBeenCalledWith(['today'], expect.objectContaining({ replaceUrl: true }));
    });

    it('без головної чи коли її не видно — перша сторінка меню', async () => {
      const { fixture, http, navigate } = await setup('viewer', null, { ...shell, home: 'secret' });
      http.expectOne('/api/v1/projects/p1/pages').flush(pages);
      await render(fixture);
      expect(navigate).toHaveBeenCalledWith(['today'], expect.objectContaining({ replaceUrl: true }));
    });

    it('службові форми не потрапляють у меню навіть із app.nav; пункти мають значок за типом сторінки', async () => {
      const slotNew: ProjectPage = { ...page('slot-new', 'Слот — новий запис'), kind: 'form', definition: { table: 'slots', fields: [], onSubmit: { operation: 'insert' } } };
      const today: ProjectPage = { ...page('today', 'Сьогодні'), kind: 'dashboard', definition: { table: '', blocks: [{ kind: 'actions', actions: [{ label: 'Додати', page: 'slot-new' } as never] }] } };
      const { fixture, http, host } = await setup('viewer', null, { ...shell, nav: [{ label: '', pages: ['today'] }, { label: 'Записи', pages: ['bookings', 'slot-new'] }] });
      http.expectOne('/api/v1/projects/p1/pages').flush([today, page('bookings', 'Записи'), slotNew]);
      await render(fixture);
      expect([...host.querySelectorAll('[data-page]')].map((a) => a.getAttribute('data-page'))).toEqual(['today', 'bookings']);
      expect(host.querySelector('[data-page="today"] .material-symbols-outlined')?.textContent).toBe('space_dashboard');
      expect(host.querySelector('[data-page="bookings"] .side-nav__label')?.textContent).toBe('Записи');
    });

    it('меню згортається до значків і пам’ятає це', async () => {
      localStorage.removeItem('openbp.appNav.collapsed');
      const { fixture, http, host } = await setup('viewer', null, shell);
      http.expectOne('/api/v1/projects/p1/pages').flush(pages);
      await render(fixture);
      const nav = host.querySelector('app-side-nav')!;
      expect(nav.classList).not.toContain('side-nav--collapsed');
      host.querySelector<HTMLButtonElement>('[data-testid="shell-collapse"]')!.click();
      await render(fixture);
      expect(nav.classList).toContain('side-nav--collapsed');
      expect(localStorage.getItem('openbp.appNav.collapsed')).toBe('1');
      host.querySelector<HTMLButtonElement>('[data-testid="shell-collapse"]')!.click();
      await render(fixture);
      expect(localStorage.getItem('openbp.appNav.collapsed')).toBeNull();
    });

    it('editor бачить кнопку «Конструктор», viewer — ні', async () => {
      const editor = await setup('editor', null, shell);
      editor.http.expectOne('/api/v1/projects/p1/pages').flush(pages);
      await render(editor.fixture);
      expect(editor.host.querySelector('[data-testid="shell-builder"]')).not.toBeNull();
      TestBed.resetTestingModule();
      const viewer = await setup('viewer', null, shell);
      viewer.http.expectOne('/api/v1/projects/p1/pages').flush(pages);
      await render(viewer.fixture);
      expect(viewer.host.querySelector('[data-testid="shell-builder"]')).toBeNull();
    });
  });
});
