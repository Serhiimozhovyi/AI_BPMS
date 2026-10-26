import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { StageActedEvent, StageBoardComponent, StageFailedEvent } from './stage-board.component';
import { ContractStage, ExecutionStages } from './stages-api.service';

const URL = '/api/v1/executions/e1/stages';

function stage(patch: Partial<ContractStage>): ContractStage {
  return {
    id: 'st',
    name: 'Етап',
    position: 0,
    status: 'pending',
    overdue: false,
    enteredAt: null,
    dueAt: null,
    exitedAt: null,
    owner: null,
    violations: [],
    actions: [],
    unmet: [],
    ...patch,
  };
}

const stages: ExecutionStages = {
  execution: { id: 'e1', workflowId: 'w1', versionId: 'v1', mode: 'manual', state: 'waiting', startedAt: null, finishedAt: null, error: null },
  stages: [
    stage({
      id: 'st_request',
      name: 'Заявка',
      status: 'active',
      unmet: [{ ruleId: 'r_limit', mode: 'block', message: 'Сума перевищує ліміт погодження.', to: 'st_approve' }],
      violations: [
        {
          id: 'v1',
          stageId: 'st_request',
          ruleId: 'r_limit',
          mode: 'block',
          status: 'open',
          message: 'Сума перевищує ліміт погодження.',
          reason: null,
          actorUserId: null,
          createdAt: '2026-10-26T09:00:00Z',
          resolvedAt: null,
        },
      ],
      actions: [{ id: 'override:r_limit', kind: 'override', title: 'Сума перевищує ліміт погодження.', reasonRequired: true, ruleId: 'r_limit' }],
    }),
    stage({
      id: 'st_approve',
      name: 'Погодження',
      position: 1,
      status: 'active',
      dueAt: '2026-10-28T09:00:00Z',
      owner: { role: 'viewer' },
      actions: [
        {
          id: 'task:t1',
          kind: 'task',
          title: 'Погодити знижку 15%',
          reasonRequired: false,
          taskId: 't1',
          nodeId: 'n_appr',
          decisions: [
            { key: 'approve', label: 'Погодити' },
            { key: 'reject', label: 'Відхилити' },
          ],
          fields: { type: 'object', properties: { comment: { type: 'string', title: 'Коментар' } } },
        },
      ],
    }),
    stage({ id: 'st_close', name: 'Закриття', position: 2 }),
  ],
};

