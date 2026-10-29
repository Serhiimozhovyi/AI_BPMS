import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AssistantProposalView } from './global-assistant-api.service';
import { ProposalCardComponent } from './proposal-card.component';

const ID = '11111111-2222-3333-4444-555555555555';

function proposal(extra: Partial<AssistantProposalView> = {}): AssistantProposalView {
  return { id: 'p1', tool: 'create_table', arguments: { name: 'Заявки' }, plan: null, status: 'pending', result: null, createdAt: '', decidedAt: null, ...extra };
}

describe('ProposalCardComponent', () => {
  function create(p: AssistantProposalView, busy = false) {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(ProposalCardComponent);
    fixture.componentRef.setInput('proposal', p);
    fixture.componentRef.setInput('busy', busy);
    fixture.detectChanges();
    const events: string[] = [];
    fixture.componentInstance.approved.subscribe(() => events.push('apply'));
    fixture.componentInstance.rejected.subscribe(() => events.push('cancel'));
    return { fixture, host: fixture.nativeElement as HTMLElement, events };
  }

  const click = (host: HTMLElement, id: string) => host.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)!.click();

  it('створення таблиці: показує колонки й попередження, кнопки віддають рішення', () => {
    const { host, events } = create(
      proposal({
        plan: {
          planId: 'x',
          action: 'create',
          definition: { name: 'Заявки' },
          addedColumns: [
            { name: 'number', type: 'string', key: true },
            { name: 'phone', type: 'string', sensitive: true },
          ],
          warnings: ['Колонка phone чутлива.'],
        },
      }),
    );
    expect(host.querySelector('h3')?.textContent).toBe('Створити таблицю «Заявки»');
    expect(host.textContent).toContain('number (string, ключ), phone (string, чутлива)');
    expect(host.querySelector('[data-testid="proposal-warning"]')?.textContent).toContain('чутлива');

    click(host, 'proposal-apply');
    click(host, 'proposal-cancel');
    expect(events).toEqual(['apply', 'cancel']);
  });

  it('зміна таблиці: перейменування, видалені й змінені колонки, кількість рядків', () => {
    const { host } = create(
      proposal({
        tool: 'alter_table',
        plan: {
          action: 'alter',
          definition: { name: 'Клієнти' },
          renamedFrom: 'Покупці',
          removedColumns: [{ name: 'old', type: 'string' }],
          changedColumns: [{ before: { name: 'age', type: 'string' }, after: { name: 'age', type: 'number' } }],
          constraintsChanged: true,
          rowCount: 42,
          warnings: ['Значення колонки old буде втрачено.'],
        },
      }),
    );
    const text = host.textContent ?? '';
    expect(text).toContain('Змінити таблицю «Клієнти»');
    expect(text).toContain('Нова назва замість «Покупці»');
    expect(text).toContain('Видаляються: old (string)');
    expect(text).toContain('age (string) → age (number)');
    expect(text).toContain('Змінюються обмеження цілісності');
    expect(text).toContain('Рядків у таблиці зараз: 42');
    expect(text).toContain('буде втрачено');
  });

  it('зміна сценарію: план вузлів, зв\'язків і налаштувань замість JSON', () => {
    const { host, events } = create(
      proposal({
        tool: 'update_workflow',
        arguments: { id: ID, definition: { nodes: [] } },
        plan: {
          planId: 'w1',
          name: 'Заявки',
          baseVersion: 3,
          addedNodes: [{ id: 'n_mail', name: 'Лист', type: 'mcp.tool' }],
          removedNodes: [{ id: 'n_old', name: 'Старий', type: 'core.set' }],
          changedNodes: [{ id: 'n_if', name: 'Перевірка', type: 'core.if', fields: ['name', 'parameters.conditions', 'position'], renamedFrom: 'Умова' }],
          addedConnections: [{ from: 'n_if', fromName: 'Перевірка', output: 1, to: 'n_mail', toName: 'Лист', input: 0 }],
          removedConnections: [],
          settingsChanges: [{ field: 'timezone', before: 'UTC', after: 'Europe/Kyiv' }],
          otherChanges: ['notes'],
          errors: [],
          warnings: [{ code: 'VARIABLE_UNKNOWN', message: 'Змінної $vars.chat немає.' }],
          valid: true,
          hasChanges: true,
        },
      }),
    );
    const text = host.textContent ?? '';
    expect(host.querySelector('h3')?.textContent).toBe('Змінити сценарій «Заявки»');
    expect(host.querySelector('[data-testid="proposal-nodes-added"]')?.textContent).toContain('«Лист» (mcp.tool)');
    expect(host.querySelector('[data-testid="proposal-nodes-removed"]')?.textContent).toContain('«Старий» (core.set)');
    expect(host.querySelector('[data-testid="proposal-node-changed"]')?.textContent).toContain('«Перевірка»: назва (була «Умова»), параметри conditions, місце на полотні');
    expect(host.querySelector('[data-testid="proposal-links-added"]')?.textContent).toContain('«Перевірка» (вихід 2) → «Лист»');
    expect(host.querySelector('[data-testid="proposal-setting"]')?.textContent).toContain('Часовий пояс: UTC → Europe/Kyiv');
    expect(text).toContain('нотатки на полотні');
    expect(text).toContain('чернеткою версії 4');
    expect(host.querySelector('[data-testid="proposal-warning"]')?.textContent).toContain('$vars.chat');
    expect(host.querySelector('pre')).toBeNull();

    click(host, 'proposal-apply');
    expect(events).toEqual(['apply']);
  });

  it('зміна сценарію з помилками: показує їх і не дає застосувати', () => {
    const { host } = create(
      proposal({
        tool: 'update_workflow',
        arguments: { id: ID },
        plan: {
          planId: 'w2',
          name: 'Заявки',
          baseVersion: 1,
          errors: [{ code: 'CONNECTION_NODE_UNKNOWN', message: 'Зв\'язок веде до вузла «ghost», якого немає.' }],
          valid: false,
          hasChanges: true,
        },
      }),
    );
    expect(host.querySelector('[data-testid="proposal-error"]')?.textContent).toContain('ghost');
    expect(host.querySelector<HTMLButtonElement>('[data-testid="proposal-apply"]')!.disabled).toBe(true);
    expect(host.querySelector<HTMLButtonElement>('[data-testid="proposal-cancel"]')!.disabled).toBe(false);
    expect(host.querySelector('[data-testid="proposal-invalid"]')).not.toBeNull();
  });

  it('зміна сценарію без відмінностей: так і каже', () => {
    const { host } = create(proposal({ tool: 'update_workflow', arguments: { id: ID }, plan: { name: 'Заявки', baseVersion: 2, hasChanges: false, valid: true } }));
    expect(host.querySelector('[data-testid="proposal-no-changes"]')?.textContent).toContain('версії 2');
  });

  it('інший інструмент: аргументи списком і повний JSON у «Параметри»', () => {
    const { host } = create(proposal({ tool: 'create_workflow_group', arguments: { name: 'Бот', description: 'Підтримка' } }));
    expect(host.querySelector('h3')?.textContent).toBe('Створити проєкт «Бот»');
    expect(Array.from(host.querySelectorAll('dt')).map((d) => d.textContent)).toEqual(['name', 'description']);
    expect(host.querySelector('pre')?.textContent).toContain('"description": "Підтримка"');
  });

  it('busy блокує кнопки', () => {
    const { host } = create(proposal(), true);
    expect(host.querySelector<HTMLButtonElement>('[data-testid="proposal-apply"]')!.disabled).toBe(true);
    expect(host.querySelector<HTMLButtonElement>('[data-testid="proposal-cancel"]')!.disabled).toBe(true);
  });

  it('застосовано: кнопок немає, є посилання на створене', () => {
    const { host } = create(proposal({ tool: 'create_workflow', arguments: { name: 'Звіт' }, status: 'applied', result: `{"id":"${ID}"}` }));
    expect(host.querySelector('[data-testid="proposal-apply"]')).toBeNull();
    expect(host.querySelector('[data-testid="proposal-status"]')?.textContent).toContain('Застосовано.');
    expect(host.querySelector('[data-testid="proposal-link"]')?.getAttribute('href')).toBe(`/workflows/${ID}`);
  });

  it('скасовано і помилка застосування', () => {
    expect(create(proposal({ status: 'rejected' })).host.querySelector('[data-testid="proposal-status"]')?.textContent).toContain('Скасовано');
    TestBed.resetTestingModule();
    const failed = create(proposal({ status: 'failed', result: '{"message":"План застарів."}' })).host;
    expect(failed.querySelector('[data-testid="proposal-status"]')?.textContent).toContain('Не вдалося: План застарів.');
    expect(failed.querySelector('[data-testid="proposal-apply"]')).toBeNull();
  });
});
