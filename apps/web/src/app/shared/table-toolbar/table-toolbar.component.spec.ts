import { TestBed } from '@angular/core/testing';
import { TableToolbarComponent } from './table-toolbar.component';

describe('TableToolbarComponent', () => {
  function setup(inputs: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(TableToolbarComponent);
    for (const [key, value] of Object.entries(inputs)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('без налаштувань показує лише порожню панель', () => {
    const { host } = setup();
    expect(host.querySelector('[role="toolbar"]')).not.toBeNull();
    expect(host.querySelector('.bp-toolbar__view')).toBeNull();
    expect(host.querySelector('input[type="search"]')).toBeNull();
    expect(host.querySelector('.bp-toolbar__tools button')).toBeNull();
  });

  it('перемикач вигляду змінює view і aria-pressed', () => {
    const { fixture, host } = setup({ view: 'table' });
    const table = host.querySelector<HTMLButtonElement>('[data-testid="view-table"]')!;
    const cards = host.querySelector<HTMLButtonElement>('[data-testid="view-cards"]')!;
    expect(table.getAttribute('aria-pressed')).toBe('true');
    expect(cards.getAttribute('aria-pressed')).toBe('false');
    cards.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.view()).toBe('cards');
    expect(cards.getAttribute('aria-pressed')).toBe('true');
    expect(table.getAttribute('aria-pressed')).toBe('false');
  });

  it('пошук оновлює search і має data-testid', () => {
    const { fixture, host } = setup({ searchPlaceholder: 'Пошук', searchTestId: 'my-search' });
    const input = host.querySelector<HTMLInputElement>('[data-testid="my-search"]')!;
    input.value = 'акт';
    input.dispatchEvent(new Event('input'));
    expect(fixture.componentInstance.search()).toBe('акт');
  });

  it('іконки фільтра, вивантаження й налаштувань шлють події', () => {
    const { fixture, host } = setup({ showFilter: true, showExport: true, showSettings: true });
    const fired: string[] = [];
    fixture.componentInstance.filter.subscribe(() => fired.push('filter'));
    fixture.componentInstance.exportData.subscribe(() => fired.push('export'));
    fixture.componentInstance.settings.subscribe(() => fired.push('settings'));
    for (const id of ['toolbar-filter', 'toolbar-export', 'toolbar-settings']) {
      host.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)!.click();
    }
    expect(fired).toEqual(['filter', 'export', 'settings']);
  });
});
