import { GROUPS_COLLAPSE_KEY, NO_GROUP, groupSections, readCollapsed, recount, reorder, writeCollapsed } from './workflow-groups';
import { WorkflowGroup, WorkflowSummary } from './workflows-api.service';

const sales: WorkflowGroup = { id: 'g1', name: 'Продажі', sortOrder: 1, workflowCount: 1 };
const finance: WorkflowGroup = { id: 'g2', name: 'Фінанси', sortOrder: 0, workflowCount: 1 };

function wf(id: string, name: string, groupId: string | null = null): WorkflowSummary {
  return { id, name, active: false, version: 1, updatedAt: '', groupId };
}

const workflows = [wf('w1', 'Заявки з сайту', 'g1'), wf('w2', 'Звіт за день', 'g2'), wf('w3', 'Розсилка'), wf('w4', 'Сирота', 'g-deleted')];

describe('workflow-groups', () => {
  it('секції груп за sortOrder, «Без групи» в кінці; сценарій невідомої групи — без групи', () => {
    const sections = groupSections([sales, finance], workflows, { groups: new Set(), search: '' });
    expect(sections.map((s) => s.id)).toEqual(['g2', 'g1', NO_GROUP]);
    expect(sections.map((s) => s.workflows.map((w) => w.id))).toEqual([['w2'], ['w1'], ['w3', 'w4']]);
  });

  it('порожні секції без пошуку лишаються (туди можна перетягнути), з пошуком — ховаються', () => {
    const empty: WorkflowGroup = { id: 'g3', name: 'Нова', sortOrder: 2, workflowCount: 0 };
    expect(groupSections([sales, empty], [], { groups: new Set(), search: '' }).map((s) => s.id)).toEqual(['g1', 'g3', NO_GROUP]);
    expect(groupSections([sales, empty], workflows, { groups: new Set(), search: 'заяв' }).map((s) => s.id)).toEqual(['g1']);
  });

  it('чіпи обмежують секції, пошук працює в їхніх межах без урахування регістру', () => {
    const chips = new Set(['g1', NO_GROUP]);
    expect(groupSections([sales, finance], workflows, { groups: chips, search: '' }).map((s) => s.id)).toEqual(['g1', NO_GROUP]);
    const found = groupSections([sales, finance], workflows, { groups: chips, search: '  ЗАЯВ ' });
    expect(found.flatMap((s) => s.workflows.map((w) => w.id))).toEqual(['w1']);
    expect(groupSections([sales, finance], workflows, { groups: new Set(['g2']), search: 'розсилка' })).toEqual([]);
  });

  it('recount оновлює лічильники лише там, де змінились', () => {
    const counted = recount([sales, finance], [wf('a', 'a', 'g1'), wf('b', 'b', 'g1')]);
    expect(counted.map((g) => g.workflowCount)).toEqual([2, 0]);
    expect(recount([sales], [wf('a', 'a', 'g1')])[0]).toBe(sales);
  });

  it('reorder переставляє групи й перенумеровує sortOrder', () => {
    const third: WorkflowGroup = { id: 'g3', name: 'Склад', sortOrder: 2, workflowCount: 0 };
    const result = reorder([sales, finance, third], 2, 0);
    expect(result.map((g) => g.id)).toEqual(['g3', 'g2', 'g1']);
    expect(result.map((g) => g.sortOrder)).toEqual([0, 1, 2]);
    expect(reorder([sales, finance], 0, 5).map((g) => g.id)).toEqual(['g2', 'g1']);
  });

  it('згортання зберігається в localStorage; зіпсоване значення — порожньо', () => {
    writeCollapsed({ g1: true, none: false });
    expect(readCollapsed()).toEqual({ g1: true, none: false });
    localStorage.setItem(GROUPS_COLLAPSE_KEY, '[1]');
    expect(readCollapsed()).toEqual({});
    localStorage.setItem(GROUPS_COLLAPSE_KEY, '{oops');
    expect(readCollapsed()).toEqual({});
  });
});
