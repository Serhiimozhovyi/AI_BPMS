import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { parseAssistantStream } from './assistant-api.service';
import { AssistantPanelComponent } from './assistant-panel.component';

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
    return {
      fixture,
      host: fixture.nativeElement as HTMLElement,
      http: TestBed.inject(HttpTestingController),
    };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('F3-117: у панелі редактора лише пояснення процесу й «Описати процес»; розмови тут немає, є підказка про загальну панель', () => {
    const { host } = create();
    expect(host.querySelector('app-explanation-card')).not.toBeNull();
    expect(host.querySelector('[data-testid="describe-process"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="assistant-input"]')).toBeNull();
    expect(host.querySelector('[data-testid="assistant-send"]')).toBeNull();
    expect(host.querySelector('[data-testid="assistant-log"]')).toBeNull();
    expect(host.querySelector('[data-testid="assistant-chat-hint"]')?.textContent).toContain('загальній панелі');
  });

  it('«Закрити» віддає подію closed', () => {
    const { fixture, host } = create();
    let closed = 0;
    fixture.componentInstance.closed.subscribe(() => closed++);
    (host.querySelector('[data-testid="assistant-close"]') as HTMLButtonElement).click();
    expect(closed).toBe(1);
  });

  it('модель не вибрана (409 при побудові з опису) — замість «Описати процес» підказка про MCP із назвою сценарію', () => {
    const { fixture, host, http } = create();
    const input = host.querySelector('[data-testid="describe-input"]') as HTMLTextAreaElement;
    input.value = 'працівник подає заявку';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (host.querySelector('[data-testid="describe-run"]') as HTMLButtonElement).click();
    http
      .expectOne('/api/v1/workflows/w1/draft')
      .flush(JSON.stringify({ code: 'ASSISTANT_NOT_CONFIGURED', detail: 'Помічник не налаштований.' }), {
        status: 409,
        statusText: 'Conflict',
      });
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="describe-process"]')).toBeNull();
    expect(host.querySelector('[data-testid="assistant-mcp"]')?.textContent).toContain('через MCP');
    expect(host.querySelector('[data-testid="assistant-mcp-prompt"]')?.textContent?.trim()).toBe(
      'Поясни сценарій «Заявки» і збережи пояснення в AI_BPMS',
    );
  });
});
