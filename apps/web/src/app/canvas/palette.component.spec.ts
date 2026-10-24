import { TestBed } from '@angular/core/testing';
import { PaletteItem, buildPalette } from './palette';
import { PALETTE_COLLAPSE_KEY, PaletteComponent } from './palette.component';
import { SAMPLE_DESCRIPTORS } from './sample-workflows';

describe('PaletteComponent', () => {
  beforeEach(() => localStorage.removeItem(PALETTE_COLLAPSE_KEY));

  function items(host: HTMLElement, section: string): HTMLElement {
    return host.querySelector(`[data-section="${section}"] .palette__items`) as HTMLElement;
  }

  function toggle(host: HTMLElement, section: string): HTMLButtonElement {
    return host.querySelector(`[data-section="${section}"] [data-testid="palette-toggle"]`) as HTMLButtonElement;
  }

  function create() {
    const fixture = TestBed.createComponent(PaletteComponent);
    fixture.componentRef.setInput(
      'sections',
      buildPalette(SAMPLE_DESCRIPTORS, [{ id: 'c1', name: '1С', tools: [{ name: 'create_order', inputSchema: {} }] }]),
    );
    fixture.detectChanges();
    return fixture;
  }

  it('показує секції груп і серверів; клік по елементу віддає його', () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;
    const picked: PaletteItem[] = [];
    fixture.componentInstance.pick.subscribe((i) => picked.push(i));

    expect(Array.from(host.querySelectorAll('[data-section]')).map((s) => s.getAttribute('data-section'))).toContain('server:c1');
    (host.querySelector('[data-item="c1/create_order"]') as HTMLButtonElement).click();

    expect(picked.map((i) => [i.type, i.serverId, i.toolName])).toEqual([['mcp.tool', 'c1', 'create_order']]);
  });

  it('пошук фільтрує елементи; без збігів — повідомлення', async () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;
    const input = host.querySelector('[data-testid="palette-search"]') as HTMLInputElement;

    input.value = 'умова';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(Array.from(host.querySelectorAll('[data-item]')).map((b) => b.getAttribute('data-item'))).toEqual(['core.if']);

    input.value = 'zzz';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.querySelector('[data-testid="palette-empty"]')).not.toBeNull();
  });

  it('конектори за замовчуванням згорнуті до назви, групи core — розгорнуті; клік перемикає', () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;

    expect(items(host, 'server:c1').hidden).toBe(true);
    expect(toggle(host, 'server:c1').getAttribute('aria-expanded')).toBe('false');
    expect(toggle(host, 'server:c1').textContent).toContain('1С');
    expect(items(host, 'group:logic').hidden).toBe(false);

    toggle(host, 'server:c1').click();
    toggle(host, 'group:logic').click();
    fixture.detectChanges();

    expect(items(host, 'server:c1').hidden).toBe(false);
    expect(items(host, 'group:logic').hidden).toBe(true);
    expect(JSON.parse(localStorage.getItem(PALETTE_COLLAPSE_KEY)!)).toEqual({ 'server:c1': false, 'group:logic': true });
  });

  it('стан згортання відновлюється з localStorage', () => {
    localStorage.setItem(PALETTE_COLLAPSE_KEY, JSON.stringify({ 'server:c1': false, 'group:trigger': true }));
    const host = create().nativeElement as HTMLElement;

    expect(items(host, 'server:c1').hidden).toBe(false);
    expect(items(host, 'group:trigger').hidden).toBe(true);
  });

  it('пошук розгортає знайдені секції, зокрема згорнуті конектори', async () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;
    const input = host.querySelector('[data-testid="palette-search"]') as HTMLInputElement;

    input.value = 'create';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(items(host, 'server:c1').hidden).toBe(false);
    expect(toggle(host, 'server:c1').getAttribute('aria-expanded')).toBe('true');
  });
});
