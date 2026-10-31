import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { TaskSummary } from '../../tasks/tasks-api.service';
import { TasksPage } from './tasks.page';
import { chooseOption } from '../../shared/select-search/select-testing';

describe('TasksPage (F6-02)', () => {
  const task: TaskSummary = {
    id: 't1',
    executionId: 'e1',
    workflowId: 'w1',
    workflowName: 'Знижки',
    nodeId: 'n_appr',
    title: 'Погодити знижку 15%',
    status: 'open',
    assignee: { role: 'editor' },
    escalated: true,
    canComplete: true,
    createdAt: '2026-10-26T09:00:00Z',
    dueAt: '2026-10-27T09:00:00Z',
  };

  function setup(role: 'admin' | 'viewer') {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'u@x.ua', role });
    const fixture = TestBed.createComponent(TasksPage);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  it('показує мої відкриті задачі з посиланням на картку', () => {
    const { fixture, http, host } = setup('viewer');
    http.expectOne((r) => r.url === '/api/v1/tasks' && r.params.get('scope') === 'mine' && r.params.get('status') === 'open')
      .flush({ items: [task], page: 1, pageSize: 50, total: 1 });
    fixture.detectChanges();

    expect(host.querySelectorAll('[data-testid="task-row"]').length).toBe(1);
    expect(host.querySelector('[data-testid="task-link"]')?.getAttribute('href')).toBe('/tasks/t1');
    expect(host.textContent).toContain('Роль: editor');
    expect(host.textContent).toContain('ескалація');
    expect(host.querySelector('[data-testid="task-status"]')?.textContent).toContain('Відкрита');
    expect(host.querySelector('[data-testid="filter-scope"]')).toBeNull();
  });

  it('admin перемикає на всі задачі, фільтр статусу і порожній список', () => {
    const { fixture, http, host } = setup('admin');
    http.expectOne((r) => r.url === '/api/v1/tasks').flush({ items: [], page: 1, pageSize: 50, total: 0 });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="empty"]')).not.toBeNull();

    chooseOption(host.querySelector('[data-testid="filter-scope"]')!, 'all');
    http.expectOne((r) => r.params.get('scope') === 'all' && r.params.get('status') === 'open').flush({ items: [task], page: 1, pageSize: 50, total: 1 });

    chooseOption(host.querySelector('[data-testid="filter-status"]')!, '');
    http.expectOne((r) => r.params.get('scope') === 'all' && !r.params.has('status')).flush({ items: [task], page: 1, pageSize: 50, total: 1 });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="total"]')?.textContent).toContain('1');
  });

  it('помилка Api — повідомлення', () => {
    const { fixture, http, host } = setup('viewer');
    http.expectOne((r) => r.url === '/api/v1/tasks').flush({ detail: 'Потрібна роль admin або вища.' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('admin');
  });
});
