import { HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { DraftTodoItem } from './assistant-api.service';
import { DescribeProcessComponent, DraftResult, progressText } from './describe-process.component';

const URL = '/api/v1/workflows/w1/draft';

function sse(...events: object[]): string {
  return events
    .map((e) => `event: ${(e as { type: string }).type}\ndata: ${JSON.stringify(e)}\n\n`)
    .join('');
}

describe('progressText', () => {
  it('додає «i з n» лише до кроку вузла без результату', () => {
    expect(progressText({ type: 'step', stage: 'node', index: 2, total: 5, text: 'Налаштовую «А»' })).toBe(
      'Налаштовую «А» (2 з 5)',
    );
    expect(
      progressText({ type: 'step', stage: 'node', index: 2, total: 5, ok: true, text: '«А» налаштовано' }),
    ).toBe('«А» налаштовано');
    expect(progressText({ type: 'step', stage: 'plan', text: 'Складаю план процесу' })).toBe(
      'Складаю план процесу',
    );
  });
});

describe('DescribeProcessComponent', () => {
  const empty: WorkflowDefinition = emptyWorkflowDefinition('Заявки');
  const built: WorkflowDefinition = {
    ...empty,
    nodes: [
      {
        id: 'n_a',
        name: 'А',
        type: 'core.manualTrigger',
        typeVersion: 1,
        position: [0, 0] as [number, number],
        parameters: {},
      },
    ],
  };
  const todo: DraftTodoItem[] = [{ nodeId: 'n_a', text: 'А: вкажіть поля' }];

  function create(definition: WorkflowDefinition = empty, savedTodo: DraftTodoItem[] = []) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(DescribeProcessComponent);
    fixture.componentRef.setInput('workflowId', 'w1');
    fixture.componentRef.setInput('definition', definition);
    fixture.componentRef.setInput('savedTodo', savedTodo);
    fixture.detectChanges();
    const drafted: DraftResult[] = [];
    const selected: string[] = [];
    let notConfigured = 0;
    fixture.componentInstance.drafted.subscribe((r) => drafted.push(r));
    fixture.componentInstance.todoSelected.subscribe((id) => selected.push(id));
    fixture.componentInstance.notConfigured.subscribe(() => notConfigured++);
    return {
      fixture,
      host: fixture.nativeElement as HTMLElement,
      http: TestBed.inject(HttpTestingController),
      drafted,
      selected,
      notConfigured: () => notConfigured,
    };
  }

  function type(host: HTMLElement, text: string) {
    const input = host.querySelector('[data-testid="describe-input"]') as HTMLTextAreaElement;
    input.value = text;
    input.dispatchEvent(new Event('input'));
  }

  const click = (host: HTMLElement, id: string) =>
    (host.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement).click();

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('кнопка вимкнена без опису; запит несе опис і поточне визначення; прогрес кроками; сценарій віддається один раз', () => {
    const { fixture, host, http, drafted } = create();
    expect((host.querySelector('[data-testid="describe-run"]') as HTMLButtonElement).disabled).toBe(true);
    expect(host.querySelector('[data-testid="describe-replace-warning"]')).toBeNull();

    type(host, '  працівник подає заявку  ');
    fixture.detectChanges();
    click(host, 'describe-run');
    fixture.detectChanges();

    const request = http.expectOne(URL);
    expect(request.request.body).toEqual({ description: 'працівник подає заявку', definition: empty });
    expect(host.querySelector('[data-testid="describe-stop"]')).not.toBeNull();

    const partial = sse(
      { type: 'step', stage: 'plan', text: 'Складаю план процесу' },
      { type: 'step', stage: 'node', index: 1, total: 2, text: 'Налаштовую «А»' },
      { type: 'definition', definition: built },
    );
    request.event({ type: HttpEventType.DownloadProgress, loaded: 1, partialText: partial });
    fixture.detectChanges();
    const lines = () =>
      Array.from(host.querySelectorAll('[data-testid="describe-progress"] li')).map((l) =>
        l.textContent?.trim(),
      );
    expect(lines()).toEqual(['Складаю план процесу', 'Налаштовую «А» (1 з 2)']);
    expect(drafted).toEqual([]);

    request.flush(partial + sse({ type: 'todo', todo }, { type: 'done' }));
    fixture.detectChanges();

    expect(drafted).toEqual([{ definition: built, todo }]);
    expect(host.querySelector('[data-testid="describe-run"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="describe-todo-item"]')?.textContent?.trim()).toBe(
      'А: вкажіть поля',
    );
  });

  it('непорожній сценарій — попередження «замінить поточні кроки»', () => {
    const { host } = create(built);
    expect(host.querySelector('[data-testid="describe-replace-warning"]')?.textContent).toContain(
      'замінить поточні кроки',
    );
  });

  it('«Зупинити» обриває запит, сценарій не віддається, в прогресі «Зупинено»', () => {
    const { fixture, host, http, drafted } = create();
    type(host, 'процес');
    fixture.detectChanges();
    click(host, 'describe-run');
    fixture.detectChanges();
    const request = http.expectOne(URL);
    request.event({
      type: HttpEventType.DownloadProgress,
      loaded: 1,
      partialText: sse({ type: 'definition', definition: built }),
    });

    click(host, 'describe-stop');
    fixture.detectChanges();

    expect(request.cancelled).toBe(true);
    expect(drafted).toEqual([]);
    expect(host.querySelector('[data-testid="describe-progress"]')?.textContent).toContain('Зупинено');
    expect(host.querySelector('[data-testid="describe-run"]')).not.toBeNull();
  });

  it('пункт «Що доналаштувати» віддає вузол; збережений список видно без нової побудови', () => {
    const { fixture, host, selected } = create(built, todo);
    fixture.detectChanges();
    click(host, 'describe-todo-item');
    expect(selected).toEqual(['n_a']);
  });

  it('помилка побудови — червоний рядок; 409 — сигнал «не налаштований»', () => {
    const { fixture, host, http, drafted, notConfigured } = create();
    type(host, 'процес');
    fixture.detectChanges();
    click(host, 'describe-run');
    http
      .expectOne(URL)
      .flush(sse({ type: 'error', code: 'DRAFT_PLAN_FAILED', text: 'План не склався.' }, { type: 'done' }));
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="describe-error"]')?.textContent).toContain('План не склався.');
    expect(drafted).toEqual([]);

    click(host, 'describe-run');
    http
      .expectOne(URL)
      .flush(JSON.stringify({ code: 'ASSISTANT_NOT_CONFIGURED', detail: 'Помічник не налаштований.' }), {
        status: 409,
        statusText: 'Conflict',
      });
    fixture.detectChanges();
    expect(notConfigured()).toBe(1);
    expect(host.querySelector('[data-testid="describe-error"]')).toBeNull();
  });
});
