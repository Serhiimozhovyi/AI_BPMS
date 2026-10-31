import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { FormDesignerComponent } from '../forms/designer/form-designer.component';
import type { JsonSchema } from '../forms/form-fields';

/** Що відкривається в діалозі конструктора. */
export interface FormDesignerDialogData {
  /** Поточна схема параметра; `null` — полів ще немає. */
  schema: JsonSchema | null;
  /** Назва поля — у заголовку. */
  label: string;
  /** Заголовок, опис і кнопка форми для вкладки «Перегляд» (значення з параметрів вузла, вирази — як текст). */
  title?: string;
  description?: string;
  submitLabel?: string;
}

/**
 * Повноекранний діалог конструктора форми (F11-04): «Зберегти» віддає нову схему, «Скасувати» — без змін; закриття
 * зі зміненою схемою — підтвердження, щоб не втратити правки.
 */
@Component({
  selector: 'app-form-designer-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatDialogModule, FormDesignerComponent],
  template: `
    <div class="fdd__head">
      <h2 mat-dialog-title class="fdd__title" i18n="@@fdd.title">Поля форми</h2>
      <button mat-icon-button type="button" data-testid="fdd-close" i18n-aria-label="@@fdd.close" aria-label="Закрити" (click)="cancel()">
        <span class="material-symbols-outlined" aria-hidden="true">close</span>
      </button>
    </div>
    <mat-dialog-content class="fdd" data-testid="form-designer-dialog">
      <bp-form-designer [schema]="data.schema" [preview]="meta" (schemaChange)="draft.set($event)" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" data-testid="fdd-cancel" (click)="cancel()" i18n="@@fdd.cancel">Скасувати</button>
      <button mat-flat-button type="button" data-testid="fdd-save" [disabled]="!dirty()" (click)="save()" i18n="@@fdd.save">Зберегти</button>
    </mat-dialog-actions>
  `,
  styles: `
    .fdd__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding-right: 12px;
    }
    .fdd__title {
      flex: 1;
    }
    .fdd {
      max-height: none;
    }
  `,
})
export class FormDesignerDialogComponent {
  protected readonly data = inject<FormDesignerDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<FormDesignerDialogComponent, JsonSchema>>(MatDialogRef);

  protected readonly meta = { title: this.data.title, description: this.data.description, submitLabel: this.data.submitLabel };
  protected readonly draft = signal<JsonSchema | null>(null);
  protected readonly dirty = computed(() => this.draft() !== null);

  protected save(): void {
    const schema = this.draft();
    if (schema) this.ref.close(schema);
  }

  protected cancel(): void {
    if (this.dirty() && !window.confirm($localize`:@@fdd.discard:Закрити без збереження? Зміни полів буде втрачено.`)) return;
    this.ref.close();
  }
}
