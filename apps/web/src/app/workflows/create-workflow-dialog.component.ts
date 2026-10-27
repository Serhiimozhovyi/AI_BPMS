import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { WorkflowGroup } from './workflows-api.service';

/** Дані діалогу створення сценарію (F3-80). */
export interface CreateWorkflowDialogData {
  groups: WorkflowGroup[];
  /** Група, вибрана за замовчуванням; `null` — без групи. */
  groupId: string | null;
}

/** Результат діалогу: назва і група нового сценарію. */
export interface CreateWorkflowResult {
  name: string;
  groupId: string | null;
}

/** Діалог «Створити сценарій»: назва і група; після підтвердження сторінка створює сценарій і відкриває редактор. */
@Component({
  selector: 'app-create-workflow-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title i18n="@@workflows.create">Створити сценарій</h2>
    <form [formGroup]="form" (ngSubmit)="submit()" data-testid="create-form">
      <mat-dialog-content class="flex flex-col gap-2">
        <mat-form-field appearance="outline">
          <mat-label i18n="@@workflows.form.name">Назва сценарію</mat-label>
          <input matInput formControlName="name" name="name" data-testid="create-name" cdkFocusInitial />
          @if (form.controls.name.hasError('required')) {
            <mat-error i18n="@@workflows.form.name.required">Вкажіть назву</mat-error>
          }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label i18n="@@workflows.form.group">Група</mat-label>
          <mat-select formControlName="groupId" data-testid="create-group">
            <mat-option [value]="null" i18n="@@workflows.groups.none">Без групи</mat-option>
            @for (g of data.groups; track g.id) {
              <mat-option [value]="g.id">{{ g.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close data-testid="create-cancel" i18n="@@workflows.form.cancel">Скасувати</button>
        <button mat-flat-button type="submit" data-testid="create-submit" i18n="@@workflows.form.submit">Створити</button>
      </mat-dialog-actions>
    </form>
  `,
})
export class CreateWorkflowDialog {
  readonly data = inject<CreateWorkflowDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<CreateWorkflowDialog, CreateWorkflowResult>>(MatDialogRef);

  readonly form = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    groupId: new FormControl<string | null>(this.data.groupId),
  });

  submit(): void {
    const name = this.form.controls.name.value.trim();
    if (!name) {
      this.form.controls.name.setValue('');
      this.form.controls.name.markAsTouched();
      return;
    }
    this.ref.close({ name, groupId: this.form.controls.groupId.value });
  }
}
