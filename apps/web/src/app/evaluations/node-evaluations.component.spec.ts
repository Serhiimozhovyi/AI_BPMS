import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EvaluationDetail, EvaluationRun, caseTrends, parseJsonField } from './evaluations-api.service';
import { NodeEvaluationsComponent, fromDraft, toDraft } from './node-evaluations.component';

const run = (id: string, version: number, passed: boolean[]): EvaluationRun => ({
  id,
  evaluationId: 'e1',
  versionId: `v${version}`,
  version,
  startedAt: '2026-10-24T10:00:00Z',
  finishedAt: '2026-10-24T10:00:01Z',
  passed: passed.filter(Boolean).length,
  total: passed.length,
  results: passed.map((p, i) => ({
    caseId: `c${i + 1}`,
    name: `Кейс ${i + 1}`,
    passed: p,
    actual: { text: p ? 'ok' : 'bad' },
    reason: p ? null : 'Очікувалось {"text":"ok"}',
    error: null,
    durationMs: 12,
    tokens: null,
    executionId: `x${i}`,
  })),
});

const detail = (runs: EvaluationRun[] = []): EvaluationDetail => ({
  summary: { id: 'e1', workflowId: 'w1', nodeId: 'n_llm', name: 'Оцінка «Модель»', caseCount: 2, lastRun: runs[0] ?? null },
  cases: [
    { id: 'c1', name: 'добре', input: [{ json: { text: 'good' } }], expected: { text: 'ok' }, check: 'exact', config: {} },
    { id: 'c2', name: 'вираз', input: [{ json: { text: 'x' } }], expected: null, check: 'expression', config: { expression: '$json.text === "ok"' } },
  ],
  runs,
});

