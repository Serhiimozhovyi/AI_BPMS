import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { TaskDetail } from '../../tasks/tasks-api.service';
import { TaskPage } from './task.page';

describe('TaskPage (F6-02)', () => {
  const detail: TaskDetail = {
    task: {
      id: 't1',
      executionId: 'e1',
      workflowId: 'w1',
      workflowName: 'Знижки',
      nodeId: 'n_appr',
      title: 'Погодити знижку 15%',
      status: 'open',
      assignee: { email: 'boss@x.ua' },
      escalated: false,
      canComplete: true,
      createdAt: '2026-10-26T09:00:00Z',
    },
    description: 'Клієнт ТОВ «Зерно»',
    decisions: [
      { key: 'approve', label: 'Погодити' },
      { key: 'reject', label: 'Відхилити' },
    ],
    fields: { type: 'object', required: ['comment'], properties: { comment: { type: 'string', title: 'Коментар' } } },
  };

  function setup(id = 'tok_abc') {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id }) } } },
      ],
    });
    const fixture = TestBed.createComponent(TaskPage);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('картка за токеном: опис, поля за схемою і кнопка на кожне рішення', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/tasks/tok_abc').flush(detail);
    await render(fixture);

    expect(host.querySelector('[data-testid="task-title"]')?.textContent).toContain('Погодити знижку 15%');
    expect(host.querySelector('[data-testid="task-description"]')?.textContent).toContain('Зерно');
    expect(host.querySelector('[data-testid="form-field-comment"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="decision-approve"]')?.textContent).toContain('Погодити');
    expect(host.querySelector('[data-testid="decision-reject"]')?.textContent).toContain('Відхилити');
  });

  it('рішення з даними форми; без обов’язкового поля — не надсилається', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/tasks/tok_abc').flush(detail);
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="decision-approve"]')!.click();
    await render(fixture);
    http.expectNone('/api/v1/tasks/t1/complete');
    expect(host.querySelector('[data-testid="task-submit-error"]')).not.toBeNull();

    const input = host.querySelector<HTMLInputElement>('[data-testid="form-field-comment"]')!;
    input.value = 'ок';
    input.dispatchEvent(new Event('input'));
    host.querySelector<HTMLButtonElement>('[data-testid="decision-approve"]')!.click();
    const request = http.expectOne('/api/v1/tasks/t1/complete');
    expect(request.request.body).toEqual({ decision: 'approve', data: { comment: 'ок' } });
    request.flush({ ...detail, task: { ...detail.task, status: 'done', canComplete: false, decision: 'approve' }, completedBy: { id: 'u1', email: 'boss@x.ua' } });
    await render(fixture);

    expect(host.querySelector('[data-testid="task-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="task-decided"]')?.textContent).toContain('Погодити');
    expect(host.querySelector('[data-testid="task-status"]')?.textContent).toContain('Вирішена');
  });

  it('чужа задача — 403 з поясненням, форми немає', async () => {
    const { fixture, http, host } = setup('t9');
    http.expectOne('/api/v1/tasks/t9').flush({ code: 'TASK_FORBIDDEN', detail: '…' }, { status: 403, statusText: 'Forbidden' });
    await render(fixture);

    expect(host.querySelector('[data-testid="task-error"]')?.textContent).toContain('іншого виконавця');
    expect(host.querySelector('[data-testid="task-form"]')).toBeNull();
  });

  it('помилки полів з Api — під полями', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/tasks/tok_abc').flush(detail);
    await render(fixture);
    const input = host.querySelector<HTMLInputElement>('[data-testid="form-field-comment"]')!;
    input.value = 'x';
    input.dispatchEvent(new Event('input'));
    host.querySelector<HTMLButtonElement>('[data-testid="decision-reject"]')!.click();
    http.expectOne('/api/v1/tasks/t1/complete').flush(
      { code: 'FORM_INVALID', errors: [{ field: 'comment', message: 'Закоротко.' }] },
      { status: 400, statusText: 'Bad Request' },
    );
    await render(fixture);

    expect(host.textContent).toContain('Закоротко.');
    expect(host.querySelector('[data-testid="task-submit-error"]')).not.toBeNull();
  });
});
