import { TestBed } from '@angular/core/testing';
import { SAMPLE_DESCRIPTORS } from '../canvas/sample-workflows';
import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { BusinessViewComponent, wrapTitle } from './business-view.component';
import { standaloneSvg } from './business-export';

const definition: WorkflowDefinition = {
  ...emptyWorkflowDefinition('Погодження'),
  nodes: [
    { id: 't', name: 'Нова задача', type: 'core.webhook', typeVersion: 1, position: [0, 0], parameters: {} },
    { id: 'if', name: 'Погоджено?', type: 'core.if', typeVersion: 1, position: [0, 0], parameters: {} },
    { id: 'k', name: 'Задача виконавцю', type: 'mcp.tool', typeVersion: 1, position: [0, 0], parameters: { serverId: 'kit' } },
    { id: 's', name: 'Поля', type: 'core.set', typeVersion: 1, position: [0, 0], parameters: {} },
  ],
  connections: [
    { from: { node: 't', output: 0 }, to: { node: 'if', input: 0 } },
    { from: { node: 'if', output: 0 }, to: { node: 's', input: 0 } },
    { from: { node: 's', output: 0 }, to: { node: 'k', input: 0 } },
  ],
  business: {
    steps: [{ id: 'h', title: 'Погоджувач тисне «Погодити»', lane: 'kit-lane', links: [{ node: 'if', direction: 'in', label: 'читає статус' }] }],
    lanes: [{ id: 'kit-lane', name: 'Кіт 2', description: 'люди у веб-формах' }],
  },
};

describe('BusinessViewComponent', () => {
  function render() {
    const fixture = TestBed.createComponent(BusinessViewComponent);
    fixture.componentRef.setInput('definition', definition);
    fixture.componentRef.setInput('descriptors', SAMPLE_DESCRIPTORS);
    fixture.componentRef.setInput('connectors', [{ id: 'kit', name: 'Кіт CRM' }]);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('доріжки з підписами, кроки без технічних вузлів, підписи стрілок і легенда', () => {
    const { host } = render();
    const lanes = [...host.querySelectorAll('[data-testid="business-lane"]')].map((l) =>
      [...l.querySelectorAll('text')].map((t) => t.textContent?.trim()).join(' '),
    );
    expect(lanes).toEqual(['Кіт 2 люди у веб-формах', 'Зовнішня система', 'OpenBP', 'Кіт CRM']);
    const steps = [...host.querySelectorAll('[data-testid="business-step"]')].map((s) => [s.getAttribute('data-step'), s.getAttribute('data-kind')]);
    expect(steps).toEqual([
      ['t', 'trigger'],
      ['if', 'decision'],
      ['k', 'task'],
      ['h', 'external'],
    ]);
    expect(host.textContent).not.toContain('core.');
    const labels = [...host.querySelectorAll('[data-testid="business-edge"] text')].map((t) => t.textContent);
    expect(labels).toEqual(['так', 'читає статус']);
    const legend = host.querySelector('[data-testid="business-legend"]')?.textContent ?? '';
    expect(legend).toContain('дія людини в іншій системі');
    expect(legend).toContain('рішення');
    expect(legend).not.toContain('таймаут');
  });

  it('клік на крок вузла повідомляє id вузла; зовнішній крок — ні', () => {
    const { fixture, host } = render();
    const picked: string[] = [];
    fixture.componentInstance.stepPick.subscribe((id) => picked.push(id));
    (host.querySelector('[data-step="k"]') as SVGGElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));
    (host.querySelector('[data-step="h"]') as SVGGElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(picked).toEqual(['k']);
  });

  it('експорт SVG: самодостатній файл без класів і трансформації екрана', () => {
    const { host } = render();
    const svg = host.querySelector('svg') as SVGSVGElement;
    svg.style.transform = 'scale(2)';
    const text = standaloneSvg(svg);
    expect(text.startsWith('<?xml')).toBe(true);
    expect(text).toContain('Погоджувач тисне');
    expect(text).not.toContain('class=');
    expect(text).not.toContain('scale(2)');
    expect(text).toContain('xmlns="http://www.w3.org/2000/svg"');
  });

  it('порожній сценарій — підказка', () => {
    const fixture = TestBed.createComponent(BusinessViewComponent);
    fixture.componentRef.setInput('definition', emptyWorkflowDefinition('x'));
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="business-empty"]')).not.toBeNull();
  });
});

describe('wrapTitle', () => {
  it('ділить по словах і обрізає зайве', () => {
    expect(wrapTitle('Погоджувач тисне «Погодити» у формі', 22, 3)).toEqual(['Погоджувач тисне', '«Погодити» у формі']);
    expect(wrapTitle('один два три чотири пять шість', 8, 2)).toEqual(['один два', 'три…']);
    expect(wrapTitle('Наддовгеслововякеневлазить', 10, 2)).toEqual(['Наддовгес…']);
  });
});
