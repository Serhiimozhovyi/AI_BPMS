import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent, normalizeSearch } from './select-search.component';
import { chooseOption, matSelectOf, selectValue } from './select-testing';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatFormFieldModule, MatSelectModule, SelectSearchComponent],
  template: `
    <mat-form-field appearance="outline">
      <mat-select data-testid="long" aria-label="Таблиця" [value]="value()" (selectionChange)="value.set($event.value)">
        <bp-select-search />
        @for (o of options(); track o) {
          <mat-option [value]="o">{{ o }}</mat-option>
        }
      </mat-select>
    </mat-form-field>
    <mat-form-field appearance="outline">
      <mat-select data-testid="short" aria-label="Роль" value="editor">
        <bp-select-search />
        <mat-option value="editor">Editor</mat-option>
        <mat-option value="viewer">Viewer</mat-option>
      </mat-select>
    </mat-form-field>
  `,
})
class HostComponent {
  readonly options = signal(['Записи', 'Слоти', 'Психологи', 'Клієнти', 'Відгуки', 'Розклад', 'Оплати', 'Нотатки', 'Зміни']);
  readonly value = signal('Слоти');
}

describe('SelectSearchComponent (F9-14)', () => {
  async function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host };
  }

  async function open(fixture: { detectChanges(): void }, host: HTMLElement, testid: string) {
    host.querySelector<HTMLElement>(`[data-testid="${testid}"] .mat-mdc-select-trigger`)!.click();
    fixture.detectChanges();
    await Promise.resolve(); // `openedChange` у Material — мікрозадачею після відкриття.
    await new Promise((resolve) => setTimeout(resolve)); // фокус у пошук — після показу панелі.
    fixture.detectChanges();
  }

  function searchIn(testid: string): HTMLElement {
    const panelId = document.querySelector(`[data-testid="${testid}"]`)!.getAttribute('aria-controls')!;
    return document.getElementById(panelId)!.querySelector<HTMLElement>('.bp-select-search')!;
  }

  function visibleOptions(): string[] {
    return [...document.querySelectorAll<HTMLElement>('mat-option')].filter((o) => !o.hidden).map((o) => o.textContent!.trim());
  }

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = '')));

  it('довгий список: пошук видно, фільтрує без регістру, «Нічого не знайдено», скидається при закритті', async () => {
    const { fixture, host } = await setup();
    await open(fixture, host, 'long');
    const search = searchIn('long');
    expect(search.classList).not.toContain('bp-select-search--off');
    const input = search.querySelector<HTMLInputElement>('[data-testid="select-search"]')!;
    expect(document.activeElement).toBe(input);

    input.value = 'КЛІ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(visibleOptions()).toEqual(['Клієнти']);

    input.value = 'щось інше';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(visibleOptions()).toEqual([]);
    expect(search.querySelector('[data-testid="select-search-empty"]')?.textContent).toContain('Нічого не знайдено');

    matSelectOf(host.querySelector('[data-testid="long"]')!).close();
    await Promise.resolve();
    fixture.detectChanges();
    expect(visibleOptions()).toHaveLength(9);
    expect(input.value).toBe('');
  });

  it('пробіл і літери в полі не вибирають пункт; Enter вибирає перший знайдений', async () => {
    const { fixture, host } = await setup();
    await open(fixture, host, 'long');
    const input = searchIn('long').querySelector<HTMLInputElement>('input')!;
    const space = new KeyboardEvent('keydown', { key: ' ', keyCode: 32, bubbles: true });
    input.dispatchEvent(space);
    expect(selectValue(host.querySelector('[data-testid="long"]')!)).toBe('Слоти');

    input.value = 'оплат';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13, bubbles: true }));
    fixture.detectChanges();
    expect(fixture.componentInstance.value()).toBe('Оплати');
  });

  it('короткий список (до 7 пунктів) — без поля пошуку; вибір працює як завжди', async () => {
    const { fixture, host } = await setup();
    await open(fixture, host, 'short');
    expect(searchIn('short').classList).toContain('bp-select-search--off');
    chooseOption(host.querySelector('[data-testid="short"]')!, 'viewer');
    expect(selectValue(host.querySelector('[data-testid="short"]')!)).toBe('viewer');
  });

  it('normalizeSearch ігнорує регістр і зайві пробіли', () => {
    expect(normalizeSearch('  Нова   Заявка ')).toBe('нова заявка');
    expect(normalizeSearch(undefined)).toBe('');
  });
});
