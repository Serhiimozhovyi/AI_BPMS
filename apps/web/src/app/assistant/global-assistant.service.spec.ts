import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { contextFromUrl, GlobalAssistantService } from './global-assistant.service';

const ID = '11111111-2222-3333-4444-555555555555';
const OTHER = '99999999-2222-3333-4444-555555555555';
const BASE = '/api/v1/assistant/conversations';

const PROPOSAL = {
  id: 'p1',
  tool: 'create_table',
  arguments: { name: 'Заявки' },
  plan: null,
  status: 'pending' as const,
  result: null,
  createdAt: '',
  decidedAt: null,
};

const sse = (...events: object[]) =>
  events.map((e) => `event: ${(e as { type: string }).type}\ndata: ${JSON.stringify(e)}\n\n`).join('');

describe('contextFromUrl', () => {
  it('бере сторінку без запиту й ідентифікатори з адреси', () => {
    expect(contextFromUrl('/workflows')).toEqual({ page: '/workflows' });
    expect(contextFromUrl(`/workflows/${ID}?x=1#a`)).toEqual({ page: `/workflows/${ID}`, workflowId: ID });
    expect(contextFromUrl(`/executions/${ID}`)).toEqual({ page: `/executions/${ID}`, executionId: ID });
    expect(contextFromUrl(`/projects/${OTHER}/tables`)).toEqual({ page: `/projects/${OTHER}/tables`, projectId: OTHER });
  });

  it('не вважає ідентифікатором те, що ним не є', () => {
    expect(contextFromUrl('/workflows/new')).toEqual({ page: '/workflows/new' });
    expect(contextFromUrl('/')).toEqual({ page: '/' });
  });
});