describe('NodeEvaluationsComponent', () => {
  function setup(readonly = false) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(NodeEvaluationsComponent);
    fixture.componentRef.setInput('workflowId', 'w1');
    fixture.componentRef.setInput('nodeId', 'n_llm');
    fixture.componentRef.setInput('readonly', readonly);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const host = fixture.nativeElement as HTMLElement;
    const click = (testId: string) => {
      (host.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    return { fixture, http, host, click };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('без оцінок — «Створити оцінку»: POST і порожній кейс у формі', () => {
    const { fixture, http, host, click } = setup();
    http.expectOne('/api/v1/workflows/w1/evaluations?nodeId=n_llm').flush([]);
    fixture.detectChanges();

    click('evaluation-create');
    const create = http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/evaluations' });
    expect(create.request.body).toEqual({ nodeId: 'n_llm', name: null, cases: [] });
    create.flush({ ...detail(), cases: [] });
    fixture.detectChanges();

    expect(host.querySelectorAll('[data-case]').length).toBe(1);
    expect((host.querySelector('[data-testid="evaluation-save"]') as HTMLButtonElement).disabled).toBe(false);
    expect((host.querySelector('[data-testid="evaluation-run"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('кейси, збереження з PUT і запуск: рахунок, рядки результатів і зміна проти попереднього прогону', () => {
    const { fixture, http, host, click } = setup();
    http.expectOne('/api/v1/workflows/w1/evaluations?nodeId=n_llm').flush([detail().summary]);
    http.expectOne('/api/v1/evaluations/e1').flush(detail([run('r1', 3, [true, true])]));
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="evaluation-name"]')?.textContent).toContain('Оцінка «Модель»');
    expect((host.querySelector('[data-case="1"] [data-testid="case-expression"]') as HTMLInputElement).value).toBe('$json.text === "ok"');
    expect(host.querySelector('[data-testid="evaluation-score"]')?.textContent).toBe('2/2');

    const name = host.querySelector('[data-case="0"] [data-testid="case-name"]') as HTMLInputElement;
    name.value = 'дуже добре';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect((host.querySelector('[data-testid="evaluation-run"]') as HTMLButtonElement).disabled).toBe(true);
    click('evaluation-save');
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/evaluations/e1' });
    expect(put.request.body.cases[0]).toEqual({ name: 'дуже добре', input: [{ json: { text: 'good' } }], expected: { text: 'ok' }, check: 'exact', config: {} });
    expect(put.request.body.cases[1].config).toEqual({ expression: '$json.text === "ok"' });
    put.flush(detail([run('r1', 3, [true, true])]));
    fixture.detectChanges();

    click('evaluation-run');
    http.expectOne({ method: 'POST', url: '/api/v1/evaluations/e1/run' }).flush(run('r2', 4, [true, false]));
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="evaluation-score"]')?.textContent).toBe('1/2');
    expect(host.querySelector('[data-testid="evaluation-previous"]')?.textContent).toContain('2/2');
    expect(Array.from(host.querySelectorAll('[data-result]')).map((r) => r.getAttribute('data-result'))).toEqual(['pass', 'fail']);
    expect(host.querySelector('[data-result="fail"] [data-testid="case-trend"]')?.textContent).toContain('зламалось');
    expect(host.querySelector('[data-result="fail"]')?.textContent).toContain('Очікувалось');
  });

  it('помилка прогону — повідомлення; некоректний JSON — без запиту', () => {
    const { fixture, http, host, click } = setup();
    http.expectOne('/api/v1/workflows/w1/evaluations?nodeId=n_llm').flush([detail().summary]);
    http.expectOne('/api/v1/evaluations/e1').flush(detail());
    fixture.detectChanges();

    click('evaluation-run');
    http.expectOne('/api/v1/evaluations/e1/run').flush({ detail: 'Потрібна роль editor або вища.' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="evaluations-error"]')?.textContent).toContain('editor');

    const input = host.querySelector('[data-case="0"] [data-testid="case-input"]') as HTMLTextAreaElement;
    input.value = '{';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    click('evaluation-save');
    expect(host.querySelector('[data-testid="evaluations-error"]')?.textContent).toContain('некоректний JSON');
  });

  it('лише читання: без кнопок змін і запуску', () => {
    const { fixture, http, host } = setup(true);
    http.expectOne('/api/v1/workflows/w1/evaluations?nodeId=n_llm').flush([]);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="evaluation-create"]')).toBeNull();
    expect(host.textContent).toContain('Оцінок для вузла ще немає');
  });
});

describe('evaluations helpers', () => {
  it('caseTrends: виправлено, зламалось, без змін, новий', () => {
    const before = run('a', 1, [true, false, true]);
    const after = { ...run('b', 2, [false, true, true, true]) };
    expect(caseTrends(after, before)).toEqual({ c1: 'broken', c2: 'fixed', c3: 'same', c4: 'new' });
    expect(caseTrends(after, null)).toEqual({ c1: 'new', c2: 'new', c3: 'new', c4: 'new' });
  });

  it('toDraft/fromDraft: один item — об\'єкт, масив — items; налаштування за способом перевірки', () => {
    const draft = toDraft({ name: 'x', input: [{ json: { a: 1 } }], expected: 'ok', check: 'jsonPath', config: { path: 'text' } });
    expect(draft.input).toBe('{\n  "a": 1\n}');
    expect(fromDraft(draft, 0)).toEqual({ name: 'x', input: [{ json: { a: 1 } }], expected: 'ok', check: 'jsonPath', config: { path: 'text' } });
    expect(fromDraft({ ...draft, name: '', input: '[{"json":{"b":2}},{"json":{}}]', check: 'llm', credentialId: ' c1 ', model: 'm', criteria: '' }, 2)).toEqual({
      name: null,
      input: [{ json: { b: 2 } }, { json: {} }],
      expected: 'ok',
      check: 'llm',
      config: { credentialId: 'c1', model: 'm' },
    });
    expect(() => parseJsonField('{', 'Вхід')).toThrow(/Вхід/);
    expect(parseJsonField('  ', 'Вхід')).toBeNull();
  });
});
