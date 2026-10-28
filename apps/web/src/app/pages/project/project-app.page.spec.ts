import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectPage } from '../../projects/project-pages-api.service';
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
  async function setup(role: 'editor' | 'viewer', firstChild: unknown = null) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { firstChild, parent: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }) } } } },
      ],
    });
    TestBed.inject(ProjectContextService).projects.set([{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 0, role }]);
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
    expect([...host.querySelectorAll('[data-page]')].map((a) => a.textContent?.trim())).toEqual(['Слоти', 'Записи']);
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
});
