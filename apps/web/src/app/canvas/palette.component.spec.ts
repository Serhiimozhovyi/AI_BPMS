import { TestBed } from '@angular/core/testing';
import { PaletteItem, buildPalette } from './palette';
import { PaletteComponent } from './palette.component';
import { SAMPLE_DESCRIPTORS } from './sample-workflows';

describe('PaletteComponent', () => {
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
});
