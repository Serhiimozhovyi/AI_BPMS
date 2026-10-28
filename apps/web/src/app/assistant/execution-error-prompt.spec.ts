import { ExecutionDetail, ExecutionStep } from '../executions/executions-api.service';
import { executionErrorPrompt, failedStep } from './execution-error-prompt';

const step = (nodeId: string, status: ExecutionStep['status'], error: ExecutionStep['error'] = null): ExecutionStep => ({
  id: nodeId,
  nodeId,
  runIndex: 0,
  status,
  input: [[{ json: { token: 'SECRET-IN-INPUT', phone: '+380501112233' } }]],
  output: [[{ json: { apiKey: 'SECRET-IN-OUTPUT' } }]],
  error,
  startedAt: '',
  finishedAt: '',
});

function detail(steps: ExecutionStep[], error: string | null = 'Крок упав'): ExecutionDetail {
  return {
    summary: { id: 'e1', workflowId: 'w1', versionId: 'v1', mode: 'manual', state: 'error', startedAt: null, finishedAt: null, error },
    steps,
  };
}

describe('executionErrorPrompt', () => {
  it('називає сценарій, виконання, вузол і текст помилки; вузол іде окремо для контексту', () => {
    const prompt = executionErrorPrompt(
      detail([step('n_start', 'success'), step('n_http', 'error', { message: 'HTTP 401', details: 'Unauthorized' })]),
      'Заявки',
    );
    expect(prompt.nodeId).toBe('n_http');
    expect(prompt.text).toContain('Розбери помилку виконання e1 сценарію «Заявки»');
    expect(prompt.text).toContain('Помилка виконання: Крок упав');
    expect(prompt.text).toContain('Упав вузол n_http (запуск 0): HTTP 401\nUnauthorized');
    expect(prompt.text).toContain('Поясни причину простими словами');
  });

  it('дані кроків (input/output) у запит не потрапляють', () => {
    const prompt = executionErrorPrompt(detail([step('n_http', 'error', { message: 'HTTP 401' })]), 'Заявки');
    for (const leaked of ['SECRET-IN-INPUT', 'SECRET-IN-OUTPUT', '+380501112233', 'token', 'apiKey']) {
      expect(prompt.text).not.toContain(leaked);
    }
  });

  it('довгий текст помилки скорочується', () => {
    const prompt = executionErrorPrompt(detail([step('n', 'error', { message: 'я'.repeat(5000) })]), 'З');
    expect(prompt.text.length).toBeLessThan(1400);
    expect(prompt.text).toContain('…');
  });

  it('без кроку з помилкою — лише помилка виконання, вузла немає', () => {
    const d = detail([step('n_start', 'success')]);
    expect(failedStep(d)).toBeUndefined();
    const prompt = executionErrorPrompt(d, 'З');
    expect(prompt.nodeId).toBeNull();
    expect(prompt.text).not.toContain('Упав вузол');
  });
});