describe('StageBoardComponent (F7-05)', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(StageBoardComponent);
    fixture.componentRef.setInput('executionId', 'e1');
    const acted: StageActedEvent[] = [];
    const failed: StageFailedEvent[] = [];
    fixture.componentInstance.acted.subscribe((e) => acted.push(e));
    fixture.componentInstance.failed.subscribe((e) => failed.push(e));
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement, acted, failed };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function column(host: HTMLElement, id: string): HTMLElement {
    return host.querySelector(`[data-stage="${id}"]`) as HTMLElement;
  }

  function type(element: Element | null, value: string) {
    const field = element as HTMLInputElement | HTMLTextAreaElement;
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }

  it('етапи колонками: стан, строк, виконавець, невиконані правила, порушення й дії', async () => {
    const { fixture, http, host } = setup();
    http.expectOne(URL).flush(stages);
    await render(fixture);

    expect([...host.querySelectorAll('[data-stage]')].map((c) => c.getAttribute('data-stage'))).toEqual(['st_request', 'st_approve', 'st_close']);
    expect(column(host, 'st_close').getAttribute('data-status')).toBe('pending');
    expect(column(host, 'st_approve').querySelector('[data-testid="stage-due"]')).not.toBeNull();
    expect(column(host, 'st_approve').querySelector('[data-testid="stage-owner"]')?.textContent).toContain('viewer');
    const request = column(host, 'st_request');
    expect(request.querySelector('[data-testid="stage-unmet"]')?.textContent).toContain('ліміт');
    expect(request.querySelector('[data-testid="stage-violation"]')?.textContent).toContain('не виправлено');
    expect(request.querySelector('[data-testid="stage-reason"]')).not.toBeNull();
    const approve = column(host, 'st_approve');
    expect(approve.querySelector('[data-testid="form-field-comment"]')).not.toBeNull();
    expect(approve.querySelector('[data-testid="decision-approve"]')?.textContent).toContain('Погодити');
    expect(approve.querySelector('[data-testid="stage-reason"]')).toBeNull();
  });

  it('рішення задачі: 422 без причини показує поле причини, з причиною — дія виконана і подія acted', async () => {
    const { fixture, http, host, acted, failed } = setup();
    http.expectOne(URL).flush(stages);
    await render(fixture);
    const approve = () => column(host, 'st_approve');

    (approve().querySelector('[data-testid="decision-approve"]') as HTMLButtonElement).click();
    const first = http.expectOne(`${URL}/st_approve/actions/task%3At1`);
    expect(first.request.method).toBe('POST');
    expect(first.request.body).toEqual({ decision: 'approve', data: {}, reason: null });
    first.flush({ code: 'STAGE_RULE_REASON_REQUIRED', detail: 'Немає коментаря до рішення.' }, { status: 422, statusText: 'Unprocessable' });
    await render(fixture);

    expect(approve().querySelector('[data-testid="stage-action-error"]')?.textContent).toContain('Немає коментаря');
    expect(failed).toEqual([{ code: 'STAGE_RULE_REASON_REQUIRED', message: 'Немає коментаря до рішення.' }]);
    // Без причини повторно не відправляє.
    (approve().querySelector('[data-testid="decision-approve"]') as HTMLButtonElement).click();
    http.expectNone(`${URL}/st_approve/actions/task%3At1`);

    type(approve().querySelector('[data-testid="stage-reason"]'), ' Клієнт постійний ');
    (approve().querySelector('[data-testid="decision-approve"]') as HTMLButtonElement).click();
    const second = http.expectOne(`${URL}/st_approve/actions/task%3At1`);
    expect(second.request.body).toEqual({ decision: 'approve', data: {}, reason: 'Клієнт постійний' });
    second.flush({ ...stages, stages: [stages.stages[0], { ...stages.stages[1], status: 'done', actions: [] }, stages.stages[2]] });
    await render(fixture);

    expect(acted).toEqual([{ executionId: 'e1', stageId: 'st_approve', actionId: 'task:t1', kind: 'task', decision: 'approve' }]);
    expect(approve().getAttribute('data-status')).toBe('done');
    expect(approve().querySelector('[data-testid="stage-action"]')).toBeNull();
  });

  it('відхилення правила вимагає причину до запиту', async () => {
    const { fixture, http, host, acted } = setup();
    http.expectOne(URL).flush(stages);
    await render(fixture);
    const request = () => column(host, 'st_request');

    (request().querySelector('[data-testid="stage-override"]') as HTMLButtonElement).click();
    await render(fixture);
    http.expectNone(`${URL}/st_request/actions/override%3Ar_limit`);
    expect(request().querySelector('[data-testid="stage-action-error"]')?.textContent).toContain('причину');

    type(request().querySelector('[data-testid="stage-reason"]'), 'Погоджено директором');
    (request().querySelector('[data-testid="stage-override"]') as HTMLButtonElement).click();
    const req = http.expectOne(`${URL}/st_request/actions/override%3Ar_limit`);
    expect(req.request.body).toEqual({ reason: 'Погоджено директором' });
    req.flush(stages);
    await render(fixture);

    expect(acted.map((a) => a.kind)).toEqual(['override']);
  });

  it('помилка завантаження — повідомлення і подія failed', async () => {
    const { fixture, http, host, failed } = setup();
    http.expectOne(URL).flush({ code: 'NOT_FOUND', detail: 'Виконання не знайдено.' }, { status: 404, statusText: 'Not Found' });
    await render(fixture);

    expect(host.querySelector('[data-testid="stage-board-error"]')?.textContent).toContain('не знайдено');
    expect(failed[0].code).toBe('NOT_FOUND');
  });
});
