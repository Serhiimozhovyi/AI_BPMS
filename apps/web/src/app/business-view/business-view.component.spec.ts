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
    expect(lanes).toEqual(['Кіт 2 люди у веб-формах', 'Зовнішня система', 'AI_BPMS', 'Кіт CRM']);
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

  describe('правки (F3-68)', () => {
    function editable() {
      const r = render();
      r.fixture.componentRef.setInput('editable', true);
      r.fixture.detectChanges();
      const changes: WorkflowDefinition[] = [];
      r.fixture.componentInstance.definitionChange.subscribe((d) => changes.push(d));
      return { ...r, changes };
    }

    function inline(host: HTMLElement): HTMLInputElement {
      return host.querySelector('[data-testid="business-inline"]') as HTMLInputElement;
    }

    function submit(fixture: ReturnType<typeof render>['fixture'], host: HTMLElement, value: string): void {
      const input = inline(host);
      input.value = value;
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      fixture.detectChanges();
    }

    it('подвійний клік на крок — назва; Enter пише business.title вузла', () => {
      const { fixture, host, changes } = editable();
      host.querySelector('[data-step="k"]')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      fixture.detectChanges();
      expect(inline(host).value).toBe('Задача виконавцю');
      submit(fixture, host, 'Доручити виконавцю');
      expect(changes).toHaveLength(1);
      expect(changes[0].nodes.find((n) => n.id === 'k')?.business).toEqual({ title: 'Доручити виконавцю' });
      expect(changes[0].connections).toBe(definition.connections);
      expect(inline(host)).toBeNull();
    });

    it('Escape скасовує редагування', () => {
      const { fixture, host, changes } = editable();
      host.querySelector('[data-step="k"]')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      fixture.detectChanges();
      inline(host).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      fixture.detectChanges();
      expect(inline(host)).toBeNull();
      expect(changes).toEqual([]);
    });

    it('подвійний клік на стрілку — підпис ребра', () => {
      const { fixture, host, changes } = editable();
      host.querySelector('[data-testid="business-edge"][data-from="if"]')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      fixture.detectChanges();
      expect(inline(host).value).toBe('так');
      submit(fixture, host, 'погоджено');
      expect(changes[0].connections[1].business).toEqual({ label: 'погоджено' });
    });

    it('меню кроку: перенести в доріжку', () => {
      const { fixture, host, changes } = editable();
      host.querySelector('[data-step="k"]')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
      fixture.detectChanges();
      const items = [...host.querySelectorAll('[data-testid^="business-menu-"]')].map((b) => b.getAttribute('data-testid'));
      expect(items).toEqual(['business-menu-rename', 'business-menu-subtitle', 'business-menu-move', 'business-menu-human', 'business-menu-hide', 'business-menu-open']);
      (host.querySelector('[data-testid="business-menu-move"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      (host.querySelector('[data-testid="business-menu-lane-kit-lane"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(changes[0].nodes.find((n) => n.id === 'k')?.business).toEqual({ lane: 'kit-lane' });
      expect(host.querySelector('[data-testid="business-menu"]')).toBeNull();
    });

    it('меню кроку: дія людини — новий крок з зв\'язком, назва одразу на редагуванні', () => {
      const { fixture, host, changes } = editable();
      host.querySelector('[data-step="k"]')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
      fixture.detectChanges();
      (host.querySelector('[data-testid="business-menu-human"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      (host.querySelector('[data-testid="business-menu-lane-kit-lane"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      const added = changes[0].business?.steps?.at(-1);
      expect(added).toMatchObject({ lane: 'kit-lane', links: [{ node: 'k', direction: 'in' }] });
      fixture.componentRef.setInput('definition', changes[0]);
      fixture.detectChanges();
      TestBed.tick();
      fixture.detectChanges();
      expect(inline(host)?.value).toBe('Нова дія людини');
    });

    it('меню дії людини: напрям і видалення', () => {
      const { fixture, host, changes } = editable();
      host.querySelector('[data-step="h"]')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
      fixture.detectChanges();
      (host.querySelector('[data-testid="business-menu-direction"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      (host.querySelector('[data-testid="business-menu-link-0"]') as HTMLButtonElement).click();
      expect(changes[0].business?.steps?.[0].links[0].direction).toBe('out');

      host.querySelector('[data-step="h"]')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
      fixture.detectChanges();
      (host.querySelector('[data-testid="business-menu-remove"]') as HTMLButtonElement).click();
      expect(changes[1].business?.steps).toEqual([]);
    });

    it('меню доріжки: нижче й перейменування доріжки за замовчуванням', () => {
      const { fixture, host, changes } = editable();
      host.querySelector('[data-lane="kit-lane"]')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
      fixture.detectChanges();
      expect(host.querySelector('[data-testid="business-menu-up"]')).toBeNull();
      (host.querySelector('[data-testid="business-menu-down"]') as HTMLButtonElement).click();
      expect(changes[0].business?.lanes?.map((l) => l.id)).toEqual(['external', 'kit-lane', 'openbp', 'connector:kit']);

      host.querySelector('[data-lane="openbp"] [data-testid="business-lane-name"]')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      fixture.detectChanges();
      submit(fixture, host, 'Сценарій');
      expect(changes[1].business?.lanes?.find((l) => l.id === 'openbp')?.name).toBe('Сценарій');
    });

    it('перетягування кроку в іншу доріжку', () => {
      const { fixture, host, changes } = editable();
      const step = host.querySelector('[data-step="k"]')!;
      const lane = host.querySelector('[data-lane="kit-lane"] rect')!;
      const targetY = Number(lane.getAttribute('y')) + 40 + 16;
      step.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, button: 0, clientX: 300, clientY: 400 }));
      const viewport = host.querySelector('[data-testid="business-view"]')!;
      viewport.dispatchEvent(new MouseEvent('pointermove', { bubbles: true, clientX: 300, clientY: targetY }));
      fixture.detectChanges();
      expect(step.getAttribute('transform')).toContain('translate');
      viewport.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 300, clientY: targetY }));
      fixture.detectChanges();
      expect(changes[0].nodes.find((n) => n.id === 'k')?.business).toEqual({ lane: 'kit-lane' });
      expect(step.getAttribute('transform')).toBeNull();
    });

    it('без editable — ні меню, ні редагування', () => {
      const { fixture, host } = render();
      const changes: WorkflowDefinition[] = [];
      fixture.componentInstance.definitionChange.subscribe((d) => changes.push(d));
      host.querySelector('[data-step="k"]')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      host.querySelector('[data-step="k"]')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
      fixture.detectChanges();
      expect(inline(host)).toBeNull();
      expect(host.querySelector('[data-testid="business-menu"]')).toBeNull();
      expect(host.querySelector('[data-testid="business-add-lane"]')).toBeNull();
      expect(changes).toEqual([]);
    });
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