describe('GlobalAssistantService', () => {
  async function setup(url = '/') {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([{ path: '**', children: [] }])],
    });
    await TestBed.inject(Router).navigateByUrl(url);
    return { service: TestBed.inject(GlobalAssistantService), http: TestBed.inject(HttpTestingController) };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('перше повідомлення створює розмову й несе контекст сторінки та вибраний вузол; кроки й текст — у рядках', async () => {
    const { service, http } = await setup(`/workflows/${ID}`);
    service.setNode('n_set');
    service.draft.set('Поясни вузол');
    service.send();
    expect(service.busy()).toBe(true);
    expect(service.draft()).toBe('');

    http.expectOne({ method: 'POST', url: BASE }).flush({ id: 'c1', title: 'Нова', messages: [], proposals: [] });
    const request = http.expectOne(`${BASE}/c1/messages`);
    expect(request.request.body).toEqual({
      message: 'Поясни вузол',
      context: { page: `/workflows/${ID}`, workflowId: ID, nodeId: 'n_set' },
    });
    request.flush(
      sse(
        { type: 'step', tool: 'get_workflow', ok: true, text: 'виконано' },
        { type: 'message', text: 'Вузол задає поля.' },
        { type: 'done' },
      ),
    );
    http.expectOne({ method: 'GET', url: BASE }).flush([]);

    expect(service.currentId()).toBe('c1');
    expect(service.busy()).toBe(false);
    expect(service.lines().map((l) => [l.kind, l.text])).toEqual([
      ['user', 'Поясни вузол'],
      ['step', 'Прочитав сценарій: виконано'],
      ['assistant', 'Вузол задає поля.'],
    ]);
  });

  it('друге повідомлення йде в ту саму розмову; пропозиція — картка в розмові й у сховищі', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.draft.set('Створи таблицю');
    service.send();
    http.expectOne(`${BASE}/c1/messages`).flush(sse({ type: 'proposal', proposal: PROPOSAL }, { type: 'done' }));
    http.expectOne({ method: 'GET', url: BASE }).flush([]);
    expect(service.proposals()).toEqual([PROPOSAL]);
    expect(service.lines().at(-1)).toEqual({ kind: 'proposal', text: 'create_table', proposalId: 'p1' });
  });

  it('рішення: «Застосувати» оновлює стан картки відповіддю Api; кнопки блокуються, поки йде запит', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.proposals.set([PROPOSAL]);
    service.decide('p1', true);
    service.decide('p1', true);
    expect(service.deciding()).toEqual(['p1']);
    const request = http.expectOne({ method: 'POST', url: `${BASE}/c1/proposals/p1` });
    expect(request.request.body).toEqual({ apply: true });
    request.flush({ ...PROPOSAL, status: 'applied', result: '{"applied":true}' });
    expect(service.proposals()[0].status).toBe('applied');
    expect(service.deciding()).toEqual([]);
  });

  it('«Скасувати» шле apply=false', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.proposals.set([PROPOSAL]);
    service.decide('p1', false);
    const request = http.expectOne({ method: 'POST', url: `${BASE}/c1/proposals/p1` });
    expect(request.request.body).toEqual({ apply: false });
    request.flush({ ...PROPOSAL, status: 'rejected' });
    expect(service.proposals()[0].status).toBe('rejected');
  });

  it('пропозицію вже вирішено (409) — розмова перечитується замість червоного рядка', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.proposals.set([PROPOSAL]);
    service.decide('p1', true);
    http
      .expectOne(`${BASE}/c1/proposals/p1`)
      .flush({ code: 'ASSISTANT_PROPOSAL_DECIDED', detail: 'Цю пропозицію вже вирішено.' }, { status: 409, statusText: 'Conflict' });
    http.expectOne({ method: 'GET', url: `${BASE}/c1` }).flush({
      id: 'c1',
      title: 'Т',
      userId: 'u',
      createdAt: '',
      updatedAt: '',
      messages: [],
      proposals: [{ ...PROPOSAL, status: 'applied' }],
    });
    expect(service.proposals()[0].status).toBe('applied');
    expect(service.lines().some((l) => l.kind === 'error')).toBe(false);
  });

  it('інша помилка рішення — червоний рядок', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.proposals.set([PROPOSAL]);
    service.decide('p1', true);
    http
      .expectOne(`${BASE}/c1/proposals/p1`)
      .flush({ code: 'FORBIDDEN', detail: 'Писати в розмову може лише її автор.' }, { status: 403, statusText: 'Forbidden' });
    expect(service.lines().find((l) => l.kind === 'error')?.text).toContain('лише її автор');
    expect(service.deciding()).toEqual([]);
  });

  it('модель не вибрана (409) — прапорець замість червоного рядка', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.draft.set('Привіт');
    service.send();
    http
      .expectOne(`${BASE}/c1/messages`)
      .flush(JSON.stringify({ code: 'ASSISTANT_NOT_CONFIGURED', detail: 'Помічник не налаштований.' }), { status: 409, statusText: 'Conflict' });
    http.expectOne({ method: 'GET', url: BASE }).flush([]);
    expect(service.notConfigured()).toBe(true);
    expect(service.lines().some((l) => l.kind === 'error')).toBe(false);
  });

  it('інша помилка Api — червоний рядок', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.draft.set('Привіт');
    service.send();
    http
      .expectOne(`${BASE}/c1/messages`)
      .flush(JSON.stringify({ code: 'ASSISTANT_PROVIDER_ERROR', detail: 'Модель недоступна.' }), { status: 502, statusText: 'Bad Gateway' });
    http.expectOne({ method: 'GET', url: BASE }).flush([]);
    expect(service.lines().find((l) => l.kind === 'error')?.text).toContain('Модель недоступна.');
    expect(service.busy()).toBe(false);
  });

  it('історія: розмова відкривається з репліками, кроками й приміткою; нова розмова очищає чат; видалення поточної теж', async () => {
    const { service, http } = await setup();
    service.showHistory();
    http.expectOne({ method: 'GET', url: BASE }).flush([{ id: 'c1', title: 'Таблиця заявок', userId: 'u', messageCount: 3, createdAt: '', updatedAt: '' }]);
    expect(service.conversations().map((c) => c.title)).toEqual(['Таблиця заявок']);

    service.select('c1');
    http.expectOne(`${BASE}/c1`).flush({
      id: 'c1',
      title: 'Таблиця заявок',
      userId: 'u',
      createdAt: '',
      updatedAt: '',
      messages: [
        { role: 'user', text: 'Зроби таблицю', tool: null, ok: null, at: '' },
        { role: 'tool', text: 'план змін — чекає рішення', tool: 'create_table', ok: true, at: '', proposalId: 'p1' },
        { role: 'note', text: 'Користувач скасував пропозицію', tool: null, ok: null, at: '' },
      ],
      proposals: [PROPOSAL],
    });
    expect(service.currentId()).toBe('c1');
    expect(service.historyOpen()).toBe(false);
    // Крок з пропозицією дає картку на цьому місці; примітка про рішення призначена моделі й не показується.
    expect(service.lines().map((l) => [l.kind, l.text, l.proposalId])).toEqual([
      ['user', 'Зроби таблицю', undefined],
      ['step', 'Створення таблиці: план змін — чекає рішення', undefined],
      ['proposal', 'create_table', 'p1'],
    ]);
    expect(service.proposals()).toEqual([PROPOSAL]);

    service.remove('c1');
    http.expectOne({ method: 'DELETE', url: `${BASE}/c1` }).flush(null);
    http.expectOne({ method: 'GET', url: BASE }).flush([]);
    expect(service.currentId()).toBeNull();
    expect(service.lines()).toEqual([]);
    expect(service.historyOpen()).toBe(true);

    service.newConversation();
    expect(service.historyOpen()).toBe(false);
  });

  it('show() з текстом і send=false лише підставляє його в поле', async () => {
    const { service } = await setup();
    service.show({ text: 'Напиши вираз: ', send: false, nodeId: 'n1' });
    expect(service.open()).toBe(true);
    expect(service.draft()).toBe('Напиши вираз: ');
    expect(service.busy()).toBe(false);
  });
});
