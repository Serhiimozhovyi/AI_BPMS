import { ExecutionStep } from '../executions/executions-api.service';
import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { ancestorIds, applyEvent, isRunning, nodeStatuses, runOutputs, singleNodeRequest, startedRun, streamFailed } from './test-run';

function step(nodeId: string, id: string, status: ExecutionStep['status'] = 'success', runIndex = 0, output: ExecutionStep['output'] = [[{ json: { node: nodeId } }]]): ExecutionStep {
  return { id, nodeId, runIndex, status, input: [], output, error: status === 'error' ? { message: 'збій' } : null, startedAt: '', finishedAt: '' };
}

describe('test-run', () => {
  it('кроки з SSE: статуси вузлів і перехід queued → running на першому кроці', () => {
    let run = startedRun('e1');
    expect(isRunning(run)).toBe(true);
    run = applyEvent(run, { kind: 'step', step: step('a', 's1') });
    expect(run.state).toBe('running');
    run = applyEvent(run, { kind: 'step', step: step('b', 's2', 'error') });
    expect([...nodeStatuses(run)]).toEqual([
      ['a', 'success'],
      ['b', 'error'],
    ]);
  });

  it('фінальний status завершує запуск і несе помилку виконання', () => {
    let run = applyEvent(startedRun('e1'), { kind: 'status', executionId: 'e1', state: 'running', error: null });
    expect(run.state).toBe('running');
    run = applyEvent(run, { kind: 'status', executionId: 'e1', state: 'error', error: 'вузол b' });
    expect(run).toMatchObject({ state: 'error', error: 'вузол b' });
    expect(isRunning(run)).toBe(false);
  });

  it('ігнорує події іншого виконання, повтор того самого кроку і старіший runIndex', () => {
    const run = applyEvent(startedRun('e1'), { kind: 'step', step: step('loop', 's2', 'success', 1) });
    expect(applyEvent(run, { kind: 'status', executionId: 'e2', state: 'success', error: null })).toBe(run);
    expect(applyEvent(run, { kind: 'step', step: step('loop', 's2', 'success', 1) })).toBe(run);
    expect(applyEvent(run, { kind: 'step', step: step('loop', 's1', 'success', 0) })).toBe(run);
    expect(applyEvent(run, { kind: 'step', step: step('loop', 's3', 'success', 2) }).steps.get('loop')?.id).toBe('s3');
  });

  it('обрив потоку до фіналу зупиняє «виконується», після фіналу нічого не змінює', () => {
    const failed = streamFailed(startedRun('e1', 'running'), 'обрив');
    expect(failed.error).toBe('обрив');
    expect(isRunning(failed)).toBe(false);
    const done = applyEvent(startedRun('e1'), { kind: 'status', executionId: 'e1', state: 'success', error: null });
    expect(streamFailed(done, 'обрив')).toBe(done);
    expect(isRunning(null)).toBe(false);
  });

  it('runOutputs: вихід 0 успішних вузлів — для підказок виразів', () => {
    let run = applyEvent(startedRun('e1'), { kind: 'step', step: step('a', 's1') });
    run = applyEvent(run, { kind: 'step', step: step('b', 's2', 'error', 0, []) });
    run = applyEvent(run, { kind: 'step', step: step('c', 's3', 'success', 0, []) });
    expect(runOutputs(run)).toEqual({ a: [{ json: { node: 'a' } }], c: [] });
    expect(runOutputs(null)).toEqual({});
  });

  it('новий запуск зберігає кроки попереднього, але приймає для них нові кроки (навіть з тим самим runIndex)', () => {
    const first = applyEvent(applyEvent(startedRun('e1'), { kind: 'step', step: step('a', 's1') }), { kind: 'step', step: step('b', 's2') });
    let next = startedRun('e2', 'queued', first.steps);
    expect([...nodeStatuses(next).keys()]).toEqual(['a', 'b']);
    next = applyEvent(next, { kind: 'step', step: step('b', 's3', 'error') });
    expect(next.steps.get('b')?.id).toBe('s3');
    expect(next.steps.get('a')?.id).toBe('s1');
    expect([...next.current]).toEqual(['b']);
  });

  it('singleNodeRequest: лише попередники з успіхом/згаслою гілкою йдуть у runData, по всіх портах', () => {
    const definition: WorkflowDefinition = {
      ...emptyWorkflowDefinition('w'),
      connections: [
        { from: { node: 'a', output: 0 }, to: { node: 'b', input: 0 } },
        { from: { node: 'b', output: 1 }, to: { node: 'c', input: 0 } },
        { from: { node: 'x', output: 0 }, to: { node: 'c', input: 0 } },
        { from: { node: 'c', output: 0 }, to: { node: 'd', input: 0 } },
      ],
    } as WorkflowDefinition;
    expect([...ancestorIds(definition, 'c')].sort()).toEqual(['a', 'b', 'x']);
    expect(ancestorIds(definition, 'a').size).toBe(0);

    let run = startedRun('e1');
    run = applyEvent(run, { kind: 'step', step: step('a', 's1') });
    run = applyEvent(run, { kind: 'step', step: step('b', 's2', 'success', 0, [[], [{ json: { yes: true } }]]) });
    run = applyEvent(run, { kind: 'step', step: step('x', 's3', 'error') });
    run = applyEvent(run, { kind: 'step', step: step('d', 's4') });
    expect(singleNodeRequest(definition, run, 'c')).toEqual({
      destinationNodeId: 'c',
      runData: { a: [[{ json: { node: 'a' } }]], b: [[], [{ json: { yes: true } }]] },
    });
    expect(singleNodeRequest(definition, null, 'c')).toEqual({ destinationNodeId: 'c', runData: {} });
  });
});
