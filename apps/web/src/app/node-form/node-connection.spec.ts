import { TestBed } from '@angular/core/testing';
import { McpToolInfo } from '../connectors/connectors-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { ConnectionChange, NodeConnection, NodePanelComponent } from './node-panel.component';
import { MCP_TOOL_DESCRIPTOR } from './test-schemas';

const tool = (name: string): McpToolInfo => ({ name, inputSchema: { type: 'object' } });
const SEND = tool('send_message');
const EDIT = tool('edit_message_text');

const CONNECTIONS: NodeConnection[] = [
  { id: 'bot1', name: 'Бот продажів', catalogId: 'telegram', tools: [SEND, EDIT] },
  { id: 'bot2', name: 'Бот підтримки', catalogId: 'telegram', tools: [SEND] },
  { id: 'bot3', name: 'Бот без повідомлень', catalogId: 'telegram', tools: [EDIT] },
  { id: 'bot4', name: 'Ще не завантажений', catalogId: 'telegram', tools: [] },
  { id: 'crm', name: 'CRM', catalogId: 'crm', tools: [tool('list_deals')] },
  { id: 'own', name: 'Свій сервер', catalogId: null, tools: [tool('ping')] },
];

describe('NodePanelComponent: поле «Підключення» (F3-95)', () => {
  async function setup(parameters: Record<string, unknown>, connections: NodeConnection[] | null = CONNECTIONS) {
    const node: WorkflowNode = { id: 'n1', name: 'Надіслати', type: 'mcp.tool', typeVersion: 1, position: [0, 0], parameters };
    const fixture = TestBed.createComponent(NodePanelComponent);
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('descriptor', MCP_TOOL_DESCRIPTOR);
    fixture.componentRef.setInput('serverTools', connections?.find((c) => c.id === parameters['serverId'])?.tools ?? null);
    fixture.componentRef.setInput('connections', connections);
    const changes: ConnectionChange[] = [];
    let created = 0;
    fixture.componentInstance.connectionChange.subscribe((c) => changes.push(c));
    fixture.componentInstance.newConnection.subscribe(() => created++);
    await render(fixture);
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, changes, created: () => created };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** Вибір у mat-select як користувач: відкрити й натиснути варіант. */
  async function pick(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }, host: HTMLElement, option: string) {
    host.querySelector<HTMLElement>('[data-testid="connection-select"] .mat-mdc-select-trigger')!.click();
    await render(fixture);
    const el = document.querySelector<HTMLElement>(option);
    if (!el) throw new Error(`немає ${option}`);
    el.click();
    await render(fixture);
  }

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('без списку підключень поле не показується; сирого «serverId · toolName» у тілі панелі немає', async () => {
    const { host } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: {} }, null);
    expect(host.querySelector('[data-testid="node-connection"]')).toBeNull();
    expect(host.textContent).not.toContain('bot1 · send_message');
  });

  it('підключення того самого запису каталогу, «+ Нове підключення…» і технічні значення лише в підказці', async () => {
    const { fixture, host } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: {} });
    const select = host.querySelector<HTMLElement>('[data-testid="connection-select"]')!;
    expect(select.getAttribute('title')).toBe('bot1 · send_message');
    expect(select.textContent).toContain('Бот продажів');
    expect(host.textContent).not.toContain('bot1 · send_message');

    host.querySelector<HTMLElement>('.mat-mdc-select-trigger')!.click();
    await render(fixture);
    const options = Array.from(document.querySelectorAll('[data-testid="connection-option"]')).map((o) => o.getAttribute('data-connection'));
    expect(options).toEqual(['bot1', 'bot2', 'bot3', 'bot4']);
    expect(document.querySelector('[data-testid="connection-new"]')?.textContent).toContain('Нове підключення');
  });

  it('вузол без запису каталогу бачить усі підключення', async () => {
    const { fixture, host } = await setup({ serverId: 'own', toolName: 'ping', arguments: {} });
    host.querySelector<HTMLElement>('.mat-mdc-select-trigger')!.click();
    await render(fixture);
    expect(document.querySelectorAll('[data-testid="connection-option"]')).toHaveLength(CONNECTIONS.length);
  });

  it('інше підключення з тією самою дією: дія й аргументи лишаються', async () => {
    const { fixture, host, changes } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: { chat_id: 5 } });
    await pick(fixture, host, '[data-connection="bot2"]');
    expect(changes).toEqual([{ serverId: 'bot2', tool: null }]);
    expect(host.querySelector('[data-testid="connection-reset-warning"]')).toBeNull();
  });

  it('tools нового підключення ще невідомі: дія й аргументи лишаються', async () => {
    const { fixture, host, changes } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: { chat_id: 5 } });
    await pick(fixture, host, '[data-connection="bot4"]');
    expect(changes).toEqual([{ serverId: 'bot4', tool: null }]);
  });

  it('у новому підключенні немає дії й аргументи непорожні: попередження, скидання лише після підтвердження', async () => {
    const { fixture, host, changes } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: { chat_id: 5 } });
    await pick(fixture, host, '[data-connection="bot3"]');
    expect(changes).toEqual([]);
    expect(host.querySelector('[data-testid="connection-reset-warning"]')?.textContent).toContain('Бот без повідомлень');

    host.querySelector<HTMLButtonElement>('[data-testid="connection-confirm"]')!.click();
    await render(fixture);
    expect(changes).toEqual([{ serverId: 'bot3', tool: EDIT }]);
    expect(host.querySelector('[data-testid="connection-reset-warning"]')).toBeNull();
  });

  it('скасування попередження нічого не змінює', async () => {
    const { fixture, host, changes } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: { chat_id: 5 } });
    await pick(fixture, host, '[data-connection="bot3"]');
    host.querySelector<HTMLButtonElement>('[data-testid="connection-cancel"]')!.click();
    await render(fixture);
    expect(changes).toEqual([]);
    expect(host.querySelector('[data-testid="connection-select"]')?.textContent).toContain('Бот продажів');
  });

  it('у новому підключенні немає дії, але аргументів ще немає: дія замінюється одразу', async () => {
    const { fixture, host, changes } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: {} });
    await pick(fixture, host, '[data-connection="bot3"]');
    expect(changes).toEqual([{ serverId: 'bot3', tool: EDIT }]);
  });

  it('«+ Нове підключення…» просить діалог і не змінює вузол', async () => {
    const { fixture, host, changes, created } = await setup({ serverId: 'bot1', toolName: 'send_message', arguments: {} });
    await pick(fixture, host, '[data-testid="connection-new"]');
    expect(created()).toBe(1);
    expect(changes).toEqual([]);
    expect(host.querySelector('[data-testid="connection-select"]')?.textContent).toContain('Бот продажів');
  });
});
