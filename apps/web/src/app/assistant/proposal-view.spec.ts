import { AssistantProposalView } from './global-assistant-api.service';
import { columnText, failureText, proposalLink, proposalTitle, scalarArguments, tablePlan } from './proposal-view';

const ID = '11111111-2222-3333-4444-555555555555';

function proposal(tool: string, args: Record<string, unknown> = {}, extra: Partial<AssistantProposalView> = {}): AssistantProposalView {
  return { id: 'p1', tool, arguments: args, plan: null, status: 'pending', result: null, createdAt: '', decidedAt: null, ...extra };
}

describe('proposalTitle', () => {
  it('називає дію й об’єкт', () => {
    expect(proposalTitle(proposal('create_table', { name: 'Заявки' }))).toBe('Створити таблицю «Заявки»');
    expect(proposalTitle(proposal('alter_table', {}, { plan: { action: 'alter', definition: { name: 'Клієнти' } } }))).toBe('Змінити таблицю «Клієнти»');
    expect(proposalTitle(proposal('create_workflow', { name: 'Звіт' }))).toBe('Створити сценарій «Звіт»');
    expect(proposalTitle(proposal('create_workflow'))).toBe('Створити сценарій');
    expect(proposalTitle(proposal('create_workflow_group', { name: 'Бот' }))).toBe('Створити проєкт «Бот»');
    expect(proposalTitle(proposal('publish_workflow'))).toBe('Опублікувати сценарій');
    expect(proposalTitle(proposal('some_tool'))).toBe('some_tool');
  });
});

describe('tablePlan і колонки', () => {
  it('план є лише в пропозиціях таблиць', () => {
    expect(tablePlan(proposal('create_workflow'))).toBeNull();
    expect(tablePlan(proposal('create_table', {}, { plan: { action: 'create', planId: 'x' } }))?.action).toBe('create');
  });

  it('колонка текстом: тип і позначки', () => {
    expect(columnText({ name: 'phone', type: 'string', sensitive: true })).toBe('phone (string, чутлива)');
    expect(columnText({ name: 'id', type: 'string', key: true })).toBe('id (string, ключ)');
    expect(columnText({ name: 'x' })).toBe('x (?)');
  });
});

describe('scalarArguments', () => {
  it('лише прості значення, довгі скорочує', () => {
    const rows = scalarArguments(proposal('x', { name: 'А', count: 2, flag: false, nested: { a: 1 }, none: null, long: 'я'.repeat(200) }));
    expect(rows.map((r) => r.key)).toEqual(['name', 'count', 'flag', 'long']);
    expect(rows[3].value).toHaveLength(118);
  });
});

describe('failureText', () => {
  it('бере message з JSON, інакше сирий текст', () => {
    expect(failureText(proposal('x', {}, { result: '{"error":"conflict","message":"Назва зайнята."}' }))).toBe('Назва зайнята.');
    expect(failureText(proposal('x', {}, { result: 'збій' }))).toBe('збій');
    expect(failureText(proposal('x', {}, { result: null }))).toBe('');
  });
});

describe('proposalLink', () => {
  const applied = (tool: string, args: Record<string, unknown>, result: string | null) => proposal(tool, args, { status: 'applied', result });

  it('без застосування посилання немає', () => {
    expect(proposalLink(proposal('create_workflow'))).toBeNull();
    expect(proposalLink({ ...applied('create_workflow', {}, `{"id":"${ID}"}`), status: 'rejected' })).toBeNull();
  });

  it('створений сценарій — з id із результату, навіть якщо результат обрізано', () => {
    expect(proposalLink(applied('create_workflow', { name: 'Звіт' }, `{"id":"${ID}","name":"Звіт","definition":{"nodes":[{"id`))).toEqual({
      href: `/workflows/${ID}`,
      label: 'Відкрити сценарій',
    });
  });

  it('змінений сценарій — з аргументів', () => {
    expect(proposalLink(applied('update_workflow', { id: ID }, '{"version":3}'))?.href).toBe(`/workflows/${ID}`);
    expect(proposalLink(applied('move_workflow', { workflowId: ID }, '{}'))?.href).toBe(`/workflows/${ID}`);
    expect(proposalLink(applied('update_workflow', { id: 'не-guid' }, '{}'))).toBeNull();
  });

  it('проєкт, виконання, таблиці', () => {
    expect(proposalLink(applied('create_workflow_group', { name: 'Бот' }, `{"id":"${ID}","name":"Бот"}`))?.href).toBe(`/projects/${ID}`);
    expect(proposalLink(applied('run_workflow', { id: ID }, `{"id":"99999999-2222-3333-4444-555555555555","status":"queued"}`))?.href).toBe(
      '/executions/99999999-2222-3333-4444-555555555555',
    );
    expect(proposalLink(applied('create_table', { name: 'Т' }, '{"applied":true}'))?.href).toBe('/tables');
    expect(proposalLink(applied('some_tool', {}, '{}'))).toBeNull();
  });
});
