import dailyReport from '../../../../../examples/daily-report.json';
import helloManual from '../../../../../examples/hello-manual.json';
import orderTotal from '../../../../../examples/order-total.json';
import telegram1c from '../../../../../examples/telegram-1c.json';
import { diffDefinitions, isEmptyDiff } from './version-diff';
import { WorkflowDefinition } from './workflows-api.service';

const EXAMPLES = [dailyReport, helloManual, orderTotal, telegram1c] as unknown as WorkflowDefinition[];

const clone = (d: WorkflowDefinition): WorkflowDefinition => JSON.parse(JSON.stringify(d)) as WorkflowDefinition;

describe('diffDefinitions', () => {
  it.each(EXAMPLES.map((d) => [d.name, d]))('%s: версія сама з собою — порожній diff, навіть з іншим порядком ключів', (_, def) => {
    const reordered = clone(def);
    reordered.nodes = reordered.nodes.map((n) => Object.fromEntries(Object.entries(n).reverse()) as typeof n);
    expect(isEmptyDiff(diffDefinitions(def, reordered))).toBe(true);
  });

  it.each(EXAMPLES.map((d) => [d.name, d]))('%s: видалений останній вузол з його ребрами, змінений і переміщений вузли', (_, def) => {
    const after = clone(def);
    const removed = after.nodes.pop()!;
    after.connections = after.connections.filter((c) => c.from.node !== removed.id && c.to.node !== removed.id);
    after.nodes[0].position = [after.nodes[0].position[0] + 40, after.nodes[0].position[1]];
    after.nodes[1].parameters = { ...after.nodes[1].parameters, extra: 1 };

    const diff = diffDefinitions(def, after);

    expect(diff.removedNodes).toEqual([removed.name]);
    expect(diff.addedNodes).toEqual([]);
    expect(diff.changedNodes).toEqual([after.nodes[1].name]);
    expect(diff.movedNodes).toEqual([after.nodes[0].name]);
    expect(diff.removedEdges.length).toBe(def.connections.length - after.connections.length);
    expect(diff.removedEdges.every((e) => e.includes(removed.name))).toBe(true);
    expect(diff.addedEdges).toEqual([]);
    expect(diff.otherChanged).toBe(false);

    // У зворотний бік — ті самі вузли й ребра додано.
    const back = diffDefinitions(after, def);
    expect(back.addedNodes).toEqual([removed.name]);
    expect(back.addedEdges.length).toBe(diff.removedEdges.length);
  });

  it('нове ребро між наявними вузлами і зміна налаштувань', () => {
    const def = clone(helloManual as unknown as WorkflowDefinition);
    const after = clone(def);
    after.connections.push({ from: { node: 'n_start', output: 0 }, to: { node: 'n_length', input: 0 } });
    after.settings = { ...after.settings, timezone: 'UTC' };

    const diff = diffDefinitions(def, after);

    expect(diff.addedEdges).toEqual(['Старт#0 → Довжина#0']);
    expect(diff.otherChanged).toBe(true);
    expect(isEmptyDiff(diff)).toBe(false);
  });
});
