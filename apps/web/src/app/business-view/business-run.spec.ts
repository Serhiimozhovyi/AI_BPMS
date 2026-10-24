import { toCanvasModel } from '../canvas/canvas-model';
import { SAMPLE_DESCRIPTORS } from '../canvas/sample-workflows';
import { ExecutionStep } from '../executions/executions-api.service';
import { WorkflowDefinition, WorkflowNode, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { buildBusinessView } from './business-model';
import { BusinessRun, businessRunStatuses } from './business-run';

function node(id: string, type: string): WorkflowNode {
  return { id, name: id, type, typeVersion: 1, position: [0, 0], parameters: {} };
}

/** t → if → (так) s(set) → a ; (ні) b. Плюс дія людини h, що її читає a. */
const definition: WorkflowDefinition = {
  ...emptyWorkflowDefinition('t'),
  nodes: [node('t', 'core.manualTrigger'), node('if', 'core.if'), node('s', 'core.set'), node('a', 'mcp.tool'), node('b', 'mcp.tool')],
  connections: [
    { from: { node: 't', output: 0 }, to: { node: 'if', input: 0 } },
    { from: { node: 'if', output: 0 }, to: { node: 's', input: 0 } },
    { from: { node: 's', output: 0 }, to: { node: 'a', input: 0 } },
    { from: { node: 'if', output: 1 }, to: { node: 'b', input: 0 } },
  ],
  business: { steps: [{ id: 'h', title: 'Людина', lane: 'x', links: [{ node: 'a', direction: 'in' }] }] },
};

function step(nodeId: string, status: ExecutionStep['status'], output: number[] = [1], runIndex = 0): ExecutionStep {
  return {
    id: `${nodeId}:${runIndex}`,
    nodeId,
    runIndex,
    status,
    input: [],
    output: output.map((n) => Array.from({ length: n }, () => ({ json: {} }))),
    error: status === 'error' ? { message: 'x' } : null,
    startedAt: '2026-10-24T10:00:00Z',
    finishedAt: '2026-10-24T10:00:01.500Z',
  };
}

function statuses(run: BusinessRun | null) {
  const model = toCanvasModel(definition, SAMPLE_DESCRIPTORS);
  const result = businessRunStatuses(buildBusinessView(definition, model), model, run);
  return Object.fromEntries([...result].map(([id, info]) => [id, info.status]));
}

describe('businessRunStatuses', () => {
  it('без виконання — порожньо', () => {
    expect(statuses(null)).toEqual({});
  });

  it('завершене виконання гілкою «так»: пройдені кроки, гілка «ні» не дійшла, дія людини пройдена', () => {
    const run: BusinessRun = { state: 'success', steps: [step('t', 'success'), step('if', 'success', [1, 0]), step('s', 'success'), step('a', 'success')] };
    expect(statuses(run)).toEqual({ t: 'done', if: 'done', a: 'done', b: 'pending', h: 'done' });
  });

  it('триває: наступний крок за непорожнім виходом — «виконується», порожня гілка — ні', () => {
    const run: BusinessRun = { state: 'running', steps: [step('t', 'success'), step('if', 'success', [0, 2])] };
    expect(statuses(run)).toEqual({ t: 'done', if: 'done', a: 'pending', b: 'running', h: 'pending' });
  });

  it('стан waiting — «чекає»', () => {
    const run: BusinessRun = { state: 'waiting', steps: [step('t', 'success')] };
    expect(statuses(run)['if']).toBe('waiting');
  });

  it('збій згорнутого вузла — на найближчому видимому кроці перед ним', () => {
    const run: BusinessRun = { state: 'error', steps: [step('t', 'success'), step('if', 'success', [1, 0]), step('s', 'error', [])] };
    expect(statuses(run)).toEqual({ t: 'done', if: 'error', a: 'pending', b: 'pending', h: 'pending' });
  });

  it('цикл: береться останній прохід вузла', () => {
    const run: BusinessRun = { state: 'error', steps: [step('t', 'success'), step('if', 'success', [1, 0], 0), step('if', 'error', [], 1)] };
    expect(statuses(run)['if']).toBe('error');
  });
});
