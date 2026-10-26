import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { EVENT_SOURCE_FACTORY, ExecutionDetail, ExecutionStep, ExecutionSummary } from '../../executions/executions-api.service';
import { FakeEventSource } from '../../executions/fake-event-source';
import { ExecutionsPage, applyDetailEvent, dayBoundary } from './executions.page';

const failed: ExecutionSummary = {
  id: 'e1',
  workflowId: 'w1',
  versionId: 'v1',
  mode: 'trigger',
  state: 'error',
  startedAt: '2026-10-23T10:00:00Z',
  finishedAt: '2026-10-23T10:00:02Z',
  error: 'Вузол «1С» завершився помилкою.',
};
const running: ExecutionSummary = { ...failed, id: 'e2', mode: 'manual', state: 'running', finishedAt: null, error: null };

function step(id: string, nodeId: string, status: ExecutionStep['status'] = 'success', message?: string): ExecutionStep {
  return {
    id,
    nodeId,
    runIndex: 0,
    status,
    input: [[{ json: { in: nodeId } }]],
    output: status === 'success' ? [[{ json: { out: nodeId } }]] : [],
    error: message ? { message } : null,
    startedAt: '2026-10-23T10:00:00Z',
    finishedAt: '2026-10-23T10:00:01Z',
  };
}

const failedDetail: ExecutionDetail = {
  summary: failed,
  steps: [step('s1', 'n_webhook'), step('s2', 'n_1c', 'error', 'HTTP 500 від 1С')],
};

const workflow = {
  id: 'w1',
  name: 'Telegram → 1С',
  active: true,
  version: 3,
  updatedAt: '2026-10-23T09:00:00Z',
  definition: {
    formatVersion: 1,
    name: 'Telegram → 1С',
    settings: {},
    nodes: [
      { id: 'n_webhook', name: 'Нова заявка' },
      { id: 'n_1c', name: '1С: замовлення' },
    ],
    connections: [],
    pinData: {},
  },
};

