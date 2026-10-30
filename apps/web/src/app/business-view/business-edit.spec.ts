import { toCanvasModel } from '../canvas/canvas-model';
import { SAMPLE_DESCRIPTORS } from '../canvas/sample-workflows';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { BusinessView, buildBusinessView } from './business-model';
import {
  addExternalStep,
  addLane,
  linkExternalStep,
  moveLane,
  moveStepToLane,
  removeExternalStep,
  renameLane,
  setEdgeLabel,
  setLaneDescription,
  setStepHidden,
  setStepSubtitle,
  setStepTitle,
  toggleLinkDirection,
} from './business-edit';
import kitApproval from '../../../../../examples/kit-approval-business.json';

const CONNECTORS = [{ id: 'mail', name: 'Пошта' }];

/** Ручний запуск → умова → (так) лист / (ні) підготовка (згорнута). Без бізнес-розмітки. */
function plain(): WorkflowDefinition {
  return {
    formatVersion: 1,
    nodes: [
      { id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
      { id: 'n_check', name: 'Сума велика?', type: 'core.if', typeVersion: 1, position: [200, 0], parameters: {} },
      { id: 'n_mail', name: 'Лист керівнику', type: 'mcp.tool', typeVersion: 1, position: [400, 0], parameters: { serverId: 'mail', toolName: 'send' } },
      { id: 'n_prep', name: 'Підготувати', type: 'core.set', typeVersion: 1, position: [400, 200], parameters: {} },
    ],
    connections: [
      { from: { node: 'n_start', output: 0 }, to: { node: 'n_check', input: 0 } },
      { from: { node: 'n_check', output: 0 }, to: { node: 'n_mail', input: 0 } },
      { from: { node: 'n_check', output: 1 }, to: { node: 'n_prep', input: 0 } },
    ],
  } as WorkflowDefinition;
}

function kit(): WorkflowDefinition {
  return structuredClone(kitApproval) as unknown as WorkflowDefinition;
}

function viewOf(def: WorkflowDefinition): BusinessView {
  return buildBusinessView(def, toCanvasModel(def, SAMPLE_DESCRIPTORS), CONNECTORS);
}

/** Визначення без жодних `business` — те, що бачить рушій. */
function technical(def: WorkflowDefinition): unknown {
  const strip = <T extends object>(o: T): T => {
    const copy = { ...o } as Record<string, unknown>;
    delete copy['business'];
    return copy as T;
  };
  return { ...strip(def), nodes: def.nodes.map(strip), connections: def.connections.map(strip) };
}

/** Правила валідатора F3-67: доріжки кроків оголошені, у кожної дії людини є зв'язки з наявними вузлами. */
function businessErrors(def: WorkflowDefinition): string[] {
  const lanes = new Set((def.business?.lanes ?? []).map((l) => l.id));
  const nodes = new Set(def.nodes.map((n) => n.id));
  const errors: string[] = [];
  for (const n of def.nodes) if (n.business?.lane && !lanes.has(n.business.lane)) errors.push(`lane:${n.id}`);
  for (const s of def.business?.steps ?? []) {
    if (!lanes.has(s.lane)) errors.push(`lane:${s.id}`);
    if (s.links.length === 0) errors.push(`links:${s.id}`);
    for (const l of s.links) if (!nodes.has(l.node)) errors.push(`node:${s.id}`);
  }
  return errors;
}

function expectBusinessOnly(before: WorkflowDefinition, after: WorkflowDefinition): void {
  expect(after).not.toBe(before);
  expect(technical(after)).toEqual(technical(before));
  expect(businessErrors(after)).toEqual([]);
}

describe('business-edit (F3-68)', () => {
  it('назва кроку вузла пишеться в business.title; порожня чи як name — прибирається', () => {
    const def = plain();
    const renamed = setStepTitle(def, 'n_check', '  Потрібне погодження?  ');
    expectBusinessOnly(def, renamed);
    expect(renamed.nodes[1].business).toEqual({ title: 'Потрібне погодження?' });
    expect(viewOf(renamed).steps.find((s) => s.id === 'n_check')?.title).toBe('Потрібне погодження?');

    expect(setStepTitle(renamed, 'n_check', '').nodes[1].business).toBeUndefined();
    expect(setStepTitle(renamed, 'n_check', 'Сума велика?').nodes[1].business).toBeUndefined();
    expect(setStepTitle(def, 'n_check', 'Сума велика?')).toBe(def);
  });

  it('назва й підрядок дії людини', () => {
    const def = kit();
    const next = setStepSubtitle(setStepTitle(def, 's_create', 'Автор подає заявку'), 's_create', 'форма заявки');
    expectBusinessOnly(def, next);
    expect(next.business?.steps?.[0]).toMatchObject({ title: 'Автор подає заявку', subtitle: 'форма заявки' });
    expect(setStepTitle(def, 's_create', ' ')).toBe(def);
    expect(setStepSubtitle(next, 's_create', '').business?.steps?.[0].subtitle).toBeUndefined();
  });

  it('підрядок вузла', () => {
    const def = plain();
    const next = setStepSubtitle(def, 'n_mail', 'пошта керівника');
    expectBusinessOnly(def, next);
    expect(next.nodes[2].business).toEqual({ subtitle: 'пошта керівника' });
  });

  it('перенесення в доріжку за замовчуванням оголошує всі видимі доріжки в поточному порядку', () => {
    const def = plain();
    const before = viewOf(def);
    expect(before.lanes.map((l) => l.id)).toEqual(['user', 'openbp', 'connector:mail']);

    const next = moveStepToLane(def, before, 'n_check', 'connector:mail');
    expectBusinessOnly(def, next);
    expect(next.business?.lanes?.map((l) => l.id)).toEqual(['user', 'openbp', 'connector:mail']);
    expect(next.nodes[1].business).toEqual({ lane: 'connector:mail' });
    const after = viewOf(next);
    expect(after.steps.find((s) => s.id === 'n_check')?.lane).toBe('connector:mail');
    expect(after.lanes.map((l) => l.name)).toEqual(['Користувач', 'AI_BPMS', 'Пошта']);
  });

  it('перенесення в оголошену доріжку не чіпає інших; порожня доріжка — повернути за типом', () => {
    const def = kit();
    const next = moveStepToLane(def, viewOf(def), 'n_assign', 'notify');
    expectBusinessOnly(def, next);
    expect(next.business?.lanes).toEqual(def.business?.lanes);
    expect(next.nodes.find((n) => n.id === 'n_assign')?.business).toEqual({ subtitle: 'kit-crm', lane: 'notify' });
    expect(moveStepToLane(next, viewOf(next), 'n_assign', '').nodes.find((n) => n.id === 'n_assign')?.business).toEqual({ subtitle: 'kit-crm' });

    const external = moveStepToLane(def, viewOf(def), 's_stage', 'openbp');
    expectBusinessOnly(def, external);
    expect(external.business?.steps?.find((s) => s.id === 's_stage')?.lane).toBe('openbp');
  });

  it('сховати й показати крок: hidden пишеться лише всупереч правилу за замовчуванням', () => {
    const def = plain();
    const hidden = setStepHidden(def, 'n_mail', true);
    expectBusinessOnly(def, hidden);
    expect(hidden.nodes[2].business).toEqual({ hidden: true });
    expect(viewOf(hidden).steps.some((s) => s.id === 'n_mail')).toBe(false);
    expect(setStepHidden(hidden, 'n_mail', false).nodes[2].business).toBeUndefined();

    const shown = setStepHidden(def, 'n_prep', false);
    expect(shown.nodes[3].business).toEqual({ hidden: false });
    expect(viewOf(shown).steps.some((s) => s.id === 'n_prep')).toBe(true);
    expect(setStepHidden(def, 'n_prep', true)).toBe(def);
  });

  it('нова дія людини з зв\'язком; видалення', () => {
    const def = plain();
    const { definition: next, stepId } = addExternalStep(def, viewOf(def), 'user', 'n_mail', 'Керівник погоджує', 'out');
    expectBusinessOnly(def, next);
    expect(stepId).toBe('s_1');
    expect(next.business?.steps).toEqual([{ id: 's_1', title: 'Керівник погоджує', lane: 'user', links: [{ node: 'n_mail', direction: 'out' }] }]);
    const view = viewOf(next);
    expect(view.steps.find((s) => s.id === 's_1')).toMatchObject({ kind: 'external', lane: 'user' });
    expect(view.edges.find((e) => e.to === 's_1')).toMatchObject({ from: 'n_mail', kind: 'write' });

    const removed = removeExternalStep(next, 's_1');
    expect(removed.business?.steps).toEqual([]);
    expect(removeExternalStep(def, 's_1')).toBe(def);
  });

  it('напрям і нові зв\'язки дії людини', () => {
    const def = kit();
    const toggled = toggleLinkDirection(def, 's_stage', 0);
    expectBusinessOnly(def, toggled);
    expect(toggled.business?.steps?.find((s) => s.id === 's_stage')?.links).toEqual([{ node: 'n_update_stage', direction: 'in' }]);

    const linked = linkExternalStep(def, 's_stage', 'n_notify_next', 'in');
    expectBusinessOnly(def, linked);
    expect(linked.business?.steps?.find((s) => s.id === 's_stage')?.links).toHaveLength(2);
    expect(linkExternalStep(linked, 's_stage', 'n_notify_next', 'in')).toBe(linked);
    expect(linkExternalStep(def, 's_stage', 'n_missing', 'in')).toBe(def);
  });

  it('підпис стрілки переходу й зв\'язку дії людини', () => {
    const def = plain();
    const view = viewOf(def);
    const yes = view.edges.find((e) => e.from === 'n_check')!;
    expect(yes.label).toBe('так');
    const labelled = setEdgeLabel(def, yes, 'більше 10 000');
    expectBusinessOnly(def, labelled);
    expect(labelled.connections[1].business).toEqual({ label: 'більше 10 000' });
    expect(viewOf(labelled).edges.find((e) => e.from === 'n_check')?.label).toBe('більше 10 000');
    expect(setEdgeLabel(labelled, yes, '').connections[1].business).toBeUndefined();

    const k = kit();
    const read = viewOf(k).edges.find((e) => e.from === 's_approve' || e.to === 's_approve')!;
    const next = setEdgeLabel(k, read, 'ставить задачу');
    expectBusinessOnly(k, next);
    expect(next.business?.steps?.find((s) => s.id === 's_approve')?.links[0].label).toBe('ставить задачу');
  });

  it('доріжки: перейменувати, підпис, порядок, нова', () => {
    const def = plain();
    const view = viewOf(def);
    const renamed = renameLane(def, view, 'openbp', 'Бухгалтерія');
    expectBusinessOnly(def, renamed);
    expect(viewOf(renamed).lanes.map((l) => l.name)).toEqual(['Користувач', 'Бухгалтерія', 'Пошта']);
    expect(renameLane(def, view, 'openbp', '  ')).toBe(def);

    const described = setLaneDescription(renamed, viewOf(renamed), 'openbp', 'сценарій');
    expect(described.business?.lanes?.[1]).toEqual({ id: 'openbp', name: 'Бухгалтерія', description: 'сценарій' });
    expect(setLaneDescription(described, viewOf(described), 'openbp', '').business?.lanes?.[1].description).toBeUndefined();

    const moved = moveLane(def, view, 'connector:mail', -1);
    expectBusinessOnly(def, moved);
    expect(viewOf(moved).lanes.map((l) => l.id)).toEqual(['user', 'connector:mail', 'openbp']);
    expect(moveLane(def, view, 'user', -1)).toBe(def);

    const { definition: added, laneId } = addLane(def, view, 'Кіт 2');
    expectBusinessOnly(def, added);
    expect(laneId).toBe('lane_4');
    expect(viewOf(added).lanes.map((l) => l.name)).toEqual(['Користувач', 'AI_BPMS', 'Пошта', 'Кіт 2']);
  });
});
