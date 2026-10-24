import { McpToolInfo } from '../connectors/connectors-api.service';
import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { changeNodeTool, classifyAction, defaultAction, filterActions, groupActions, isAutomaticName } from './connector-actions';

const tool = (name: string, extra: Partial<McpToolInfo> = {}): McpToolInfo => ({ name, inputSchema: { type: 'object' }, ...extra });

describe('connector-actions', () => {
  it('групування за іменами: list/get/create/update/delete → «Ресурс → Операція», решта — «Інші дії» останньою', () => {
    const groups = groupActions([
      tool('send_message', { title: 'Надіслати повідомлення' }),
      tool('update_document_orders'),
      tool('list_document_orders'),
      tool('delete_catalog_kontragenty'),
      tool('get_document_orders'),
      tool('create_document_orders'),
      tool('list_catalog_kontragenty'),
      tool('sales_report'),
    ]);

    expect(groups.map((g) => [g.label, g.actions.map((a) => `${a.label}:${a.tool.name}`)])).toEqual([
      ['catalog_kontragenty', ['Список:list_catalog_kontragenty', 'Видалити:delete_catalog_kontragenty']],
      ['document_orders', ['Список:list_document_orders', 'Отримати:get_document_orders', 'Створити:create_document_orders', 'Змінити:update_document_orders']],
      ['Інші дії', ['Надіслати повідомлення:send_message', 'sales_report:sales_report']],
    ]);
    expect(defaultAction([tool('send_message'), tool('list_orders')])?.name).toBe('list_orders');
    expect(defaultAction([])).toBeNull();
  });

  it('анотації x-openbp-resource / x-openbp-operation важать більше за ім’я', () => {
    expect(classifyAction(tool('send_message', { inputSchema: { 'x-openbp-resource': 'Повідомлення', 'x-openbp-operation': 'create' } }))).toEqual({
      resource: 'Повідомлення',
      operation: 'Створити',
      order: 2,
    });
    expect(classifyAction(tool('pin_message', { title: 'Закріпити', inputSchema: { 'x-openbp-resource': 'Повідомлення', 'x-openbp-operation': 'Закріпити' } }))).toMatchObject({
      resource: 'Повідомлення',
      operation: 'Закріпити',
    });
    expect(classifyAction(tool('list_x', { inputSchema: { 'x-openbp-resource': ' ' } }))).toMatchObject({ resource: 'x', operation: 'Список' });
  });

  it('пошук дій — кожне слово в назві, імені, описі чи ресурсі', () => {
    const groups = groupActions([tool('send_message', { title: 'Надіслати повідомлення', description: 'Текст у чат' }), tool('list_orders')]);
    expect(filterActions(groups, 'чат').flatMap((g) => g.actions.map((a) => a.tool.name))).toEqual(['send_message']);
    expect(filterActions(groups, 'orders список').flatMap((g) => g.actions.map((a) => a.tool.name))).toEqual(['list_orders']);
    expect(filterActions(groups, 'немає')).toEqual([]);
    expect(filterActions(groups, '')).toEqual(groups);
  });

  describe('changeNodeTool', () => {
    const send = tool('send_message', { title: 'Надіслати повідомлення' });
    const edit = tool('edit_message_text', { title: 'Змінити повідомлення' });
    const definition = (name: string): WorkflowDefinition => ({
      ...emptyWorkflowDefinition('С'),
      nodes: [
        { id: 'a', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
        { id: 'b', name, type: 'mcp.tool', typeVersion: 1, position: [260, 40], parameters: { serverId: 'tg', toolName: 'send_message', arguments: { chat_id: 1 }, itemsPath: 'result' }, retry: { maxTries: 2, waitMs: 100 } },
        { id: 'c', name: 'Змінити повідомлення', type: 'core.set', typeVersion: 1, position: [520, 0], parameters: {} },
      ],
      connections: [
        { from: { node: 'a', output: 0 }, to: { node: 'b', input: 0 } },
        { from: { node: 'b', output: 0 }, to: { node: 'c', input: 0 } },
      ],
    });

    it('скидає arguments, лишає id, позицію, з’єднання й інші параметри; автоматична назва — нова й унікальна', () => {
      const before = definition('Надіслати повідомлення 2');
      const after = changeNodeTool(before, 'b', edit, send);

      const node = after.nodes[1];
      expect(node).toEqual({
        ...before.nodes[1],
        name: 'Змінити повідомлення 2',
        parameters: { serverId: 'tg', toolName: 'edit_message_text', arguments: {}, itemsPath: 'result' },
      });
      expect(after.connections).toBe(before.connections);
      expect(after.nodes[0]).toBe(before.nodes[0]);
      expect(after.nodes[2]).toBe(before.nodes[2]);
    });

    it('назву, задану користувачем, не змінює; та сама дія — без змін', () => {
      const before = definition('Відповідь клієнту');
      expect(changeNodeTool(before, 'b', edit, send).nodes[1].name).toBe('Відповідь клієнту');
      expect(changeNodeTool(before, 'b', send, send)).toBe(before);
      expect(changeNodeTool(before, 'немає', edit, send)).toBe(before);
    });

    it('isAutomaticName: назва дії, з номером або ім’я tool’а', () => {
      expect(isAutomaticName('Надіслати повідомлення', send)).toBe(true);
      expect(isAutomaticName('Надіслати повідомлення 3', send)).toBe(true);
      expect(isAutomaticName('send_message', null, 'send_message')).toBe(true);
      expect(isAutomaticName('Надіслати повідомлення клієнту', send)).toBe(false);
      expect(isAutomaticName('Надіслати повідомлення x', send)).toBe(false);
    });
  });
});
