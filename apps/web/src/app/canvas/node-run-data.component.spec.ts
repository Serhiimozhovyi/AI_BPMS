import { TestBed } from '@angular/core/testing';
import { ExecutionStep } from '../executions/executions-api.service';
import { NodeRunDataComponent } from './node-run-data.component';

function render(step: ExecutionStep, outputNames: string[] = ['main'], inputNames: string[] = ['main']) {
  const fixture = TestBed.createComponent(NodeRunDataComponent);
  fixture.componentRef.setInput('step', step);
  fixture.componentRef.setInput('inputNames', inputNames);
  fixture.componentRef.setInput('outputNames', outputNames);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('NodeRunDataComponent', () => {
  it('показує input і output кожного порту як JSON з назвами портів', () => {
    const host = render(
      {
        id: 's1',
        nodeId: 'n_if',
        runIndex: 0,
        status: 'success',
        input: [[{ json: { qty: 2 } }]],
        output: [[{ json: { qty: 2 } }], []],
        error: null,
        startedAt: '',
        finishedAt: '',
      },
      ['true', 'false'],
    );
    expect(host.querySelector('[data-testid="node-run"]')?.getAttribute('data-status')).toBe('success');
    const inputs = host.querySelectorAll('[data-testid="node-run-input"]');
    expect(inputs).toHaveLength(1);
    expect(inputs[0].textContent).toContain('main · 1');
    expect(inputs[0].textContent).toContain('"qty": 2');
    const outputs = [...host.querySelectorAll('[data-testid="node-run-output"]')].map((e) => e.textContent ?? '');
    expect(outputs[0]).toContain('true · 1');
    expect(outputs[1]).toContain('false · 0');
  });

  it('помилка кроку і додатковий порт error для continueOnFail', () => {
    const host = render({
      id: 's1',
      nodeId: 'n_1c',
      runIndex: 0,
      status: 'error',
      input: [[{ json: {} }]],
      output: [[], [{ json: { error: 'timeout' } }]],
      error: { message: 'MCP: timeout' },
      startedAt: '',
      finishedAt: '',
    });
    expect(host.querySelector('[data-testid="node-run-error"]')?.textContent).toContain('MCP: timeout');
    expect(host.querySelector('[data-testid="node-run-status"]')?.textContent).toContain('помилка');
    expect(host.querySelectorAll('[data-testid="node-run-output"]')[1].textContent).toContain('error · 1');
  });

  it('помилка (F3-57): повний текст, item, технічні подробиці й кнопка до вхідних даних', () => {
    const host = render({
      id: 's1',
      nodeId: 'n_1c',
      runIndex: 0,
      status: 'error',
      input: [[{ json: {} }, { json: {} }]],
      output: [[]],
      error: { message: 'Поле «text» порожнє [item 1]. Вкажіть значення.', details: 'HTTP 400: text is required', itemIndex: 1 },
      startedAt: '',
      finishedAt: '',
    });
    expect(host.querySelector('[data-testid="node-run-error-message"]')?.textContent).toBe('Поле «text» порожнє [item 1]. Вкажіть значення.');
    expect(host.querySelector('[data-testid="node-run-error-item"]')?.textContent).toContain('item 1');
    expect(host.querySelector('[data-testid="node-run-error-details"]')?.textContent).toContain('HTTP 400');
    expect(host.querySelector('[data-testid="node-run-error-input"]')).not.toBeNull();
  });

  it('без назв портів (журнал виконань) нумерує порти, а не називає перший error', () => {
    const host = render(
      {
        id: 's1',
        nodeId: 'n_set',
        runIndex: 0,
        status: 'success',
        input: [[{ json: {} }]],
        output: [[{ json: { a: 1 } }]],
        error: null,
        startedAt: '',
        finishedAt: '',
      },
      [],
      [],
    );
    expect(host.querySelector('[data-testid="node-run-input"]')?.textContent).toContain('#0 · 1');
    expect(host.querySelector('[data-testid="node-run-output"]')?.textContent).toContain('#0 · 1');
    expect(host.textContent).not.toContain('error');
  });
  it('кроки агента: модель і дії з аргументами, результатом, помилкою й токенами', () => {
    const host = render({
      id: 's2',
      nodeId: 'n_agent',
      runIndex: 0,
      status: 'error',
      input: [[{ json: {} }]],
      output: [[]],
      error: { message: 'Агент не дав відповіді за 2 ітерацій (maxIterations).' },
      startedAt: '2026-10-24T10:00:00Z',
      finishedAt: '2026-10-24T10:00:03Z',
      subSteps: [
        {
          kind: 'llm',
          name: 'qwen',
          startedAt: '2026-10-24T10:00:00.000Z',
          finishedAt: '2026-10-24T10:00:01.500Z',
          output: { toolCalls: [{ name: 'find', arguments: { q: 'Агро' } }] },
          iteration: 1,
          usage: { input: 30, output: 12 },
        },
        {
          kind: 'tool',
          name: 'CRM / find',
          startedAt: '2026-10-24T10:00:01.500Z',
          finishedAt: '2026-10-24T10:00:01.700Z',
          input: { q: 'Агро' },
          error: 'Дія «find» не виконалась: timeout',
          iteration: 1,
        },
      ],
    });
    const list = host.querySelector('[data-testid="node-run-substeps"]')!;
    const items = list.querySelectorAll('li');
    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain('модель');
    expect(items[0].textContent).toContain('qwen');
    expect(items[0].textContent).toContain('1,5 с');
    expect(items[0].textContent).toContain('42 токенів');
    expect(items[1].getAttribute('data-kind')).toBe('tool');
    expect(items[1].textContent).toContain('200 мс');
    expect(items[1].textContent).toContain('"q": "Агро"');
    expect(items[1].querySelector('[data-testid="node-run-substep-error"]')?.textContent).toContain('timeout');
  });

  it('без підкроків блоку кроків агента немає', () => {
    const host = render({ id: 's3', nodeId: 'n', runIndex: 0, status: 'success', input: [], output: [], error: null, startedAt: '', finishedAt: '' });
    expect(host.querySelector('[data-testid="node-run-substeps"]')).toBeNull();
  });
});
