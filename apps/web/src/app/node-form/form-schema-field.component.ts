import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { FieldType, FieldTypeConfig } from '@ngx-formly/core';
import type { JsonSchema } from '../forms/form-fields';
import { isDesignableSchema, summarizeSchema } from '../forms/designer/form-summary';
import { FormDesignerDialogComponent, FormDesignerDialogData } from './form-designer-dialog.component';
import { NodeFieldProps } from './schema-fields';

/**
 * Поля форми вузла (F11-04, `x-openbp-widget: form-schema`) у панелі: кнопка «Налаштувати поля (N)» з коротким переліком
 * підписів; конструктор відкривається повноекранним діалогом (панель вузла завузька). Значення — та сама JSON Schema
 * форми, що й раніше, тож формат вузла, `/mcp` і імпорт n8n не змінюються. Вираз `{{ }}` чи не-об'єкт у параметрі
 * конструктор не відкриває — значення показано як є й редагується в «Код»/JSON-режимі панелі.
 */
@Component({
  selector: 'app-node-form-schema-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <div class="fs" [attr.data-field]="key">
      <span class="fs__label">{{ label }}</span>
      @if (designable()) {
        @if (summary().count) {
          <ul class="fs__list" data-testid="fs-list">
            @for (item of summary().labels; track $index) {
              <li>{{ item }}</li>
            }
            @if (summary().more) {
              <li class="fs__more" data-testid="fs-more">{{ moreText() }}</li>
            }
          </ul>
        } @else {
          <p class="fs__empty" data-testid="fs-empty" i18n="@@fs.empty">Полів ще немає</p>
        }
        <button mat-stroked-button type="button" data-testid="fs-open" (click)="open()">
          <ng-container i18n="@@fs.open">Налаштувати поля</ng-container> ({{ summary().count }})
        </button>
      } @else {
        <p class="fs__empty" data-testid="fs-raw" i18n="@@fs.raw">Значення не є об'єктом схеми (наприклад, вираз), тому конструктор недоступний.</p>
      }
      @if (description) {
        <p class="fs__hint">{{ description }}</p>
      }
    </div>
  `,
  styles: `
    .fs {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 6px;
      margin: 0 0 12px;
      font: var(--bp-font-p3);
    }
    .fs__label {
      font-weight: 500;
      color: var(--bp-text);
    }
    .fs__list {
      margin: 0;
      padding-left: 18px;
      color: var(--bp-text);
    }
    .fs__more,
    .fs__empty,
    .fs__hint {
      margin: 0;
      color: var(--bp-text-muted);
      font-size: 12px;
    }
  `,
})
export class NodeFormSchemaFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);

  private readonly value = signal<unknown>(undefined);
  protected readonly designable = computed(() => this.value() === undefined || this.value() === null || isDesignableSchema(this.value()));
  protected readonly summary = computed(() => summarizeSchema(this.value()));
  protected readonly moreText = computed(() => $localize`:@@fs.more:ще ${this.summary().more}:count:`);

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  protected get description(): string | undefined {
    return (this.props as NodeFieldProps).description;
  }

  ngOnInit(): void {
    this.value.set(this.formControl.value);
    this.formControl.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((v) => this.value.set(v));
  }

  private designableValue(): JsonSchema | null {
    const value = this.value();
    return isDesignableSchema(value) ? value : null;
  }

  protected open(): void {
    const data: FormDesignerDialogData = {
      schema: this.designableValue(),
      label: this.label,
      ...this.previewMeta(),
    };
    this.dialog
      .open<FormDesignerDialogComponent, FormDesignerDialogData, Record<string, unknown>>(FormDesignerDialogComponent, {
        data,
        width: '100vw',
        height: '100vh',
        maxWidth: '100vw',
        maxHeight: '100vh',
        panelClass: 'bp-dialog-fullscreen',
        autoFocus: false,
      })
      .afterClosed()
      .subscribe((schema) => {
        if (!schema) return;
        this.formControl.setValue(schema);
        this.formControl.markAsDirty();
      });
  }

  /** Заголовок, опис і кнопка вузла для перегляду: `form`-вузол `wait` має їх у `form.*`, `formTrigger` — на верхньому рівні. */
  private previewMeta(): Pick<FormDesignerDialogData, 'title' | 'description' | 'submitLabel'> {
    const parent = this.field.parent?.model as Record<string, unknown> | undefined;
    const text = (name: string) => (typeof parent?.[name] === 'string' ? (parent[name] as string) : undefined);
    return { title: text('title'), description: text('description'), submitLabel: text('submitLabel') };
  }
}
