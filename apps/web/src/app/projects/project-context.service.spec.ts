import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { NO_GROUP } from '../workflows/workflow-groups';
import { WorkflowGroup } from '../workflows/workflows-api.service';
import { PROJECT_KEY, ProjectContextService } from './project-context.service';

const sales: WorkflowGroup = { id: 'p1', name: 'Продажі', sortOrder: 1, workflowCount: 2, role: 'editor' };
const finance: WorkflowGroup = { id: 'p2', name: 'Фінанси', sortOrder: 0, workflowCount: 1, role: 'viewer' };

describe('ProjectContextService', () => {
  function setup(groups: WorkflowGroup[] = [sales, finance]) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const service = TestBed.inject(ProjectContextService);
    service.reload();
    TestBed.inject(HttpTestingController).expectOne({ method: 'GET', url: '/api/v1/workflow-groups' }).flush(groups);
    return service;
  }

  beforeEach(() => localStorage.removeItem(PROJECT_KEY));

  it('читає проєкти в порядку груп і пам’ятає вибір у localStorage', () => {
    const service = setup();
    expect(service.projects().map((p) => p.id)).toEqual(['p2', 'p1']);
    expect(service.current()).toBeNull();

    service.select('p1');
    expect(service.current()).toBe('p1');
    expect(service.currentProject()?.name).toBe('Продажі');
    expect(localStorage.getItem(PROJECT_KEY)).toBe('p1');

    service.select(NO_GROUP);
    expect(service.current()).toBe(NO_GROUP);
    expect(service.currentProject()).toBeNull();

    service.select(null);
    expect(localStorage.getItem(PROJECT_KEY)).toBeNull();
  });

  it('збережений проєкт, якого вже немає, — «усі проєкти»', () => {
    localStorage.setItem(PROJECT_KEY, 'gone');
    const service = setup();
    expect(service.current()).toBeNull();
  });

  it('куди створювати ресурс: спільні — лише admin, проєкти — де я editor', () => {
    const service = setup();
    expect(service.targets(false).map((t) => t.id)).toEqual(['p1']);
    expect(service.targets(true).map((t) => t.id)).toEqual([null, 'p1']);
    expect(service.initialTarget(true)).toBeNull();

    service.select('p1');
    expect(service.initialTarget(true)).toBe('p1');
    service.select('p2');
    // У проєкті, де я viewer, створювати не можна — перший доступний.
    expect(service.initialTarget(false)).toBe('p1');
  });
});
