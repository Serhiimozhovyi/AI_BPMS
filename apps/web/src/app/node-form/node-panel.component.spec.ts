import { TestBed } from '@angular/core/testing';
import { McpToolInfo } from '../connectors/connectors-api.service';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { NodePanelComponent, hasCustomCommon } from './node-panel.component';
import { DECISION_TABLE_DESCRIPTOR, FAKE_ORDER_TOOL, IF_DESCRIPTOR, MCP_TOOL_DESCRIPTOR, SET_DESCRIPTOR, SWITCH_DESCRIPTOR } from './test-schemas';

describe('NodePanelComponent', () => {
  async function setup(node: WorkflowNode, descriptor: NodeTypeInfo | undefined, tool: McpToolInfo | null = null) {
    const fixture = TestBed.createComponent(NodePanelComponent);
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('descriptor', descriptor);
    fixture.componentRef.setInput('tool', tool);
    const emitted: WorkflowNode[] = [];
    fixture.componentInstance.nodeChange.subscribe((n) => {
      emitted.push(n);
      // Як редактор: змінений вузол повертається в панель.
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

  function el<T extends HTMLElement>(host: HTMLElement, testId: string): T {
    const found = host.querySelector(`[data-testid="${testId}"]`);
    if (!found) throw new Error(`немає [data-testid="${testId}"]`);
    return found as T;
  }

  function type(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  const setNode: WorkflowNode = {
    id: 'n_set',
    name: 'Поля',
    type: 'core.set',
    typeVersion: 1,
    position: [0, 0],
    parameters: { fields: [{ name: 'customer', value: 'ТОВ Агро' }], legacy: 1 },
    custom: { keep: true },
  };

  it('технічний тип вузла не в тілі панелі: показується назва типу, тип — у підказці', async () => {
    const descriptor: NodeTypeInfo = { ...SET_DESCRIPTOR, type: 'core.loop', displayName: 'Цикл' };
    const { host } = await setup({ ...setNode, type: 'core.loop', parameters: {} }, descriptor);
    const type = host.querySelector<HTMLElement>('.panel__type')!;
    expect(type.textContent?.trim()).toBe('Цикл');
    expect(type.title).toBe('core.loop');
    expect(host.textContent).not.toContain('core.loop');
  });

  it('core.set: поля масиву з параметрів; додавання поля з виразом потрапляє у вузол', async () => {
    const { fixture, host, last, emitted } = await setup(setNode, SET_DESCRIPTOR);
    expect(emitted).toEqual([]);
    expect(el<HTMLInputElement>(host, 'field-name').value).toBe('customer');
    expect(el<HTMLTextAreaElement>(host, 'field-value').value).toBe('ТОВ Агро');

    el<HTMLButtonElement>(host, 'add-fields').click();
    await render(fixture);
    const names = host.querySelectorAll<HTMLInputElement>('[data-testid="field-name"]');
    const values = host.querySelectorAll<HTMLTextAreaElement>('[data-testid="field-value"]');
    expect(names).toHaveLength(2);
    type(names[1], 'total');
    type(values[1], '{{ $json.sum * 2 }}');
    await render(fixture);

    expect(last().parameters).toEqual({
      fields: [
        { name: 'customer', value: 'ТОВ Агро' },
        { name: 'total', value: '{{ $json.sum * 2 }}' },
      ],
      legacy: 1,
    });
    expect(last()['custom']).toEqual({ keep: true });
    // Форма не перебудовується від власної зміни: ті самі поля введення.
    expect(host.querySelectorAll('[data-testid="field-name"]')[1]).toBe(names[1]);

    el<HTMLButtonElement>(host, 'remove-fields-0').click();
    await render(fixture);
    expect(last().parameters['fields']).toEqual([{ name: 'total', value: '{{ $json.sum * 2 }}' }]);
  });

  it('core.set: JSON-значення і прапорець keepOnlySet', async () => {
    const { fixture, host, last } = await setup(setNode, SET_DESCRIPTOR);
    type(el<HTMLTextAreaElement>(host, 'field-value'), '{"qty": 3}');
    el<HTMLElement>(host, 'field-keepOnlySet').querySelector('input')!.click();
    await render(fixture);
    expect(last().parameters).toEqual({ fields: [{ name: 'customer', value: { qty: 3 } }], legacy: 1, keepOnlySet: true });
  });

  it('core.if: умова з переліку операторів; вираз замість enum через перемикач {{ }}', async () => {
    const ifNode: WorkflowNode = {
      id: 'n_if',
      name: 'Сума > 1000',
      type: 'core.if',
      typeVersion: 1,
      position: [0, 0],
      parameters: { conditions: [{ left: '{{ $json.total }}', operator: 'gt', right: 1000 }] },
    };
    const { fixture, host, last } = await setup(ifNode, IF_DESCRIPTOR);
    expect(el<HTMLTextAreaElement>(host, 'field-left').value).toBe('{{ $json.total }}');
    expect(el<HTMLTextAreaElement>(host, 'field-right').value).toBe('1000');
    // Оператори — українською (F3-53).
    expect(host.querySelector('[data-testid="field-operator"]')?.textContent).toContain('більше');

    type(el<HTMLTextAreaElement>(host, 'field-right'), '5000');
    await render(fixture);
    expect(last().parameters['conditions']).toEqual([{ left: '{{ $json.total }}', operator: 'gt', right: 5000 }]);

    el<HTMLButtonElement>(host, 'expr-combine').click();
    await render(fixture);
    type(el<HTMLInputElement>(host, 'field-combine'), '{{ $json.strict ? "and" : "or" }}');
    await render(fixture);
    expect(last().parameters['combine']).toBe('{{ $json.strict ? "and" : "or" }}');
  });

  describe('конструктор умов і присвоєнь (F3-53)', () => {
    const ifNode: WorkflowNode = {
      id: 'n_if',
      name: 'Умова',
      type: 'core.if',
      typeVersion: 1,
      position: [0, 0],
      parameters: {
        conditions: [
          { left: '{{ $json.total }}', operator: 'isEmpty', right: 'лишається' },
          { left: '{{ $json.city }}', operator: 'equals', right: 'Київ' },
        ],
        combine: 'and',
        extra: { keep: 1 },
      },
    };

    it('відкриття не змінює вузол; умови в ряд, праве поле сховане для «порожнє», значення лишається', async () => {
      const { fixture, host, emitted, last } = await setup(ifNode, IF_DESCRIPTOR);
      expect(emitted).toEqual([]);
      const rows = host.querySelectorAll('[data-testid="condition"]');
      expect(rows).toHaveLength(2);
      expect(rows[0].querySelector('[data-testid="field-right"]')).toBeNull();
      expect(rows[1].querySelector<HTMLInputElement>('[data-testid="field-right"]')?.value).toBe('Київ');

      // Друга умова: зміна правого значення — решта (сховане праве першої, невідомі поля) без втрат.
      type(rows[1].querySelector<HTMLInputElement>('[data-testid="field-right"]')!, 'Львів');
      await render(fixture);
      expect(last().parameters).toEqual({
        conditions: [
          { left: '{{ $json.total }}', operator: 'isEmpty', right: 'лишається' },
          { left: '{{ $json.city }}', operator: 'equals', right: 'Львів' },
        ],
        combine: 'and',
        extra: { keep: 1 },
      });
    });

    it('«будь-яка (OR)» чіпом; «Додати умову» додає рядок', async () => {
      const { fixture, host, last } = await setup(ifNode, IF_DESCRIPTOR);
      expect(el(host, 'combine-and').getAttribute('aria-checked')).toBe('true');
      el<HTMLButtonElement>(host, 'combine-or').click();
      await render(fixture);
      expect(last().parameters['combine']).toBe('or');
      expect(el(host, 'combine-or').getAttribute('aria-checked')).toBe('true');

      expect(el(host, 'add-conditions').textContent).toContain('Додати умову');
      el<HTMLButtonElement>(host, 'add-conditions').click();
      await render(fixture);
      expect(host.querySelectorAll('[data-testid="condition"]')).toHaveLength(3);
    });

    it('core.switch: підпис порту над правилом, «Додати правило» до maxItems, останнє правило не видаляється', async () => {
      const node: WorkflowNode = {
        id: 'n_sw',
        name: 'Перемикач',
        type: 'core.switch',
        typeVersion: 1,
        position: [0, 0],
        parameters: { rules: [{ conditions: [{ left: '{{ $json.x }}', operator: 'gt', right: 1 }] }] },
      };
      const { fixture, host } = await setup(node, SWITCH_DESCRIPTOR);
      expect(el(host, 'rule-port').textContent).toContain('вихід rule0');
      expect(host.querySelector('[data-testid="remove-rules-0"]')).toBeNull();
      for (let i = 0; i < 3; i++) {
        el<HTMLButtonElement>(host, 'add-rules').click();
        await render(fixture);
      }
      expect(host.querySelectorAll('[data-testid="rule-port"]')).toHaveLength(4);
      expect(el<HTMLButtonElement>(host, 'add-rules').disabled).toBe(true);
    });

    it('core.set: «Додати всі поля зі входу» — лише відсутні поля з виразами; назва підказує поля входу', async () => {
      const { fixture, host, last } = await setup(setNode, SET_DESCRIPTOR);
      fixture.componentRef.setInput('expressionContext', { nodes: [], input: { customer: 'X', total: 5, 'Місто': 'Київ' } });
      // Форма не перебудовується від контексту — кнопка з'являється при наступній перевірці поля.
      el<HTMLButtonElement>(host, 'add-fields').click();
      await render(fixture);
      expect(el(host, 'add-all-input').textContent).toContain('(2)');
      el<HTMLButtonElement>(host, 'add-all-input').click();
      await render(fixture);
      expect(last().parameters['fields']).toEqual([
        { name: 'customer', value: 'ТОВ Агро' },
        // Порожній рядок, доданий «Додати поле» (незаданий item — як і раніше).
        null,
        { name: 'total', value: '{{ $json.total }}' },
        { name: 'Місто', value: '{{ $json["Місто"] }}' },
      ]);
      expect(host.querySelector('[data-testid="add-all-input"]')).toBeNull();

      const name = host.querySelectorAll<HTMLInputElement>('[data-testid="field-name"]')[1];
      name.focus();
      name.value = 't';
      name.dispatchEvent(new Event('input'));
      await render(fixture);
      expect(document.querySelector('[data-testid="suggest-total"]')).not.toBeNull();
    });
  });

  it('mcp.tool з FakeServer-схемою: аргументи за inputSchema, число і вираз; помилка типу', async () => {
    const toolNode: WorkflowNode = {
      id: 'n_tool',
      name: 'Замовлення',
      type: 'mcp.tool',
      typeVersion: 1,
      position: [0, 0],
      parameters: { serverId: 'fake', toolName: 'order', arguments: { id: 7 } },
    };
    const { fixture, host, last } = await setup(toolNode, MCP_TOOL_DESCRIPTOR, FAKE_ORDER_TOOL);
    expect(host.textContent).not.toContain('fake · order');
    expect(host.querySelector('[data-testid="field-serverId"]')).toBeNull();
    expect(el<HTMLInputElement>(host, 'field-id').value).toBe('7');

    type(el<HTMLInputElement>(host, 'field-id'), '42');
    type(el<HTMLInputElement>(host, 'field-customer'), '{{ $json.customer }}');
    await render(fixture);
    expect(last().parameters).toEqual({ serverId: 'fake', toolName: 'order', arguments: { id: 42, customer: '{{ $json.customer }}' } });

    type(el<HTMLInputElement>(host, 'field-id'), '{{ $json.orderId }}');
    await render(fixture);
    expect(last().parameters['arguments']).toEqual({ id: '{{ $json.orderId }}', customer: '{{ $json.customer }}' });

    type(el<HTMLInputElement>(host, 'field-id'), 'сім');
    await render(fixture);
    expect(el(host, 'error-id').textContent).toContain('Очікується число');
  });

  it('mcp.tool без схеми tool’а: аргументи — JSON', async () => {
    const toolNode: WorkflowNode = {
      id: 'n_tool',
      name: 'T',
      type: 'mcp.tool',
      typeVersion: 1,
      position: [0, 0],
      parameters: { serverId: 'gone', toolName: 'x', arguments: { a: 1 } },
    };
    const { fixture, host, last } = await setup(toolNode, MCP_TOOL_DESCRIPTOR, null);
    expect(el(host, 'node-panel-no-tool')).toBeTruthy();
    type(el<HTMLTextAreaElement>(host, 'field-arguments'), '{"a": 2}');
    await render(fixture);
    expect(last().parameters['arguments']).toEqual({ a: 2 });
  });

  it('загальні поля: timeoutSec, retry, continueOnFail, disabled, notes', async () => {
    const { fixture, host, last } = await setup(setNode, SET_DESCRIPTOR);
    type(el<HTMLInputElement>(host, 'field-timeoutSec'), '30');
    type(el<HTMLInputElement>(host, 'field-maxTries'), '3');
    type(el<HTMLInputElement>(host, 'field-waitMs'), '500');
    el<HTMLElement>(host, 'field-continueOnFail').querySelector('input')!.click();
    el<HTMLElement>(host, 'field-disabled').querySelector('input')!.click();
    type(el<HTMLTextAreaElement>(host, 'field-notes'), 'Перевірити з бухгалтерією');
    await render(fixture);
    expect(last()).toMatchObject({
      timeoutSec: 30,
      retry: { maxTries: 3, waitMs: 500 },
      continueOnFail: true,
      disabled: true,
      notes: 'Перевірити з бухгалтерією',
      parameters: setNode.parameters,
      custom: { keep: true },
    });

    type(el<HTMLInputElement>(host, 'field-timeoutSec'), '{{ 5 }}');
    await render(fixture);
    expect(el(host, 'error-timeoutSec').textContent).toContain('не підтримується');
  });

  it('зовнішня зміна вузла (undo) перебудовує форму', async () => {
    const { fixture, host } = await setup(setNode, SET_DESCRIPTOR);
    type(el<HTMLInputElement>(host, 'field-name'), 'changed');
    await render(fixture);
    fixture.componentRef.setInput('node', setNode);
    await render(fixture);
    expect(el<HTMLInputElement>(host, 'field-name').value).toBe('customer');
  });

  it('невідомий тип — повідомлення замість форми параметрів', async () => {
    const { host } = await setup({ ...setNode, type: 'x.unknown' }, undefined);
    expect(el(host, 'node-panel-unknown')).toBeTruthy();
    expect(host.querySelector('[data-testid="node-parameters"]')).toBeNull();
    expect(host.querySelector('[data-testid="field-timeoutSec"]')).not.toBeNull();
  });

  describe('таблиця рішень (F6-19)', () => {
    const tableNode: WorkflowNode = {
      id: 'n_dt',
      name: 'Знижка',
      type: 'core.decisionTable',
      typeVersion: 1,
      position: [0, 0],
      parameters: {
        inputs: [
          { name: 'segment', expression: '{{ $json.segment }}' },
          { name: 'total', expression: '{{ $json.total }}' },
        ],
        outputs: [{ name: 'discount' }],
        rules: [
          { when: [{ operator: 'equals', right: 'vip' }, '-'], then: [10] },
          { when: ['-', { operator: 'gte', right: 5000 }], then: ['{{ $json.total / 1000 }}'] },
        ],
      },
    };

    it('рядки — правила, колонки — входи й виходи; відкриття не змінює вузол', async () => {
      const { host, emitted } = await setup(tableNode, DECISION_TABLE_DESCRIPTOR);
      expect(emitted).toEqual([]);
      expect(Array.from(host.querySelectorAll('[data-testid="dt-input-col"]')).map((c) => c.textContent?.trim())).toEqual(['якщо segment', 'якщо total']);
      expect(Array.from(host.querySelectorAll('[data-testid="dt-output-col"]')).map((c) => c.textContent?.trim())).toEqual(['→ discount']);
      expect(host.querySelectorAll('[data-rule]')).toHaveLength(2);
      expect(el<HTMLSelectElement>(host, 'dt-op-0-0').value).toBe('equals');
      expect(el<HTMLInputElement>(host, 'dt-right-0-0').value).toBe('vip');
      expect(el<HTMLSelectElement>(host, 'dt-op-0-1').value).toBe('-');
      expect(host.querySelector('[data-testid="dt-right-0-1"]')).toBeNull();
      expect(el<HTMLInputElement>(host, 'dt-then-1-0').value).toBe('{{ $json.total / 1000 }}');
    });

    it('правка комірок, нове правило, переміщення і видалення потрапляють у параметри', async () => {
      const { fixture, host, last } = await setup(tableNode, DECISION_TABLE_DESCRIPTOR);

      const op = el<HTMLSelectElement>(host, 'dt-op-0-1');
      op.value = 'lt';
      op.dispatchEvent(new Event('change'));
      await render(fixture);
      const right = el<HTMLInputElement>(host, 'dt-right-0-1');
      right.value = '1000';
      right.dispatchEvent(new Event('change'));
      const then = el<HTMLInputElement>(host, 'dt-then-0-0');
      then.value = 'п\'ять';
      then.dispatchEvent(new Event('change'));
      await render(fixture);
      expect(last().parameters['rules']).toEqual([
        { when: [{ operator: 'equals', right: 'vip' }, { operator: 'lt', right: 1000 }], then: ["п'ять"] },
        { when: ['-', { operator: 'gte', right: 5000 }], then: ['{{ $json.total / 1000 }}'] },
      ]);

      el<HTMLButtonElement>(host, 'dt-add').click();
      await render(fixture);
      expect((last().parameters['rules'] as unknown[])[2]).toEqual({ when: ['-', '-'], then: [null] });

      el<HTMLButtonElement>(host, 'dt-up-1').click();
      await render(fixture);
      expect((last().parameters['rules'] as { then: unknown[] }[]).map((r) => r.then[0])).toEqual(['{{ $json.total / 1000 }}', "п'ять", null]);

      el<HTMLButtonElement>(host, 'dt-remove-2').click();
      await render(fixture);
      expect(last().parameters['rules']).toHaveLength(2);
      expect(last().parameters['inputs']).toEqual(tableNode.parameters['inputs']);
    });
  });

  describe('«Дія» конектора (F3-55)', () => {
    const TOOLS: McpToolInfo[] = [
      FAKE_ORDER_TOOL,
      { name: 'list_orders', title: 'Замовлення', description: 'Список замовлень', inputSchema: { type: 'object' } },
      { name: 'get_orders', inputSchema: { type: 'object' } },
    ];
    const mcpNode = (args: Record<string, unknown>): WorkflowNode => ({
      id: 'n_mcp',
      name: 'order',
      type: 'mcp.tool',
      typeVersion: 1,
      position: [0, 0],
      parameters: { serverId: 'c1', toolName: 'order', arguments: args },
    });

    async function setupAction(args: Record<string, unknown>) {
      const ctx = await setup(mcpNode(args), MCP_TOOL_DESCRIPTOR, FAKE_ORDER_TOOL);
      ctx.fixture.componentRef.setInput('serverTools', TOOLS);
      await render(ctx.fixture);
      const changes: McpToolInfo[] = [];
      ctx.fixture.componentInstance.toolChange.subscribe((t) => changes.push(t));
      const input = el<HTMLInputElement>(ctx.host, 'action-select');
      const open = async (text = '') => {
        input.blur();
        input.focus();
        input.dispatchEvent(new Event('focusin'));
        if (text) type(input, text);
        await render(ctx.fixture);
        return [...document.querySelectorAll<HTMLElement>('[data-testid="action-option"]')];
      };
      return { ...ctx, input, open, changes };
    }

    it('поле показує поточну дію; список згрупований «Ресурс → Операція» з описом; пошук звужує', async () => {
      const { input, open } = await setupAction({});
      expect(input.value).toBe('order');

      const options = await open();
      expect(options.map((o) => o.getAttribute('data-tool'))).toEqual(['list_orders', 'get_orders', 'order']);
      // Групи: ресурс «orders» і «Інші дії».
      const groups = [...document.querySelectorAll('mat-optgroup')].map((g) => g.textContent ?? '');
      expect(groups).toHaveLength(2);
      expect(groups[0]).toContain('orders');
      expect(groups[1]).toContain('Інші дії');
      expect(options[0].textContent).toContain('Список замовлень');

      expect((await open('замовлень')).map((o) => o.getAttribute('data-tool'))).toEqual(['list_orders']);
    });

    it('порожні аргументи — дія змінюється одразу', async () => {
      const { open, changes, fixture } = await setupAction({});
      (await open()).find((o) => o.getAttribute('data-tool') === 'get_orders')!.click();
      await render(fixture);
      expect(changes.map((t) => t.name)).toEqual(['get_orders']);
    });

    it('непорожні аргументи — попередження: «Скасувати» лишає дію, «Змінити» — змінює', async () => {
      const { host, input, open, changes, fixture } = await setupAction({ id: 7 });
      (await open()).find((o) => o.getAttribute('data-tool') === 'list_orders')!.click();
      await render(fixture);
      expect(changes).toEqual([]);
      expect(el(host, 'action-reset-warning').textContent).toContain('Замовлення');

      el<HTMLButtonElement>(host, 'action-cancel').click();
      await render(fixture);
      expect(host.querySelector('[data-testid="action-reset-warning"]')).toBeNull();
      expect(input.value).toBe('order');

      (await open()).find((o) => o.getAttribute('data-tool') === 'list_orders')!.click();
      await render(fixture);
      el<HTMLButtonElement>(host, 'action-confirm').click();
      await render(fixture);
      expect(changes.map((t) => t.name)).toEqual(['list_orders']);
    });

    it('без tools конектора поля «Дія» немає', async () => {
      const { host } = await setup(mcpNode({}), MCP_TOOL_DESCRIPTOR, FAKE_ORDER_TOOL);
      expect(host.querySelector('[data-testid="action-select"]')).toBeNull();
    });
  });

  it('«Додатково» згорнуте за замовчуванням; позначка «змінено» лише коли є нестандартні значення (F3-78)', async () => {
    const plain = await setup(setNode, SET_DESCRIPTOR);
    expect(el<HTMLDetailsElement>(plain.host, 'node-extra').open).toBe(false);
    expect(plain.host.querySelector('[data-testid="node-extra-changed"]')).toBeNull();

    const tuned = await setup({ ...setNode, timeoutSec: 30 }, SET_DESCRIPTOR);
    expect(el<HTMLDetailsElement>(tuned.host, 'node-extra').open).toBe(false);
    expect(el(tuned.host, 'node-extra-changed').textContent?.trim()).toBe('змінено');

    el<HTMLElement>(plain.host, 'node-extra-toggle').click();
    await render(plain.fixture);
    expect(el<HTMLDetailsElement>(plain.host, 'node-extra').open).toBe(true);
  });

  it('hasCustomCommon: усі загальні поля (F3-78)', () => {
    const base: WorkflowNode = { id: 'a', name: 'a', type: 'core.set', typeVersion: 1, position: [0, 0], parameters: {} };
    expect(hasCustomCommon(base)).toBe(false);
    expect(hasCustomCommon({ ...base, continueOnFail: false, disabled: false, notes: '  ' })).toBe(false);
    expect(hasCustomCommon({ ...base, retry: { maxTries: 3, waitMs: 0 } })).toBe(true);
    expect(hasCustomCommon({ ...base, continueOnFail: true })).toBe(true);
    expect(hasCustomCommon({ ...base, disabled: true })).toBe(true);
    expect(hasCustomCommon({ ...base, notes: 'важливо' })).toBe(true);
  });

  it('клік по назві в панелі просить редактор перейменувати вузол (F3-78)', async () => {
    const { fixture, host } = await setup(setNode, SET_DESCRIPTOR);
    const requests: unknown[] = [];
    fixture.componentInstance.renameRequest.subscribe(() => requests.push(1));
    el<HTMLElement>(host, 'node-panel-name').click();
    expect(requests).toHaveLength(1);
  });
});
