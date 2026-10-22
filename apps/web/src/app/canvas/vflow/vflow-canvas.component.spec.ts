import { TestBed } from '@angular/core/testing';
import { demoGraph } from '../spike-graph';
import { VflowCanvasComponent } from './vflow-canvas.component';

describe('VflowCanvasComponent', () => {
  function create(virtualization = false) {
    const fixture = TestBed.createComponent(VflowCanvasComponent);
    fixture.componentRef.setInput('graph', demoGraph());
    fixture.componentRef.setInput('virtualization', virtualization);
    fixture.detectChanges();
    return fixture;
  }

  it('рендерить картку для кожного вузла демо-графа', async () => {
    const fixture = create();
    await fixture.whenStable();

    const cards = (fixture.nativeElement as HTMLElement).querySelectorAll('[data-node-id]');
    expect(Array.from(cards).map((c) => c.getAttribute('data-node-id'))).toEqual(['n_webhook', 'n_llm', 'n_1c']);
    expect(cards[0].textContent).toContain('Нова заявка');
    expect(cards[0].textContent).toContain('core.webhook');
  });

  it('тригер без входу не має вхідного порту, а вузол з двома виходами — два вихідні', async () => {
    const fixture = create();
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;

    const trigger = host.querySelector('[data-node-id="n_webhook"]')!;
    const tool = host.querySelector('[data-node-id="n_1c"]')!;
    expect(trigger.querySelectorAll('handle[type="target"], handle').length).toBe(1);
    expect(tool.querySelectorAll('handle').length).toBe(3);
    expect(tool.textContent).toContain('error');
  });

  it('передає ребра демо-графа у vflow', async () => {
    const fixture = create();
    await fixture.whenStable();

    const edges = (fixture.componentInstance as unknown as { edges: () => unknown[] }).edges();
    expect(edges).toHaveLength(2);
  });
});
