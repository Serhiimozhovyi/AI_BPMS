import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ProjectContextService } from '../../projects/project-context.service';
import { WorkflowGroup } from '../../workflows/workflows-api.service';
import { ProjectPage } from './project.page';

const base = '/api/v1/workflow-groups';

describe('ProjectPage', () => {
  async function setup(role: WorkflowGroup['role'] = 'editor') {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }) } } },
      ],
    });
    const fixture = TestBed.createComponent(ProjectPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http
      .expectOne({ method: 'GET', url: base })
      .flush([{ id: 'p1', name: 'Психологи', sortOrder: 0, workflowCount: 3, role, description: 'Бот підтримки', icon: 'psychology' }]);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує іконку, назву, опис і вкладки з власними адресами', async () => {
    const { host } = await setup();
    expect(host.querySelector('[data-testid="project-name"]')?.textContent).toContain('Психологи');
    expect(host.querySelector('[data-testid="project-icon"]')?.textContent).toContain('psychology');
    expect(host.querySelector('[data-testid="project-description"]')?.textContent).toContain('Бот підтримки');
    const tabs = [...host.querySelectorAll('[data-testid="project-tabs"] a')].map((a) => a.getAttribute('data-testid'));
    expect(tabs).toEqual(['tab-overview', 'tab-workflows', 'tab-tables', 'tab-connections', 'tab-app', 'tab-members']);
  });

  it('робить проєкт вибраним у шапці, щоб вкладки-сторінки фільтрувалися за ним', async () => {
    await setup();
    expect(TestBed.inject(ProjectContextService)['selected']()).toBe('p1');
  });

  it('editor змінює назву й опис, viewer — не бачить «Редагувати»', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-testid="project-edit"]')!.click();
    await render(fixture);
    const description = host.querySelector<HTMLInputElement>('[data-testid="project-description-input"]')!;
    description.value = 'Новий опис';
    description.dispatchEvent(new Event('input'));
    host.querySelector<HTMLFormElement>('[data-testid="project-form"]')!.dispatchEvent(new Event('submit'));
    const patch = http.expectOne({ method: 'PATCH', url: `${base}/p1` });
    expect(patch.request.body).toEqual({ name: 'Психологи', description: 'Новий опис', icon: 'psychology' });
    patch.flush({ id: 'p1', name: 'Психологи', sortOrder: 0, workflowCount: 3, description: 'Новий опис', icon: 'psychology' });
    http.expectOne({ method: 'GET', url: base }).flush([]);
    await render(fixture);
    expect(host.querySelector('[data-testid="project-description"]')?.textContent).toContain('Новий опис');
    expect(host.querySelector('[data-testid="project-edit"]')).not.toBeNull();
  });

  it('viewer не бачить кнопку редагування', async () => {
    const { host } = await setup('viewer');
    expect(host.querySelector('[data-testid="project-edit"]')).toBeNull();
  });
});
