import { ExecutionStep } from '../executions/executions-api.service';
import { applyEvent, isRunning, nodeStatuses, runOutputs, startedRun, streamFailed } from './test-run';

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
});
