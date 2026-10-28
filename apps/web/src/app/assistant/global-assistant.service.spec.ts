import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { contextFromUrl, GlobalAssistantService } from './global-assistant.service';

const ID = '11111111-2222-3333-4444-555555555555';
const OTHER = '99999999-2222-3333-4444-555555555555';
const BASE = '/api/v1/assistant/conversations';

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

  it('друге повідомлення йде в ту саму розмову; пропозиція зберігається для карток', async () => {
    const { service, http } = await setup();
    service.currentId.set('c1');
    service.draft.set('Створи таблицю');
    service.send();
    const proposal = { id: 'p1', tool: 'create_table', arguments: {}, plan: null, status: 'pending', result: null, createdAt: '', decidedAt: null };
    http.expectOne(`${BASE}/c1/messages`).flush(sse({ type: 'proposal', proposal }, { type: 'done' }));
    http.expectOne({ method: 'GET', url: BASE }).flush([]);
    expect(service.proposals()).toEqual([proposal]);
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
        { role: 'tool', text: 'план змін — чекає рішення', tool: 'create_table', ok: true, at: '' },
        { role: 'note', text: 'Користувач скасував пропозицію', tool: null, ok: null, at: '' },
      ],
      proposals: [],
    });
    expect(service.currentId()).toBe('c1');
    expect(service.historyOpen()).toBe(false);
    expect(service.lines().map((l) => [l.kind, l.text])).toEqual([
      ['user', 'Зроби таблицю'],
      ['step', 'Створення таблиці: план змін — чекає рішення'],
      ['note', 'Користувач скасував пропозицію'],
    ]);

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
