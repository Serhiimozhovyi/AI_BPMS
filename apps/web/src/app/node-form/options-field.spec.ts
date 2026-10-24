import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { McpToolInfo, ToolCallResult } from '../connectors/connectors-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { NodePanelComponent } from './node-panel.component';
import { MCP_TOOL_DESCRIPTOR } from './test-schemas';
import { TOOL_CALLER } from './tool-options.service';

/** Поле зі списком з конектора (F3-54): режими, залежні списки, кеш панелі, деградація при помилці. */
describe('поле зі списком з конектора (x-openbp-options)', () => {
  const ORDER_TOOL: McpToolInfo = {
    name: 'create_order',
    inputSchema: {
      type: 'object',
      properties: {
        customer_id: {
          type: 'string',
          title: 'Контрагент',
          'x-openbp-options': { tool: 'list_customers', arguments: { top: 50 }, itemsPath: 'result', valuePath: 'Ref_Key', labelPath: 'Description' },
        },
        contract_id: {
          type: 'string',
          title: 'Договір',
          'x-openbp-options': { tool: 'list_contracts', arguments: { owner: '{{ $params.customer_id }}' }, valuePath: 'id', labelPath: 'name', dependsOn: ['customer_id'] },
        },
        chat_id: { type: 'integer', 'x-openbp-options': { tool: 'broken', valuePath: 'id' } },
      },
    },
  };
  const LIST_TOOLS: McpToolInfo[] = [
    ORDER_TOOL,
    { name: 'list_customers', inputSchema: { type: 'object', properties: { top: { type: 'integer' } } } },
    { name: 'list_contracts', inputSchema: { type: 'object', properties: { owner: { type: 'string' } } } },
    { name: 'broken', inputSchema: { type: 'object' } },
  ];

  const CUSTOMERS = { result: [{ Ref_Key: 'c-1', Description: 'ТОВ Агро' }, { Ref_Key: 'c-2', Description: 'ФОП Петренко' }] };
  const CONTRACTS: Record<string, unknown> = {
    'c-1': [{ id: 'd-11', name: 'Договір 11' }],
    'c-2': [{ id: 'd-21', name: 'Договір 21' }, { id: 'd-22', name: 'Договір 22' }],
  };

  let calls: { serverId: string; tool: string; args: Record<string, unknown> }[];
  function caller(serverId: string, tool: string, args: Record<string, unknown>): Observable<ToolCallResult> {
    calls.push({ serverId, tool, args });
    const ok = (structuredContent: unknown): Observable<ToolCallResult> => of({ isError: false, structuredContent: structuredContent as Record<string, unknown>, content: [], elapsed: '00:00:00' });
    switch (tool) {
      case 'list_customers':
        return ok(CUSTOMERS);
      case 'list_contracts':
        return ok(CONTRACTS[String(args['owner'])] ?? []);
      default:
        return of({ isError: true, structuredContent: null, content: [{ type: 'text', text: 'HTTP 401 Unauthorized' }], elapsed: '00:00:00' });
    }
  }

  beforeEach(() => {
    calls = [];
    TestBed.configureTestingModule({ providers: [{ provide: TOOL_CALLER, useValue: caller }] });
  });

  function node(args: Record<string, unknown>): WorkflowNode {
    return { id: 'n_order', name: 'Замовлення', type: 'mcp.tool', typeVersion: 1, position: [0, 0], parameters: { serverId: 'srv_1c', toolName: 'create_order', arguments: args } };
  }

  async function setup(args: Record<string, unknown>, serverTools: McpToolInfo[] | null = LIST_TOOLS) {
    const fixture = TestBed.createComponent(NodePanelComponent);
    fixture.componentRef.setInput('node', node(args));
    fixture.componentRef.setInput('descriptor', MCP_TOOL_DESCRIPTOR);
    fixture.componentRef.setInput('tool', ORDER_TOOL);
    fixture.componentRef.setInput('serverTools', serverTools);
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
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, render, args: () => (emitted.at(-1)?.parameters['arguments'] ?? args) as Record<string, unknown> };
  }

  function el<T extends HTMLElement>(root: ParentNode, testId: string): T {
    const found = root.querySelector(`[data-testid="${testId}"]`);
    if (!found) throw new Error(`немає [data-testid="${testId}"]`);
    return found as T;
  }

  const checked = (host: HTMLElement, key: string) =>
    ['list', 'text', 'expression'].find((m) => el(host, `mode-${m}-${key}`).getAttribute('aria-checked') === 'true');

  /** Відкриває список і обирає варіант за підписом (варіанти — в overlay поза панеллю). */
  async function pick(host: HTMLElement, key: string, label: string, render: () => Promise<void>) {
    const input = el<HTMLInputElement>(host, `field-${key}`);
    input.blur();
    input.focus();
    input.dispatchEvent(new Event('focusin'));
    await render();
    const option = [...document.querySelectorAll<HTMLElement>(`[data-testid="option-${key}"]`)].find((o) => o.textContent?.includes(label));
    if (!option) throw new Error(`немає варіанта ${label}`);
    option.click();
    await render();
  }

  it('порожнє значення — «Зі списку»; вибір записує id, а поле показує підпис', async () => {
    const { host, render, args } = await setup({});
    expect(checked(host, 'customer_id')).toBe('list');
    expect(calls.find((c) => c.tool === 'list_customers')).toEqual({ serverId: 'srv_1c', tool: 'list_customers', args: { top: 50 } });

    await pick(host, 'customer_id', 'ФОП Петренко', render);

    expect(args()['customer_id']).toBe('c-2');
    expect(el<HTMLInputElement>(host, 'field-customer_id').value).toBe('ФОП Петренко');
  });

  it('режим виводиться зі значення: є в списку / інший текст / вираз', async () => {
    const inList = await setup({ customer_id: 'c-1' });
    expect(checked(inList.host, 'customer_id')).toBe('list');
    expect(el<HTMLInputElement>(inList.host, 'field-customer_id').value).toBe('ТОВ Агро');
    inList.fixture.destroy();

    const other = await setup({ customer_id: 'guid-not-in-list' });
    expect(checked(other.host, 'customer_id')).toBe('text');
    expect(el<HTMLInputElement>(other.host, 'field-customer_id').value).toBe('guid-not-in-list');
    other.fixture.destroy();

    const expression = await setup({ customer_id: '{{ $json.customer }}' });
    expect(checked(expression.host, 'customer_id')).toBe('expression');
    expect(el<HTMLInputElement>(expression.host, 'field-customer_id').value).toBe('{{ $json.customer }}');
  });

  it('перемикач: «Ввести значення» лишає значення текстом, «Вираз» — заготовка {{ }}, назад «Зі списку»', async () => {
    const { host, render, args } = await setup({ customer_id: 'c-1' });

    el<HTMLButtonElement>(host, 'mode-text-customer_id').click();
    await render();
    const text = el<HTMLInputElement>(host, 'field-customer_id');
    expect(text.value).toBe('c-1');
    text.value = 'c-9';
    text.dispatchEvent(new Event('input'));
    await render();
    expect(args()['customer_id']).toBe('c-9');

    el<HTMLButtonElement>(host, 'mode-list-customer_id').click();
    await render();
    // Значення поза списком у режимі списку показується як є.
    expect(el<HTMLInputElement>(host, 'field-customer_id').value).toBe('c-9');

    el<HTMLButtonElement>(host, 'mode-expression-customer_id').click();
    await render();
    expect(checked(host, 'customer_id')).toBe('expression');
    expect(el<HTMLInputElement>(host, 'field-contract_id')).toBeTruthy();
  });

  it('залежний список: без параметра — підказка, зміна параметра перезавантажує; кеш у межах панелі', async () => {
    const { host, render, args } = await setup({});
    expect(el(host, 'options-status-contract_id').textContent).toContain('customer_id');
    expect(calls.some((c) => c.tool === 'list_contracts')).toBe(false);

    await pick(host, 'customer_id', 'ФОП Петренко', render);
    expect(calls.filter((c) => c.tool === 'list_contracts').map((c) => c.args)).toEqual([{ owner: 'c-2' }]);
    await pick(host, 'contract_id', 'Договір 22', render);
    expect(args()).toMatchObject({ customer_id: 'c-2', contract_id: 'd-22' });

    await pick(host, 'customer_id', 'ТОВ Агро', render);
    await pick(host, 'customer_id', 'ФОП Петренко', render);
    // Повернення до c-2 — з кешу; «Оновити» — новий виклик.
    expect(calls.filter((c) => c.tool === 'list_contracts').map((c) => c.args['owner'])).toEqual(['c-2', 'c-1']);
    const customersBefore = calls.filter((c) => c.tool === 'list_customers').length;
    el<HTMLButtonElement>(host, 'refresh-customer_id').click();
    await render();
    expect(calls.filter((c) => c.tool === 'list_customers').length).toBe(customersBefore + 1);
  });

  it('помилка конектора: поле деградує в текст з повідомленням, значення не змінюється', async () => {
    const { host, render, args } = await setup({ chat_id: 42 });
    expect(checked(host, 'chat_id')).toBe('text');
    expect(el(host, 'options-status-chat_id').textContent).toContain('HTTP 401 Unauthorized');
    const input = el<HTMLInputElement>(host, 'field-chat_id');
    expect(input.value).toBe('42');
    input.value = '43';
    input.dispatchEvent(new Event('input'));
    await render();
    expect(args()['chat_id']).toBe(43);
  });

  it('HTTP-помилка виклику — теж деградація; без конектора — звичайне поле без перемикача', async () => {
    TestBed.overrideProvider(TOOL_CALLER, { useValue: () => throwError(() => new Error('мережа')) });
    const failing = await setup({});
    expect(checked(failing.host, 'customer_id')).toBe('text');
    expect(failing.host.querySelector('[data-testid="options-status-customer_id"]')?.className).toContain('error');
    failing.fixture.destroy();

    const plain = await setup({ customer_id: 'c-1' }, null);
    expect(plain.host.querySelector('[data-testid="mode-list-customer_id"]')).toBeNull();
    expect(el<HTMLInputElement>(plain.host, 'field-customer_id').value).toBe('c-1');
  });
});
