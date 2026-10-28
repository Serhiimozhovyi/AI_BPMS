import { provideHttpClient } from '@angular/common/http';
import { HttpEventType } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { parseAssistantStream } from './assistant-api.service';
import { AssistantPanelComponent } from './assistant-panel.component';

const URL = '/api/v1/workflows/w1/assistant';
const EXPLANATION = {
  status: 'none',
  version: null,
  currentVersion: 1,
  text: null,
  error: null,
  updatedAt: null,
  stale: false,
};

function sse(...events: object[]): string {
  return events
    .map((e) => `event: ${(e as { type: string }).type}\ndata: ${JSON.stringify(e)}\n\n`)
    .join('');
}

describe('parseAssistantStream', () => {
  it('розбирає повні блоки й лишає неповний хвіст', () => {
    const text = sse({ type: 'message', text: 'а' }, { type: 'done' }) + 'event: step\ndata: {"ty';
    const { events, consumed } = parseAssistantStream(text);
    expect(events.map((e) => e.type)).toEqual(['message', 'done']);
    expect(text.slice(consumed)).toBe('event: step\ndata: {"ty');
  });
});

describe('AssistantPanelComponent', () => {
  const start: WorkflowDefinition = emptyWorkflowDefinition('Заявки');

  function create() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(AssistantPanelComponent);
    fixture.componentRef.setInput('workflowId', 'w1');
    fixture.componentRef.setInput('definition', start);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/workflows/w1/explanation')
      .flush(EXPLANATION);
    fixture.detectChanges();
    const changes: WorkflowDefinition[] = [];
    fixture.componentInstance.definitionChange.subscribe((d) => changes.push(d));
    return {
      fixture,
      host: fixture.nativeElement as HTMLElement,
      http: TestBed.inject(HttpTestingController),
      changes,
    };
  }

  function type(host: HTMLElement, text: string) {
    const input = host.querySelector('[data-testid="assistant-input"]') as HTMLTextAreaElement;
    input.value = text;
    input.dispatchEvent(new Event('input'));
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('надсилає запит з визначенням і історією; кроки й текст — у розмові; визначення — один раз наприкінці', () => {
    const { fixture, host, http, changes } = create();
    type(host, 'додай Set');
    fixture.detectChanges();
    (host.querySelector('[data-testid="assistant-send"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const request = http.expectOne(URL);
    expect(request.request.body).toEqual({ message: 'додай Set', definition: start, history: [] });
    const first = {
      ...start,
      nodes: [
        {
          id: 'a',
          name: 'A',
          type: 'core.manualTrigger',
          typeVersion: 1,
          position: [0, 0] as [number, number],
          parameters: {},
        },
      ],
    };
    const second = { ...first, name: 'інше' };
    const partial = sse(
      { type: 'step', tool: 'update_workflow', ok: true, text: 'вузлів 1' },
      { type: 'definition', definition: first },
    );
    request.event({ type: HttpEventType.DownloadProgress, loaded: 1, partialText: partial });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="assistant-busy"]')).not.toBeNull();
    expect(changes).toEqual([]);

    request.flush(
      partial +
        sse(
          { type: 'definition', definition: second },
          { type: 'message', text: 'Готово' },
          { type: 'done' },
        ),
    );
    fixture.detectChanges();

    const lines = Array.from(host.querySelectorAll('[data-kind]')).map((l) => [
      l.getAttribute('data-kind'),
      l.textContent?.trim(),
    ]);
    expect(lines).toEqual([
      ['user', 'додай Set'],
      ['step', 'Змінив сценарій: вузлів 1'],
      ['assistant', 'Готово'],
    ]);
    expect(changes).toEqual([second]);
    expect(host.querySelector('[data-testid="assistant-busy"]')).toBeNull();

    // Наступний запит несе історію.
    type(host, 'а тепер Code');
    fixture.detectChanges();
    (host.querySelector('[data-testid="assistant-send"]') as HTMLButtonElement).click();
    expect(http.expectOne(URL).request.body.history).toEqual([
      { role: 'user', text: 'додай Set' },
      { role: 'assistant', text: 'Готово' },
    ]);
  });

  it('модель не вибрана (409) — замість поля підказка про MCP із назвою сценарію; запит ззовні з send надсилається одразу', () => {
    const { fixture, host, http } = create();
    fixture.componentRef.setInput('prompt', { text: 'Поясни вузол', send: true, seq: 1 });
    fixture.detectChanges();

    http
      .expectOne(URL)
      .flush(
        JSON.stringify({ code: 'ASSISTANT_NOT_CONFIGURED', detail: 'Помічник не налаштований.' }),
        { status: 409, statusText: 'Conflict' },
      );
    fixture.detectChanges();

    expect(host.querySelector('[data-kind="error"]')).toBeNull();
    expect(host.querySelector('[data-testid="assistant-input"]')).toBeNull();
    expect(host.querySelector('[data-testid="assistant-mcp"]')?.textContent).toContain('через MCP');
    expect(host.querySelector('[data-testid="assistant-mcp-prompt"]')?.textContent?.trim()).toBe(
      'Поясни сценарій «Заявки» і збережи пояснення в OpenBP',
    );
  });

  it('інша помилка Api — червоний рядок, поле лишається', () => {
    const { fixture, host, http } = create();
    fixture.componentRef.setInput('prompt', { text: 'Поясни вузол', send: true, seq: 1 });
    fixture.detectChanges();

    http
      .expectOne(URL)
      .flush(JSON.stringify({ code: 'ASSISTANT_FAILED', detail: 'Модель недоступна.' }), {
        status: 502,
        statusText: 'Bad Gateway',
      });
    fixture.detectChanges();

    expect(host.querySelector('[data-kind="error"]')?.textContent).toContain('Модель недоступна.');
    expect(host.querySelector('[data-testid="assistant-input"]')).not.toBeNull();
  });

  it('«Описати процес» (F3-104): запит ззовні з mode=describe показує опис замість розмови; перемикач повертає розмову', () => {
    const { fixture, host } = create();
    const hidden = (id: string) =>
      (host.querySelector(id === 'describe-process' ? 'app-describe-process' : `[data-testid="${id}"]`) as HTMLElement).hidden;
    expect(hidden('describe-process')).toBe(true);

    fixture.componentRef.setInput('prompt', { text: '', send: false, seq: 1, mode: 'describe' });
    fixture.detectChanges();
    expect(hidden('describe-process')).toBe(false);
    expect(hidden('assistant-log')).toBe(true);

    (host.querySelector('[data-testid="assistant-mode"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(hidden('describe-process')).toBe(true);
    expect(hidden('assistant-log')).toBe(false);
  });
});
