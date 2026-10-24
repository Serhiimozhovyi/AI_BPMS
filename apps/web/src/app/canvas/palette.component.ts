import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { PaletteItem, PaletteSection, filterPalette } from './palette';

/** Палітра канвасу: пошук, секції `core.*` за групами і tools за серверами; клік додає вузол. */
@Component({
  selector: 'app-palette',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatFormFieldModule, MatInputModule],
  template: `
    <mat-form-field appearance="outline" class="w-full" subscriptSizing="dynamic">
      <mat-label i18n="@@canvas.palette.search">Пошук вузла</mat-label>
      <input matInput type="search" data-testid="palette-search" [value]="query()" (input)="search($event)" />
    </mat-form-field>
    @for (section of visible(); track section.id) {
      <section class="palette__section" [attr.data-section]="section.id">
        <h2 class="palette__title">{{ section.label }}</h2>
        @for (item of section.items; track item.key) {
          <button
            type="button"
            class="palette__item"
            [attr.data-item]="item.key"
            [title]="item.description ?? item.type"
            (click)="pick.emit(item)"
          >
            <span class="palette__label">{{ item.label }}</span>
            <span class="palette__type">{{ item.toolName ?? item.type }}</span>
          </button>
        }
      </section>
    } @empty {
      <p class="palette__empty" data-testid="palette-empty" i18n="@@canvas.palette.empty">Нічого не знайдено</p>
    }
  `,
  styles: `
    :host {
      display: block;
      padding: 12px;
      overflow-y: auto;
    }

    .palette__section {
      margin-top: 12px;
    }

    .palette__title {
      margin: 0 0 4px;
      font-size: 11px;
      font-weight: 600;
      text-transform: uppercase;
      opacity: 0.7;
    }

    .palette__item {
      display: flex;
      flex-direction: column;
      width: 100%;
      padding: 6px 8px;
      border: 0;
      border-radius: 6px;
      background: none;
      text-align: left;
      cursor: pointer;
      font: inherit;
    }

    .palette__item:hover,
    .palette__item:focus-visible {
      background: var(--mat-sys-surface-container-high);
    }

    .palette__label {
      font-size: 13px;
    }

    .palette__type {
      font-size: 11px;
      opacity: 0.6;
    }

    .palette__empty {
      margin-top: 12px;
      opacity: 0.7;
    }
  `,
})
export class PaletteComponent {
  /** Секції палітри (`buildPalette`). */
  readonly sections = input.required<readonly PaletteSection[]>();

  /** Обраний елемент — редактор додає вузол. */
  readonly pick = output<PaletteItem>();

  protected readonly query = signal('');

  protected readonly visible = computed(() => filterPalette(this.sections(), this.query()));

  protected search(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }
}
