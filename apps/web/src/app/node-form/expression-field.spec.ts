import { TestBed } from '@angular/core/testing';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { ExpressionContext } from './expression-suggest';
import { CodeEditorFactory, CodeEditorHandle, CodeEditorOptions, MONACO_LOADER } from './monaco-loader';
import { parameterFields } from './node-form';
import { NodePanelComponent } from './node-panel.component';
import { CODE_FIELD, VALUE_FIELD } from './schema-fields';
import { CODE_DESCRIPTOR, SET_DESCRIPTOR } from './test-schemas';

/** Фейковий Monaco: запам'ятовує створені редактори і дає «набрати» текст. */
class FakeEditor implements CodeEditorHandle {
  private listeners: (() => void)[] = [];
  disposed = false;

  constructor(
    readonly host: HTMLElement,
    readonly options: CodeEditorOptions,
    private value = options.value,
  ) {}

  getValue(): string {
    return this.value;
  }

  setValue(value: string): void {
    this.value = value;
  }

  /** Користувач змінив текст у редакторі. */
  type(value: string): void {
    this.value = value;
    for (const l of this.listeners) l();
  }

  onDidChangeModelContent(listener: () => void): { dispose(): void } {
    this.listeners.push(listener);
    return { dispose: () => (this.listeners = this.listeners.filter((l) => l !== listener)) };
  }

  dispose(): void {
    this.disposed = true;
  }
}

