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
});
