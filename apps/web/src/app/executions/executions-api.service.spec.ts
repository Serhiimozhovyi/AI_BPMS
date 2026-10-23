import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EVENT_SOURCE_FACTORY, ExecutionEvent, ExecutionStep, ExecutionsApiService } from './executions-api.service';
import { FakeEventSource } from './fake-event-source';

const step: ExecutionStep = {
  id: 's1',
  nodeId: 'n1',
  runIndex: 0,
  status: 'success',
  input: [],
  output: [[{ json: { a: 1 } }]],
  error: null,
  startedAt: '2026-10-23T10:00:00Z',
  finishedAt: '2026-10-23T10:00:01Z',
};

describe('ExecutionsApiService', () => {
  let api: ExecutionsApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    FakeEventSource.last = null;
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: EVENT_SOURCE_FACTORY, useValue: (url: string) => new FakeEventSource(url) }],
    });
    api = TestBed.inject(ExecutionsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('start → POST /workflows/{id}/executions; get і cancel — /executions/{id}', () => {
    let state = '';
    api.start('w 1').subscribe((s) => (state = s.state));
    const req = http.expectOne('/api/v1/workflows/w%201/executions');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ id: 'e1', workflowId: 'w 1', versionId: 'v', mode: 'manual', state: 'queued', startedAt: null, finishedAt: null, error: null });
    expect(state).toBe('queued');

    api.get('e1').subscribe();
    http.expectOne('/api/v1/executions/e1').flush({ summary: {}, steps: [] });
    api.cancel('e1').subscribe();
    expect(http.expectOne('/api/v1/executions/e1/cancel').request.method).toBe('POST');
  });

  it('events: кроки і стани з SSE; фінальний стан закриває потік', () => {
    const events: ExecutionEvent[] = [];
    let completed = false;
    api.events('e1').subscribe({ next: (e) => events.push(e), complete: () => (completed = true) });
    const source = FakeEventSource.last!;
    expect(source.url).toBe('/api/v1/executions/e1/events');

    source.emit('status', { executionId: 'e1', state: 'running', error: null });
    source.emit('step', step);
    expect(completed).toBe(false);
    source.emit('status', { executionId: 'e1', state: 'success', error: null });

    expect(events).toEqual([
      { kind: 'status', executionId: 'e1', state: 'running', error: null },
      { kind: 'step', step },
      { kind: 'status', executionId: 'e1', state: 'success', error: null },
    ]);
    expect(completed).toBe(true);
    expect(source.closed).toBe(true);
  });

  it('events: тимчасовий обрив не ламає потік (EventSource перепідключиться), закрите з’єднання — помилка; відписка закриває', () => {
    let error: unknown = null;
    const sub = api.events('e1').subscribe({ error: (e: unknown) => (error = e) });
    const source = FakeEventSource.last!;
    source.fail(0);
    expect(error).toBeNull();
    source.fail(2);
    expect(error).toBeInstanceOf(Error);

    api.events('e2').subscribe().unsubscribe();
    expect(FakeEventSource.last!.closed).toBe(true);
    sub.unsubscribe();
  });
});