describe('Поле виразу і Monaco (F2-19)', () => {
  let editors: FakeEditor[];
  let loader: () => Promise<CodeEditorFactory>;

  beforeEach(() => {
    editors = [];
    loader = () =>
      Promise.resolve((host: HTMLElement, options: CodeEditorOptions) => {
        const editor = new FakeEditor(host, options);
        editors.push(editor);
        return editor;
      });
    TestBed.configureTestingModule({ providers: [{ provide: MONACO_LOADER, useFactory: () => () => loader() }] });
  });

  async function setup(node: WorkflowNode, descriptor: NodeTypeInfo, context?: ExpressionContext) {
    const fixture = TestBed.createComponent(NodePanelComponent);
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('descriptor', descriptor);
    if (context) fixture.componentRef.setInput('expressionContext', context);
    const emitted: WorkflowNode[] = [];
    fixture.componentInstance.nodeChange.subscribe((n) => {
      emitted.push(n);
      fixture.componentRef.setInput('node', n);
    });
    await render(fixture);
    return { fixture, emitted, host: fixture.nativeElement as HTMLElement, last: () => emitted[emitted.length - 1] };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function el<T extends HTMLElement>(host: ParentNode, testId: string): T {
    const found = host.querySelector(`[data-testid="${testId}"]`);
    if (!found) throw new Error(`немає [data-testid="${testId}"]`);
    return found as T;
  }

  /** Набрати текст із курсором у кінці. */
  function type(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    input.focus();
    input.value = value;
    input.setSelectionRange(value.length, value.length);
    input.dispatchEvent(new Event('input'));
  }

  function options(): string[] {
    return Array.from(document.querySelectorAll('mat-option')).map((o) => o.querySelector('.expr-suggest__label')?.textContent?.trim() ?? '');
  }

  const setNode: WorkflowNode = {
    id: 'n_set',
    name: 'Поля',
    type: 'core.set',
    typeVersion: 1,
    position: [0, 0],
    parameters: { fields: [{ name: 'customer', value: '' }] },
  };

  const context: ExpressionContext = {
    nodes: [{ name: 'Нова заявка', sample: { body: { text: 'Привіт' } } }],
    input: { customer: 'Петренко', total: 10 },
  };

  it('у полі виразу підказки змінних, вузлів і полів output; вибір вставляє текст у вузол', async () => {
    const { fixture, host, last } = await setup(setNode, SET_DESCRIPTOR, context);
    const value = el<HTMLTextAreaElement>(host, 'field-value');

    type(value, '{{ $');
    await render(fixture);
    expect(options()).toEqual(['$json', '$binary', '$input', '$node', '$now', '$env', '$execution']);

    type(value, '{{ $node[');
    await render(fixture);
    expect(options()).toEqual(['Нова заявка']);

    type(value, '{{ $json.cu');
    await render(fixture);
    expect(options()).toEqual(['customer']);
    el<HTMLElement>(document, 'suggest-customer').click();
    await render(fixture);

    expect(value.value).toBe('{{ $json.customer');
    expect(value.selectionStart).toBe('{{ $json.customer'.length);
    expect(last().parameters['fields']).toEqual([{ name: 'customer', value: '{{ $json.customer' }]);
  });

  it('поза {{ }} і без контексту підказок полів немає', async () => {
    const { fixture, host } = await setup(setNode, SET_DESCRIPTOR);
    const value = el<HTMLTextAreaElement>(host, 'field-value');
    type(value, '$json.');
    await render(fixture);
    expect(options()).toEqual([]);
    type(value, '{{ $json.');
    await render(fixture);
    expect(options()).toEqual([]);
  });

  it('контекст оновлюється без перебудови форми', async () => {
    const { fixture, host } = await setup(setNode, SET_DESCRIPTOR);
    const value = el<HTMLTextAreaElement>(host, 'field-value');
    fixture.componentRef.setInput('expressionContext', context);
    await render(fixture);
    expect(el<HTMLTextAreaElement>(host, 'field-value')).toBe(value);
    type(value, '{{ $json.t');
    await render(fixture);
    expect(options()).toEqual(['total']);
  });

  const codeNode: WorkflowNode = {
    id: 'n_code',
    name: 'Код',
    type: 'core.code',
    typeVersion: 1,
    position: [0, 0],
    parameters: { code: 'return $json;', mode: 'eachItem' },
  };

  it('core.code: поле code — Monaco, решта — звичайні поля', () => {
    const fields = parameterFields('core.code', CODE_DESCRIPTOR, null);
    expect(fields.map((f) => [f.key, f.type])).toEqual([
      ['code', CODE_FIELD],
      ['mode', VALUE_FIELD],
    ]);
  });

  it('core.code: код з вузла в Monaco, зміни в редакторі — у вузол', async () => {
    const { fixture, host, last } = await setup(codeNode, CODE_DESCRIPTOR);
    expect(editors).toHaveLength(1);
    expect(editors[0].options).toMatchObject({ value: 'return $json;', language: 'javascript' });
    expect(el<HTMLElement>(host, 'code-code').dataset['status']).toBe('ready');
    expect(el<HTMLElement>(host, 'code-code').contains(editors[0].host)).toBe(true);

    editors[0].type('return { ...$json, ok: true };');
    await render(fixture);
    expect(last().parameters).toEqual({ code: 'return { ...$json, ok: true };', mode: 'eachItem' });

    editors[0].type('');
    await render(fixture);
    expect(last().parameters).toEqual({ mode: 'eachItem' });
    expect(el<HTMLElement>(host, 'error-code').textContent).toContain("Обов'язкове");
  });

  it('core.code: зовнішня зміна вузла (undo) — новий редактор з новим кодом, старий звільнено', async () => {
    const { fixture } = await setup(codeNode, CODE_DESCRIPTOR);
    fixture.componentRef.setInput('node', { ...codeNode, parameters: { code: 'return [];' } });
    await render(fixture);
    expect(editors).toHaveLength(2);
    expect(editors[0].disposed).toBe(true);
    expect(editors[1].getValue()).toBe('return [];');
  });

  it('core.code: Monaco не завантажився — текстове поле', async () => {
    loader = () => Promise.reject(new Error('chunk load failed'));
    const { fixture, host, last } = await setup(codeNode, CODE_DESCRIPTOR);
    expect(el<HTMLElement>(host, 'code-fallback')).toBeTruthy();
    const text = el<HTMLTextAreaElement>(host, 'field-code');
    expect(text.value).toBe('return $json;');
    text.value = 'return [$json];';
    text.dispatchEvent(new Event('input'));
    await render(fixture);
    expect(last().parameters['code']).toBe('return [$json];');
  });
});