describe('ExecutionsPage', () => {
  async function setup(url = '/executions', items: ExecutionSummary[] = [failed, running], total = items.length) {
    FakeEventSource.last = null;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'executions', component: ExecutionsPage }]),
        { provide: EVENT_SOURCE_FACTORY, useValue: (u: string) => new FakeEventSource(u) },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl(url, ExecutionsPage);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/workflows').flush({ items: [workflow], page: 1, pageSize: 200, total: 1 });
    const list = http.expectOne((r) => r.url === '/api/v1/executions');
    list.flush({ items, page: 1, pageSize: 50, total });
    await render(harness);
    return { harness, page, http, list, host: harness.routeNativeElement as HTMLElement };
  }

  async function render(harness: RouterTestingHarness) {
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  function expectList(http: HttpTestingController): TestRequest {
    return http.expectOne((r) => r.url === '/api/v1/executions');
  }

  async function openDetail(setupResult: Awaited<ReturnType<typeof setup>>, detail: ExecutionDetail) {
    const { harness, http, host } = setupResult;
    host.querySelector<HTMLButtonElement>(`[data-execution="${detail.summary.id}"] button`)!.click();
    http.expectOne(`/api/v1/executions/${detail.summary.id}`).flush(detail);
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    await render(harness);
  }

  it('список з назвою сценарію, режимом і станом українською', async () => {
    const { host, list } = await setup();
    expect(list.request.params.get('page')).toBe('1');
    expect(list.request.params.has('workflowId')).toBe(false);
    const rows = host.querySelectorAll('[data-execution]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Telegram → 1С');
    expect(rows[0].textContent).toContain('Тригер');
    expect(rows[0].querySelector('[data-testid="state"]')?.textContent).toContain('Помилка');
    expect(rows[1].querySelector('[data-testid="state"]')?.textContent).toContain('Виконується');
  });

  it('?workflowId= з канвасу — журнал одного сценарію', async () => {
    const { list, host } = await setup('/executions?workflowId=w1');
    expect(list.request.params.get('workflowId')).toBe('w1');
    expect(host.querySelector<HTMLSelectElement>('[data-testid="filter-workflow"]')!.value).toBe('w1');
  });

  it('фільтри стану й періоду перезавантажують список з першої сторінки', async () => {
    const { harness, http, host, page } = await setup();
    const status = host.querySelector<HTMLSelectElement>('[data-testid="filter-status"]')!;
    status.value = 'error';
    status.dispatchEvent(new Event('change'));
    let req = expectList(http);
    expect(req.request.params.get('status')).toBe('error');
    req.flush({ items: [failed], page: 1, pageSize: 50, total: 1 });
    await render(harness);
    expect(host.querySelectorAll('[data-execution]')).toHaveLength(1);

    page.setFrom('2026-10-01');
    req = expectList(http);
    expect(req.request.params.get('from')).toBe(dayBoundary('2026-10-01'));
    expect(req.request.params.get('status')).toBe('error');
    req.flush({ items: [], page: 1, pageSize: 50, total: 0 });
    await render(harness);
    expect(host.querySelector('[data-testid="empty"]')).not.toBeNull();
  });

  it('ключ кореляції: колонка, клік по ключу і поле фільтра запитують журнал з correlationKey', async () => {
    const keyed: ExecutionSummary = { ...failed, correlationKey: 'угода-42' };
    const { harness, http, host } = await setup('/executions', [keyed, running]);
    const cells = host.querySelectorAll('[data-testid="correlation-key"]');
    expect(cells[0].textContent).toContain('угода-42');
    expect(cells[1].textContent).toContain('—');

    cells[0].querySelector<HTMLButtonElement>('button')!.click();
    let req = expectList(http);
    expect(req.request.params.get('correlationKey')).toBe('угода-42');
    expect(req.request.params.get('page')).toBe('1');
    req.flush({ items: [keyed], page: 1, pageSize: 50, total: 1 });
    await render(harness);
    expect(host.querySelector<HTMLInputElement>('[data-testid="filter-correlation-key"]')!.value).toBe('угода-42');

    const input = host.querySelector<HTMLInputElement>('[data-testid="filter-correlation-key"]')!;
    input.value = '  ';
    input.dispatchEvent(new Event('change'));
    req = expectList(http);
    expect(req.request.params.has('correlationKey')).toBe(false);
    req.flush({ items: [keyed, running], page: 1, pageSize: 50, total: 2 });
  });

  it('очікування: стан «Очікує», час пробудження з заголовка або кроку, «Скасувати» доступне', async () => {
    const waiting: ExecutionSummary = { ...running, id: 'e3', state: 'waiting', resumeAt: '2026-11-03T07:00:00Z', waitingNodeId: 'n_1c' };
    const ctx = await setup('/executions', [waiting]);
    const { host } = ctx;
    expect(host.querySelector('[data-execution="e3"] [data-testid="state"]')?.textContent).toContain('Очікує');

    await openDetail(ctx, { summary: waiting, steps: [step('s1', 'n_webhook'), { ...step('s2', 'n_1c', 'waiting'), resumeAt: '2026-11-03T07:00:00Z' }] });
    expect(host.querySelector('[data-testid="detail-state"]')?.textContent).toContain('Очікує');
    expect(host.querySelector('[data-testid="detail-resume-at"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="cancel-button"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="retry-button"]')).toBeNull();
    expect(ctx.page.waitingUntil({ summary: { ...waiting, resumeAt: null }, steps: [{ ...step('s2', 'n_1c', 'waiting'), resumeAt: '2026-11-04T07:00:00Z' }] })).toBe(
      '2026-11-04T07:00:00Z',
    );
  });

  it('пагінація: «Далі» запитує наступну сторінку', async () => {
    const { harness, http, host } = await setup('/executions', [failed], 120);
    expect(host.querySelector('[data-testid="page-info"]')?.textContent).toContain('1 з 3');
    host.querySelector<HTMLButtonElement>('[data-testid="next-page"]')!.click();
    const req = expectList(http);
    expect(req.request.params.get('page')).toBe('2');
    req.flush({ items: [running], page: 2, pageSize: 50, total: 120 });
    await render(harness);
    expect(host.querySelector('[data-testid="page-info"]')?.textContent).toContain('2 з 3');
  });

  it('помилка завантаження показується', async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: 'executions', component: ExecutionsPage }])],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/executions', ExecutionsPage);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/workflows').flush({ items: [], page: 1, pageSize: 200, total: 0 });
    expectList(http).flush(null, { status: 403, statusText: 'Forbidden' });
    await render(harness);
    expect((harness.routeNativeElement as HTMLElement).querySelector('[data-testid="page-error"]')?.textContent).toContain('прав');
  });

  it('деталі: кроки з назвами вузлів, помилка, input/output кроку з помилкою; «Повторити» для фінального', async () => {
    const s = await setup();
    await openDetail(s, failedDetail);
    const detail = s.host.querySelector('[data-testid="execution-detail"]')!;
    expect(detail.querySelector('[data-testid="detail-error"]')?.textContent).toContain('завершився помилкою');
    const steps = detail.querySelectorAll('[data-step]');
    expect([...steps].map((li) => li.textContent?.trim())).toEqual([expect.stringContaining('Нова заявка'), expect.stringContaining('1С: замовлення')]);
    // Відкритий крок — той, що впав.
    expect(detail.querySelector('[data-testid="node-run-error"]')?.textContent).toContain('HTTP 500 від 1С');
    expect(detail.querySelector('[data-testid="node-run-input"]')?.textContent).toContain('n_1c');
    expect(detail.querySelector('[data-testid="cancel-button"]')).toBeNull();
    expect(detail.querySelector('[data-testid="retry-button"]')).not.toBeNull();

    detail.querySelector<HTMLButtonElement>('[data-step="n_webhook"] button')!.click();
    await render(s.harness);
    expect(detail.querySelector('[data-testid="node-run-output"]')?.textContent).toContain('"out": "n_webhook"');
  });

  it('етапи (F7-02): «Етап N», назва і стан кожного етапу, прострочений позначено, строк — для активного', async () => {
    const s = await setup();
    await openDetail(s, {
      ...failedDetail,
      stages: [
        { stageId: 'st_intake', name: 'Прийом заявки', position: 0, status: 'done', overdue: false, enteredAt: '2026-10-23T10:00:00Z', dueAt: null, exitedAt: '2026-10-23T10:00:01Z' },
        { stageId: 'st_approval', name: 'Погодження', position: 1, status: 'active', overdue: true, enteredAt: '2026-10-23T10:00:01Z', dueAt: '2026-10-23T12:00:00Z', exitedAt: null },
        { stageId: 'st_close', name: 'Закриття', position: 2, status: 'skipped', overdue: false, enteredAt: null, dueAt: null, exitedAt: null },
      ],
    });
    const strip = s.host.querySelector('[data-testid="stage-strip"]')!;
    const stages = [...strip.querySelectorAll('[data-stage]')];
    expect(stages.map((li) => [li.getAttribute('data-stage'), li.getAttribute('data-status'), li.getAttribute('data-overdue')])).toEqual([
      ['st_intake', 'done', 'false'],
      ['st_approval', 'active', 'true'],
      ['st_close', 'skipped', 'false'],
    ]);
    expect(stages[0].textContent).toContain('Етап 1');
    expect(stages[0].textContent).toContain('Прийом заявки');
    expect(stages[0].querySelector('[data-testid="stage-state"]')?.textContent).toBe('Пройдено');
    expect(stages[1].querySelector('[data-testid="stage-state"]')?.textContent).toBe('Прострочено');
    expect(stages[1].querySelector('[data-testid="stage-due"]')).not.toBeNull();
    expect(stages[2].querySelector('[data-testid="stage-state"]')?.textContent).toBe('Не знадобився');
    expect(stages[2].classList).toContain('stage--skipped');
  });

  it('сценарій без етапів — смуги етапів немає', async () => {
    const s = await setup();
    await openDetail(s, { ...failedDetail, stages: [] });
    expect(s.host.querySelector('[data-testid="stage-strip"]')).toBeNull();
  });

  it('«Повторити» запускає повтор, оновлює журнал і відкриває нове виконання', async () => {
    const s = await setup();
    await openDetail(s, failedDetail);
    s.host.querySelector<HTMLButtonElement>('[data-testid="retry-button"]')!.click();
    const retried: ExecutionSummary = { ...failed, id: 'e3', mode: 'retry', state: 'queued', startedAt: null, finishedAt: null, error: null };
    s.http.expectOne({ method: 'POST', url: '/api/v1/executions/e1/retry' }).flush(retried);
    expectList(s.http).flush({ items: [retried, failed, running], page: 1, pageSize: 50, total: 3 });
    s.http.expectOne('/api/v1/executions/e3').flush({ summary: retried, steps: [] });
    s.http.expectOne('/api/v1/workflows/w1').flush(workflow);
    await render(s.harness);

    expect(s.host.querySelectorAll('[data-execution]')).toHaveLength(3);
    const detail = s.host.querySelector('[data-testid="execution-detail"]')!;
    expect(detail.getAttribute('data-state')).toBe('queued');
    expect(detail.textContent).toContain('Повтор');
    expect(FakeEventSource.last?.url).toBe('/api/v1/executions/e3/events');
  });

  it('«Продовжити з вузла»: незмінені items — без тіла, виправлені — нові items на вхід вузла, що впав', async () => {
    const s = await setup();
    await openDetail(s, failedDetail);
    s.host.querySelector<HTMLButtonElement>('[data-testid="resume-button"]')!.click();
    await render(s.harness);
    const panel = s.host.querySelector('[data-testid="resume-panel"]')!;
    expect(panel.textContent).toContain('1С: замовлення');
    const editor = panel.querySelector<HTMLTextAreaElement>('[data-testid="resume-items"]')!;
    expect(JSON.parse(editor.value)).toEqual([{ json: { in: 'n_1c' } }]);

    // Некоректний JSON — помилка, кнопка вимкнена.
    editor.value = '[{ json';
    editor.dispatchEvent(new Event('input'));
    await render(s.harness);
    expect(panel.querySelector('[data-testid="resume-items-error"]')?.textContent).toContain('JSON');
    expect(panel.querySelector<HTMLButtonElement>('[data-testid="resume-submit"]')!.disabled).toBe(true);

    editor.value = '[{ "json": { "in": "виправлено" } }]';
    editor.dispatchEvent(new Event('input'));
    await render(s.harness);
    panel.querySelector<HTMLButtonElement>('[data-testid="resume-submit"]')!.click();
    const request = s.http.expectOne({ method: 'POST', url: '/api/v1/executions/e1/resume' });
    expect(request.request.body).toEqual({ items: [{ json: { in: 'виправлено' } }] });
    const resumed: ExecutionSummary = { ...failed, id: 'e4', mode: 'resume', state: 'queued', startedAt: null, finishedAt: null, error: null, resumedFromExecutionId: 'e1' };
    request.flush(resumed);
    expectList(s.http).flush({ items: [resumed, failed, running], page: 1, pageSize: 50, total: 3 });
    s.http.expectOne('/api/v1/executions/e4').flush({ summary: resumed, steps: [failedDetail.steps[0]] });
    s.http.expectOne('/api/v1/workflows/w1').flush(workflow);
    await render(s.harness);

    const detail = s.host.querySelector('[data-testid="execution-detail"]')!;
    expect(detail.textContent).toContain('Продовження');
    expect(detail.querySelector('[data-testid="resumed-from"]')?.textContent).toContain('e1');
    expect(detail.querySelector('[data-testid="resume-button"]')).toBeNull();
  });

  it('підсценарії: деталі показують запущені дочірні виконання й посилання на батьківське', async () => {
    const s = await setup();
    const child: ExecutionSummary = { ...failed, id: 'e9', workflowId: 'w2', mode: 'sub', state: 'success', error: null, parentExecutionId: 'e1', parentNodeId: 'n_1c' };
    await openDetail(s, { ...failedDetail, children: [child] });

    const detail = s.host.querySelector('[data-testid="execution-detail"]')!;
    const children = detail.querySelectorAll('[data-testid="child-execution"]');
    expect(children).toHaveLength(1);
    expect(children[0].textContent).toContain('1С: замовлення');
    expect(children[0].textContent).toContain('e9');
    expect(detail.querySelector('[data-testid="parent-execution"]')).toBeNull();

    children[0].querySelector('button')!.click();
    s.http.expectOne('/api/v1/executions/e9').flush({ summary: child, steps: [], children: [] });
    s.http.expectOne('/api/v1/workflows/w2').flush({ ...workflow, id: 'w2', name: 'Перевірка складу' });
    await render(s.harness);

    const childDetail = s.host.querySelector('[data-testid="execution-detail"]')!;
    expect(childDetail.textContent).toContain('Підсценарій');
    expect(childDetail.querySelector('[data-testid="parent-execution"]')?.textContent).toContain('e1');
    expect(childDetail.querySelector('[data-testid="child-executions"]')).toBeNull();
  });

  it('«Продовжити з вузла» без змін надсилає порожнє тіло; для успішного виконання кнопки немає', async () => {
    const s = await setup();
    await openDetail(s, failedDetail);
    s.host.querySelector<HTMLButtonElement>('[data-testid="resume-button"]')!.click();
    await render(s.harness);
    s.host.querySelector<HTMLButtonElement>('[data-testid="resume-submit"]')!.click();
    const request = s.http.expectOne({ method: 'POST', url: '/api/v1/executions/e1/resume' });
    expect(request.request.body).toEqual({});
    request.flush({ summary: failed }, { status: 403, statusText: 'Forbidden' });
    await render(s.harness);
    expect(s.host.querySelector('[data-testid="action-error"]')).not.toBeNull();

    await openDetail(s, { summary: { ...failed, state: 'success', error: null }, steps: [] });
    expect(s.host.querySelector('[data-testid="resume-button"]')).toBeNull();
  });

  it('виконання, що йде: живе оновлення кроків і стану через SSE; «Скасувати»', async () => {
    const s = await setup();
    await openDetail(s, { summary: running, steps: [] });
    const source = FakeEventSource.last!;
    expect(source.url).toBe('/api/v1/executions/e2/events');
    const detail = s.host.querySelector('[data-testid="execution-detail"]')!;
    expect(detail.querySelector('[data-testid="retry-button"]')).toBeNull();

    source.emit('step', step('s9', 'n_webhook'));
    await render(s.harness);
    expect(detail.querySelectorAll('[data-step]')).toHaveLength(1);
    expect(detail.querySelector('[data-testid="node-run-output"]')?.textContent).toContain('n_webhook');

    detail.querySelector<HTMLButtonElement>('[data-testid="cancel-button"]')!.click();
    s.http.expectOne({ method: 'POST', url: '/api/v1/executions/e2/cancel' }).flush({ ...running, state: 'cancelling' });
    await render(s.harness);
    expect(detail.querySelector('[data-testid="detail-state"]')?.textContent).toContain('Скасовується');
    expect(detail.querySelector('[data-testid="cancel-button"]')).toBeNull();

    source.emit('status', { executionId: 'e2', state: 'cancelled', error: null });
    const final = { ...running, state: 'cancelled' as const, finishedAt: '2026-10-23T10:00:05Z' };
    s.http.expectOne('/api/v1/executions/e2').flush({ summary: final, steps: [step('s9', 'n_webhook')] });
    await render(s.harness);
    expect(detail.getAttribute('data-state')).toBe('cancelled');
    expect(s.host.querySelector('[data-execution="e2"] [data-testid="state"]')?.textContent).toContain('Скасовано');
    expect(detail.querySelector('[data-testid="retry-button"]')).not.toBeNull();
    expect(source.closed).toBe(true);
  });

  it('помилка дії показується', async () => {
    const s = await setup();
    await openDetail(s, failedDetail);
    s.host.querySelector<HTMLButtonElement>('[data-testid="retry-button"]')!.click();
    s.http.expectOne('/api/v1/executions/e1/retry').flush(null, { status: 403, statusText: 'Forbidden' });
    await render(s.harness);
    expect(s.host.querySelector('[data-testid="action-error"]')?.textContent).toContain('прав');
  });

  describe('«Бізнес | Технічний» (F3-69)', () => {
    afterEach(() => localStorage.removeItem('openbp.editor.view'));

    it('бізнес-вигляд показує виконання на схемі: пройдений крок і збій; клік на крок — його дані', async () => {
      const ctx = await setup();
      await openDetail(ctx, failedDetail);
      const { harness, http, host } = ctx;
      host.querySelector<HTMLButtonElement>('[data-testid="view-business"]')!.click();
      http.expectOne('/api/v1/nodes').flush([]);
      http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [], page: 1, pageSize: 200, total: 0 });
      await render(harness);
      const steps = [...host.querySelectorAll('[data-testid="business-step"]')].map((s) => [s.getAttribute('data-step'), s.getAttribute('data-status')]);
      expect(steps).toEqual([
        ['n_webhook', 'done'],
        ['n_1c', 'error'],
      ]);
      expect(host.querySelector<HTMLElement>('[data-testid="steps"]')!.hidden).toBe(true);
      host.querySelector('[data-step="n_webhook"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      await render(harness);
      expect(ctx.page.selectedStep()?.id).toBe('s1');

      host.querySelector<HTMLButtonElement>('[data-testid="view-technical"]')!.click();
      await render(harness);
      expect(host.querySelector('app-business-view')).toBeNull();
      expect(host.querySelector<HTMLElement>('[data-testid="steps"]')!.hidden).toBe(false);
    });
  });
});

describe('applyDetailEvent', () => {
  it('крок додається, той самий id замінюється; перший крок переводить queued → running', () => {
    const queued: ExecutionDetail = { summary: { ...running, state: 'queued' }, steps: [] };
    const one = applyDetailEvent(queued, { kind: 'step', step: step('s1', 'a') });
    expect(one.summary.state).toBe('running');
    const replaced = applyDetailEvent(one, { kind: 'step', step: step('s1', 'a', 'error', 'x') });
    expect(replaced.steps).toHaveLength(1);
    expect(replaced.steps[0].status).toBe('error');
  });

  it('статус чужого виконання ігнорується', () => {
    const detail: ExecutionDetail = { summary: running, steps: [] };
    expect(applyDetailEvent(detail, { kind: 'status', executionId: 'other', state: 'success', error: null })).toBe(detail);
  });
});
