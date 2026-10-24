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

  it('показує секції груп і конекторів; клік по конектору віддає його з дією за замовчуванням', () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;
    const picked: PaletteItem[] = [];
    fixture.componentInstance.pick.subscribe((i) => picked.push(i));

    expect(Array.from(host.querySelectorAll('[data-section]')).map((s) => s.getAttribute('data-section'))).toContain('connectors');
    expect(host.querySelector('[data-item="connector:c1"]')?.textContent).toContain('1 дій');
    (host.querySelector('[data-item="connector:c1"]') as HTMLButtonElement).click();

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

  it('секції розгорнуті за замовчуванням; клік згортає і пам’ятається', () => {
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;

    expect(items(host, 'connectors').hidden).toBe(false);
    expect(toggle(host, 'connectors').textContent).toContain('Конектори');
    expect(items(host, 'group:logic').hidden).toBe(false);

    toggle(host, 'connectors').click();
    toggle(host, 'group:logic').click();
    fixture.detectChanges();

    expect(items(host, 'connectors').hidden).toBe(true);
    expect(toggle(host, 'connectors').getAttribute('aria-expanded')).toBe('false');
    expect(items(host, 'group:logic').hidden).toBe(true);
    expect(JSON.parse(localStorage.getItem(PALETTE_COLLAPSE_KEY)!)).toEqual({ connectors: true, 'group:logic': true });
  });

  it('стан згортання відновлюється з localStorage', () => {
    localStorage.setItem(PALETTE_COLLAPSE_KEY, JSON.stringify({ connectors: true, 'group:trigger': true }));
    const host = create().nativeElement as HTMLElement;

    expect(items(host, 'connectors').hidden).toBe(true);
    expect(items(host, 'group:trigger').hidden).toBe(true);
  });

  it('пошук розгортає знайдені секції і показує дії конектора', async () => {
    localStorage.setItem(PALETTE_COLLAPSE_KEY, JSON.stringify({ connectors: true }));
    const fixture = create();
    const host = fixture.nativeElement as HTMLElement;
    const input = host.querySelector('[data-testid="palette-search"]') as HTMLInputElement;

    input.value = 'create';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(items(host, 'connectors').hidden).toBe(false);
    expect(toggle(host, 'connectors').getAttribute('aria-expanded')).toBe('true');
    expect(Array.from(host.querySelectorAll('[data-section="connectors"] [data-item]')).map((b) => b.getAttribute('data-item'))).toEqual(['c1/create_order']);
  });
});
