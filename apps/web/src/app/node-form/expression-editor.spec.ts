import { TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';
import { EvaluateExpressionRequest, ExpressionPreview, WorkflowNode, WorkflowsApiService } from '../workflows/workflows-api.service';
import { ExpressionEditorService, PREVIEW_DEBOUNCE_MS } from './expression-editor-dialog.component';
import { ExpressionContext } from './expression-suggest';
import { EXPRESSION_FUNCTIONS, expressionSegments, expressionSyntaxError, filterFunctions, insertSnippet } from './expression-tools';
import { CodeEditorHandle, CodeEditorOptions, MONACO_LOADER } from './monaco-loader';
import { NodePanelComponent } from './node-panel.component';
import { SET_DESCRIPTOR } from './test-schemas';

describe('редактор виразів (F3-56)', () => {
  describe('розбір і перевірка', () => {
    it('частини {{ … }}: найближчий }}, незакритий {{ — літерал', () => {
      expect(expressionSegments('Клієнт {{ $json.name }}, сума {{ $json.total }}!')).toEqual([
        { text: 'Клієнт ', expression: false },
        { text: '{{ $json.name }}', expression: true },
        { text: ', сума ', expression: false },
        { text: '{{ $json.total }}', expression: true },
        { text: '!', expression: false },
      ]);
      expect(expressionSegments('a {{ b')).toEqual([{ text: 'a {{ b', expression: false }]);
      expect(expressionSegments('')).toEqual([]);
    });

    it('синтаксична помилка: розбір без виконання, порожній і незакритий вираз', () => {
      expect(expressionSyntaxError('{{ $json.a.toUpperCase() }} і {{ { a: 1 } }}')).toBeNull();
      expect(expressionSyntaxError('{{ $json. }}')).toContain('Синтаксична помилка');
      expect(expressionSyntaxError('{{   }}')).toBe('Порожній вираз {{ }}.');
      expect(expressionSyntaxError('{{ $json.a')).toContain('Незакритий');
      expect(expressionSyntaxError('просто текст')).toBeNull();
      // Код не виконується.
      expect(expressionSyntaxError('{{ (() => { throw new Error("x"); })() }}')).toBeNull();
    });

    it('вставка функції: у {{ }} — як є, поза — з маркерами; курсор за фрагментом', () => {
      expect(insertSnippet('{{ $json.name }}', 13, 13, '.trim()')).toEqual({ text: '{{ $json.name.trim() }}', cursor: 20 });
      expect(insertSnippet('Дата: ', 6, 6, '$now.toISOString()')).toEqual({ text: 'Дата: {{ $now.toISOString() }}', cursor: 27 });
      expect(insertSnippet('{{ a }} xx', 8, 10, 'Date.now()')).toEqual({ text: '{{ a }} {{ Date.now() }}', cursor: 21 });
    });

    it('каталог: групи дата, рядки, числа, масиви; пошук за словами', () => {
      expect(EXPRESSION_FUNCTIONS.map((g) => g.id)).toEqual(['date', 'string', 'number', 'array']);
      expect(filterFunctions(EXPRESSION_FUNCTIONS, 'великими').flatMap((g) => g.functions.map((f) => f.label))).toEqual(['.toUpperCase()']);
      expect(filterFunctions(EXPRESSION_FUNCTIONS, 'немає такого')).toEqual([]);
    });
  });

  describe('поле і діалог', () => {
    class FakeEditor implements CodeEditorHandle {
      private listeners: (() => void)[] = [];
      cursor: number;
      constructor(readonly options: CodeEditorOptions, private value = options.value) {
        this.cursor = value.length;
      }
      getValue = () => this.value;
      setValue(value: string) {
        this.value = value;
        for (const l of this.listeners) l();
      }
      type(value: string) {
        this.value = value;
        this.cursor = value.length;
        for (const l of this.listeners) l();
      }
      onDidChangeModelContent(listener: () => void) {
        this.listeners.push(listener);
        return { dispose: () => undefined };
      }
      dispose = (): void => undefined;
      getCursor = () => this.cursor;
      setCursor(offset: number) {
        this.cursor = offset;
      }
    }

    let editors: FakeEditor[];
    let requests: { id: string; body: EvaluateExpressionRequest }[];
    let answer: (body: EvaluateExpressionRequest) => ExpressionPreview;

    const node: WorkflowNode = { id: 'n_set', name: 'Поля', type: 'core.set', typeVersion: 1, position: [0, 0], parameters: { fields: [{ name: 'customer', value: 'Клієнт {{ $json.name }}' }] } };
    const context: ExpressionContext = { nodes: [{ name: 'Старт', sample: { name: 'Олена' } }, { name: 'Без даних' }], input: { name: 'Олена' } };

    beforeEach(() => {
      editors = [];
      requests = [];
      answer = (body) => ({ value: body.expression.replace('{{ $json.name }}', 'Олена'), error: null });
      const api: Partial<WorkflowsApiService> = {
        evaluateExpression: (id: string, body: EvaluateExpressionRequest): Observable<ExpressionPreview> => {
          requests.push({ id, body });
          return of(answer(body));
        },
      };
      TestBed.configureTestingModule({
        providers: [
          ExpressionEditorService,
          { provide: WorkflowsApiService, useValue: api },
          {
            provide: MONACO_LOADER,
            useValue: () =>
              Promise.resolve((_host: HTMLElement, options: CodeEditorOptions) => {
                const editor = new FakeEditor(options);
                editors.push(editor);
                return editor;
              }),
          },
        ],
      });
      TestBed.inject(ExpressionEditorService).workflowId = 'wf_1';
    });

    afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = '')));

    async function setup(value = 'Клієнт {{ $json.name }}') {
      const fixture = TestBed.createComponent(NodePanelComponent);
      fixture.componentRef.setInput('node', { ...node, parameters: { fields: [{ name: 'customer', value }] } });
      fixture.componentRef.setInput('descriptor', SET_DESCRIPTOR);
      fixture.componentRef.setInput('expressionContext', context);
      const emitted: WorkflowNode[] = [];
      fixture.componentInstance.nodeChange.subscribe((n) => {
        emitted.push(n);
        fixture.componentRef.setInput('node', n);
      });
      const render = async () => {
        fixture.detectChanges();
        await fixture.whenStable();
        fixture.detectChanges();
      };
      await render();
      return { fixture, host: fixture.nativeElement as HTMLElement, render, last: () => emitted.at(-1) };
    }

    const el = <T extends Element>(root: ParentNode, testId: string): T => {
      const found = root.querySelector(`[data-testid="${testId}"]`);
      if (!found) throw new Error(`немає [data-testid="${testId}"]`);
      return found as T;
    };
    const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

    it('у полі {{ … }} підсвічуються, помилка синтаксису — під полем', async () => {
      const { host, render } = await setup();
      const marks = [...el(host, 'highlight-value').querySelectorAll('mark')].map((m) => m.textContent);
      expect(marks).toEqual(['{{ $json.name }}']);
      expect(host.querySelector('[data-testid="expr-error-value"]')).toBeNull();

      const field = el<HTMLTextAreaElement>(host, 'field-value');
      field.value = 'Сума {{ $json.total * }}';
      field.dispatchEvent(new Event('input'));
      await render();
      expect([...el(host, 'highlight-value').querySelectorAll('mark')].map((m) => m.textContent)).toEqual(['{{ $json.total * }}']);
      expect(el(host, 'expr-error-value').textContent).toContain('Синтаксична помилка');

      field.value = 'без виразу';
      field.dispatchEvent(new Event('input'));
      await render();
      expect(host.querySelector('[data-testid="highlight-value"]')).toBeNull();
    });

    it('«розгорнути»: Monaco з підказками, результат на першому item через Api, вставка функції, «Застосувати»', async () => {
      const { host, render, last } = await setup();
      el<HTMLButtonElement>(host, 'expand-value').click();
      await render();
      await wait(0);
      await render();

      expect(editors).toHaveLength(1);
      expect(editors[0].options).toMatchObject({ value: 'Клієнт {{ $json.name }}', language: 'openbp-expression' });
      expect(editors[0].options.complete?.('{{ $json.na', 11)?.items.map((s) => s.label)).toEqual(['name']);

      await wait(PREVIEW_DEBOUNCE_MS + 50);
      await render();
      expect(requests).toEqual([
        { id: 'wf_1', body: { expression: 'Клієнт {{ $json.name }}', nodeId: 'n_set', item: { json: { name: 'Олена' } }, nodes: { Старт: [{ json: { name: 'Олена' } }] } } },
      ]);
      expect(el(document, 'expr-result').textContent).toBe('Клієнт Олена');
      expect(el(document, 'expr-result-type').textContent).toBe('string');

      // Помилка виразу — текстом помилки; запит — лише після паузи.
      answer = () => ({ value: null, error: 'Вираз перевищив ліміт часу.' });
      editors[0].type('{{ $json.name');
      editors[0].type('{{ $json.name }}');
      await wait(PREVIEW_DEBOUNCE_MS + 50);
      await render();
      expect(requests).toHaveLength(2);
      expect(el(document, 'expr-result-error').textContent).toBe('Вираз перевищив ліміт часу.');

      // Функція каталогу — в позицію курсора (усередині {{ }}).
      editors[0].setCursor(13);
      el<HTMLButtonElement>(document, 'expr-fn-search').dispatchEvent(new Event('input'));
      const upper = [...document.querySelectorAll<HTMLButtonElement>('[data-testid="expr-fn"]')].find((b) => b.dataset['insert'] === '.toUpperCase()');
      upper!.click();
      await render();
      expect(editors[0].getValue()).toBe('{{ $json.name.toUpperCase() }}');
      expect(editors[0].cursor).toBe(27);

      el<HTMLButtonElement>(document, 'expr-apply').click();
      await render();
      await wait(300);
      await render();
      expect(last()?.parameters['fields']).toEqual([{ name: 'customer', value: '{{ $json.name.toUpperCase() }}' }]);
      expect(el<HTMLTextAreaElement>(host, 'field-value').value).toBe('{{ $json.name.toUpperCase() }}');
    });

    it('«Скасувати» не змінює поле; текст без виразу — результат без запиту', async () => {
      const { host, render, last } = await setup('просто текст');
      el<HTMLButtonElement>(host, 'expand-value').click();
      await render();
      await wait(PREVIEW_DEBOUNCE_MS + 50);
      await render();
      expect(requests).toEqual([]);
      expect(el(document, 'expr-result').textContent).toBe('просто текст');
      editors[0].type('інше');
      el<HTMLButtonElement>(document, 'expr-cancel').click();
      await render();
      await wait(300);
      expect(last()).toBeUndefined();
    });
  });
});
