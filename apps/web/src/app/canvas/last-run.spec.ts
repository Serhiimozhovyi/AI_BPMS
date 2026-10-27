import { ExecutionDetail, ExecutionStep } from '../executions/executions-api.service';
import { lastRunFromDetail, lastRunLabel, lastRunTimes } from './last-run';
import { nodeRunErrors, nodeStatuses } from './test-run';

function step(nodeId: string, runIndex: number, status: ExecutionStep['status'], finishedAt = '2026-10-27T18:10:42', message?: string): ExecutionStep {
  return {
    id: `${nodeId}-${runIndex}`,
    nodeId,
    runIndex,
    status,
    input: [],
    output: [],
    error: message ? { message } : null,
    startedAt: '2026-10-27T18:10:40',
    finishedAt,
  } as ExecutionStep;
}

const detail: ExecutionDetail = {
  summary: { id: 'e1', workflowId: 'w1', versionId: 'v1', mode: 'manual', state: 'error', startedAt: '2026-10-27T18:10:40', finishedAt: '2026-10-27T18:10:42', error: 'boom' },
  steps: [step('a', 0, 'success'), step('b', 0, 'success'), step('b', 1, 'error', '2026-10-27T18:10:43', 'Порахувати: ділення на нуль'), step('c', 0, 'skipped')],
};

describe('last-run', () => {
  it('lastRunFromDetail: лишається крок з найбільшим runIndex і мапиться на статуси та помилки вузлів', () => {
    const { run, summary } = lastRunFromDetail(detail);
    expect(summary.id).toBe('e1');
    expect(nodeStatuses(run).get('b')).toBe('error');
    expect(nodeStatuses(run).get('c')).toBe('skipped');
    expect(nodeRunErrors(run).get('b')).toBe('Порахувати: ділення на нуль');
    expect([...run.current].sort()).toEqual(['a', 'b', 'c']);
  });

  it('lastRunTimes: час завершення кроку', () => {
    const { run } = lastRunFromDetail(detail);
    expect(lastRunTimes(run).get('a')).toBe('18:10:42');
    expect(lastRunTimes(run).get('b')).toBe('18:10:43');
    expect(lastRunTimes(null).size).toBe(0);
  });

  it('lastRunLabel: сьогодні — час і стан, раніше — з датою', () => {
    const now = new Date('2026-10-27T20:00:00');
    expect(lastRunLabel(detail.summary, now)).toBe('18:10, помилка');
    expect(lastRunLabel({ ...detail.summary, state: 'success', finishedAt: '2026-10-26T09:05:00' }, now)).toBe('26.10 09:05, успішно');
    expect(lastRunLabel({ state: 'queued', startedAt: null, finishedAt: null }, now)).toBe('у черзі');
  });
});
