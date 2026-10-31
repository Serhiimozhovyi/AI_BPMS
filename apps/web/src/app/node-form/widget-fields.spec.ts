import { TestBed } from '@angular/core/testing';
import { McpToolInfo } from '../connectors/connectors-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { NodePanelComponent } from './node-panel.component';
import { FAKE_COMMANDS_TOOL, FAKE_MESSAGE_TOOL, MCP_TOOL_DESCRIPTOR } from './test-schemas';
import { chooseOption, selectValue } from '../shared/select-search/select-testing';

describe('редактори віджетів у панелі вузла (F6-30)', () => {
  function mcpNode(args: Record<string, unknown>, toolName = 'send_message'): WorkflowNode {
    return { id: 'n_tg', name: 'Telegram', type: 'mcp.tool', typeVersion: 1, position: [0, 0], parameters: { serverId: 'srv', toolName, arguments: args } };
  }

  async function setup(node: WorkflowNode, tool: McpToolInfo) {
    const fixture = TestBed.createComponent(NodePanelComponent);
    fixture.componentRef.setInput('node', node);
    fixture.componentRef.setInput('descriptor', MCP_TOOL_DESCRIPTOR);
    fixture.componentRef.setInput('tool', tool);
    const emitted: WorkflowNode[] = [];
    fixture.componentInstance.nodeChange.subscribe((n) => {
      emitted.push(n);
      fixture.componentRef.setInput('node', n);
    });
    await render(fixture);
    const host = fixture.nativeElement as HTMLElement;
    const markup = () => (emitted[emitted.length - 1]?.parameters['arguments'] as Record<string, unknown> | undefined)?.['reply_markup'];
    return { fixture, host, emitted, markup, last: () => emitted[emitted.length - 1] };
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

  function change(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('change'));
  }

  const INLINE = { inline_keyboard: [[{ text: 'Записатися', callback_data: 'book:s1' }], [{ text: 'Сайт', url: 'https://example.com' }]] };

  describe('кнопки reply_markup', () => {
    it('існуюче значення відкривається в потрібному режимі й нічого не змінює', async () => {
      const { host, emitted } = await setup(mcpNode({ chat_id: 1, text: 'Привіт', reply_markup: INLINE }), FAKE_MESSAGE_TOOL);
      expect(emitted).toEqual([]);
      expect(el(host, 'kb-mode-inline').getAttribute('aria-checked')).toBe('true');
      expect(host.querySelectorAll('[data-row]')).toHaveLength(2);
      expect(el<HTMLInputElement>(host, 'kb-text-0-0').value).toBe('Записатися');
      expect(el<HTMLInputElement>(host, 'kb-value-0-0').value).toBe('book:s1');
      expect(selectValue(el(host, 'kb-action-1-0'))).toBe('url');
      expect(el<HTMLInputElement>(host, 'kb-value-1-0').value).toBe('https://example.com');
      expect(Array.from(host.querySelectorAll('.kb__pbtn')).map((b) => b.textContent?.trim())).toEqual(['Записатися', 'Сайт']);
    });

    it('інлайн: створити кнопки з нуля — текст і дія потрапляють у reply_markup', async () => {
      const { fixture, host, markup } = await setup(mcpNode({ chat_id: 1, text: 'Привіт' }), FAKE_MESSAGE_TOOL);
      expect(el(host, 'kb-mode-none').getAttribute('aria-checked')).toBe('true');

      el<HTMLButtonElement>(host, 'kb-mode-inline').click();
      await render(fixture);
      el<HTMLButtonElement>(host, 'kb-add-row').click();
      await render(fixture);
      change(el<HTMLInputElement>(host, 'kb-text-0-0'), 'Так, {{ $json.name }}');
      change(el<HTMLInputElement>(host, 'kb-value-0-0'), 'yes:{{ $json.id }}');
      await render(fixture);
      el<HTMLButtonElement>(host, 'kb-add-button-0').click();
      await render(fixture);
      change(el<HTMLInputElement>(host, 'kb-text-0-1'), 'Сайт');
      chooseOption(el(host, 'kb-action-0-1'), 'url');
      await render(fixture);
      change(el<HTMLInputElement>(host, 'kb-value-0-1'), 'https://example.com');
      await render(fixture);

      expect(markup()).toEqual({
        inline_keyboard: [[{ text: 'Так, {{ $json.name }}', callback_data: 'yes:{{ $json.id }}' }, { text: 'Сайт', url: 'https://example.com' }]],
      });
      expect(host.querySelector('[data-testid="kb-issues"]')).toBeNull();
    });

    it('переставити кнопки й рядки та видалити', async () => {
      const { fixture, host, markup } = await setup(mcpNode({ chat_id: 1, text: 'x', reply_markup: INLINE }), FAKE_MESSAGE_TOOL);

      el<HTMLButtonElement>(host, 'kb-row-down-0').click();
      await render(fixture);
      expect((markup() as typeof INLINE).inline_keyboard.map((r) => r[0].text)).toEqual(['Сайт', 'Записатися']);

      el<HTMLButtonElement>(host, 'kb-add-button-0').click();
      await render(fixture);
      change(el<HTMLInputElement>(host, 'kb-text-0-1'), 'Друга');
      change(el<HTMLInputElement>(host, 'kb-value-0-1'), 'two');
      await render(fixture);
      el<HTMLButtonElement>(host, 'kb-left-0-1').click();
      await render(fixture);
      expect((markup() as typeof INLINE).inline_keyboard[0].map((b) => b.text)).toEqual(['Друга', 'Сайт']);

      el<HTMLButtonElement>(host, 'kb-remove-0-0').click();
      await render(fixture);
      el<HTMLButtonElement>(host, 'kb-row-remove-1').click();
      await render(fixture);
      expect(markup()).toEqual({ inline_keyboard: [[{ text: 'Сайт', url: 'https://example.com' }]] });
    });

    it('клавіатура: контакт, геолокація, resize і one_time', async () => {
      const { fixture, host, markup } = await setup(mcpNode({ chat_id: 1, text: 'x' }), FAKE_MESSAGE_TOOL);
      el<HTMLButtonElement>(host, 'kb-mode-reply').click();
      await render(fixture);
      el<HTMLButtonElement>(host, 'kb-add-row').click();
      await render(fixture);
      change(el<HTMLInputElement>(host, 'kb-text-0-0'), 'Поділитися номером');
      chooseOption(el(host, 'kb-request-0-0'), 'contact');
      await render(fixture);
      el<HTMLButtonElement>(host, 'kb-add-button-0').click();
      await render(fixture);
      change(el<HTMLInputElement>(host, 'kb-text-0-1'), 'Де я');
      chooseOption(el(host, 'kb-request-0-1'), 'location');
      await render(fixture);
      expect(markup()).toEqual({
        keyboard: [[{ text: 'Поділитися номером', request_contact: true }, { text: 'Де я', request_location: true }]],
        resize_keyboard: true,
      });

      el<HTMLInputElement>(host, 'kb-onetime').click();
      el<HTMLInputElement>(host, 'kb-resize').click();
      await render(fixture);
      expect(markup()).toEqual({
        keyboard: [[{ text: 'Поділитися номером', request_contact: true }, { text: 'Де я', request_location: true }]],
        one_time_keyboard: true,
      });
    });

    it('«Прибрати клавіатуру» і «Без кнопок»', async () => {
      const { fixture, host, markup, last } = await setup(mcpNode({ chat_id: 1, text: 'x', reply_markup: INLINE }), FAKE_MESSAGE_TOOL);
      el<HTMLButtonElement>(host, 'kb-mode-remove').click();
      await render(fixture);
      expect(markup()).toEqual({ remove_keyboard: true });
      expect(host.querySelector('[data-testid="kb-remove-hint"]')).not.toBeNull();

      el<HTMLButtonElement>(host, 'kb-mode-none').click();
      await render(fixture);
      expect('reply_markup' in (last().parameters['arguments'] as object)).toBe(false);
    });

    it('перемикання режиму не губить набране в іншому', async () => {
      const { fixture, host, markup } = await setup(mcpNode({ chat_id: 1, text: 'x', reply_markup: INLINE }), FAKE_MESSAGE_TOOL);
      el<HTMLButtonElement>(host, 'kb-mode-reply').click();
      await render(fixture);
      expect(markup()).toBeUndefined();
      el<HTMLButtonElement>(host, 'kb-mode-inline').click();
      await render(fixture);
      expect(markup()).toEqual(INLINE);
    });

    it('зауваження: порожній текст, довгий callback_data, поганий url', async () => {
      const bad = { inline_keyboard: [[{ text: '', callback_data: 'я'.repeat(40) }, { text: 'X', url: 'example.com' }]] };
      const { host } = await setup(mcpNode({ chat_id: 1, text: 'x', reply_markup: bad }), FAKE_MESSAGE_TOOL);
      const issues = Array.from(host.querySelectorAll('[data-testid="kb-issues"] li')).map((li) => li.textContent?.trim());
      expect(issues).toHaveLength(3);
      expect(issues.join(' ')).toContain('64 байти');
    });

    it('вираз і невідома розмітка показуються як є і не псуються', async () => {
      const { fixture, host, markup } = await setup(mcpNode({ chat_id: 1, text: 'x', reply_markup: '{{ $json.markup }}' }), FAKE_MESSAGE_TOOL);
      expect(el<HTMLTextAreaElement>(host, 'kb-raw').value).toBe('{{ $json.markup }}');
      change(el<HTMLTextAreaElement>(host, 'kb-raw'), '{"inline_keyboard":[[{"text":"A","web_app":{"url":"https://x.y"}}]]}');
      await render(fixture);
      expect(markup()).toEqual({ inline_keyboard: [[{ text: 'A', web_app: { url: 'https://x.y' } }]] });
      el<HTMLButtonElement>(host, 'kb-mode-inline').click();
      await render(fixture);
      expect(host.querySelector('[data-testid="kb-raw"]')).toBeNull();
    });
  });

  describe('повні форми Telegram', () => {
    it('текст має лічильник символів з лімітом і червоніє понад ліміт', async () => {
      const { fixture, host } = await setup(mcpNode({ chat_id: 1, text: 'Привіт' }), FAKE_MESSAGE_TOOL);
      expect(el(host, 'counter-text').textContent?.trim()).toBe('6 / 4096');
      const text = el<HTMLTextAreaElement>(host, 'field-text');
      text.value = 'я'.repeat(4100);
      text.dispatchEvent(new Event('input'));
      await render(fixture);
      expect(el(host, 'counter-text').textContent?.trim()).toBe('4100 / 4096');
      expect(el(host, 'counter-text').classList.contains('value__over')).toBe(true);
    });

    it('parse_mode — вибір з HTML, MarkdownV2, Markdown', async () => {
      const { host } = await setup(mcpNode({ chat_id: 1, text: 'x', parse_mode: 'HTML' }), FAKE_MESSAGE_TOOL);
      expect(el(host, 'field-parse_mode').tagName.toLowerCase()).toBe('mat-select');
    });

    it('команди меню — таблиця «команда — опис»: додати, змінити, видалити; помилки Bot API підказано', async () => {
      const node = mcpNode({ commands: [{ command: 'start', description: 'Головне меню' }] }, 'set_my_commands');
      const { fixture, host, last } = await setup(node, FAKE_COMMANDS_TOOL);
      const commands = () => (last().parameters['arguments'] as { commands?: unknown }).commands;
      expect(el<HTMLInputElement>(host, 'cm-command-0').value).toBe('start');

      el<HTMLButtonElement>(host, 'cm-add').click();
      await render(fixture);
      change(el<HTMLInputElement>(host, 'cm-command-1'), 'Help');
      change(el<HTMLInputElement>(host, 'cm-description-1'), 'Довідка');
      await render(fixture);
      expect(el(host, 'cm-issue').textContent).toContain('малі латинські');
      change(el<HTMLInputElement>(host, 'cm-command-1'), 'help');
      await render(fixture);
      expect(host.querySelector('[data-testid="cm-issue"]')).toBeNull();
      expect(commands()).toEqual([{ command: 'start', description: 'Головне меню' }, { command: 'help', description: 'Довідка' }]);

      el<HTMLButtonElement>(host, 'cm-remove-0').click();
      await render(fixture);
      expect(commands()).toEqual([{ command: 'help', description: 'Довідка' }]);
    });

    it('allowed_updates — множинний вибір', async () => {
      const node = mcpNode({ commands: [{ command: 'a', description: 'abc' }], allowed_updates: ['callback_query'] }, 'set_my_commands');
      const { fixture, host, last } = await setup(node, FAKE_COMMANDS_TOOL);
      expect(el<HTMLInputElement>(host, 'multi-allowed_updates-callback_query').checked).toBe(true);
      expect(el<HTMLInputElement>(host, 'multi-allowed_updates-message').checked).toBe(false);

      el<HTMLInputElement>(host, 'multi-allowed_updates-message').click();
      await render(fixture);
      expect((last().parameters['arguments'] as { allowed_updates: string[] }).allowed_updates).toEqual(['message', 'callback_query']);
      el<HTMLInputElement>(host, 'multi-allowed_updates-callback_query').click();
      el<HTMLInputElement>(host, 'multi-allowed_updates-message').click();
      await render(fixture);
      expect('allowed_updates' in (last().parameters['arguments'] as object)).toBe(false);
    });
  });
});
