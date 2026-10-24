import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggle, MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { WorkflowDetail, WorkflowSummary, WorkflowsApiService, toSummary } from '../../workflows/workflows-api.service';

/** `/workflows`: список сценаріїв, створення, перейменування, активація, видалення, перехід у канвас (F2-15). */
@Component({
  selector: 'app-workflows-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSlideToggleModule,
  ],
  template: `
    <section class="p-6">
      <div class="mb-4 flex items-center gap-4">
        <h1 class="mat-font-headline-sm" i18n="@@workflows.title">Сценарії</h1>
        @if (!creating()) {
          <button mat-flat-button type="button" data-testid="create-button" (click)="startCreate()" i18n="@@workflows.create">
            Створити сценарій
          </button>
        }
      </div>

      @if (creating()) {
        <form class="mb-6 flex max-w-2xl items-start gap-2" data-testid="create-form" [formGroup]="createForm" (ngSubmit)="create()">
          <mat-form-field appearance="outline" class="flex-1">
            <mat-label i18n="@@workflows.form.name">Назва сценарію</mat-label>
            <input matInput [formControl]="newName" name="name" data-testid="create-name" />
            @if (newName.hasError('required')) {
              <mat-error i18n="@@workflows.form.name.required">Вкажіть назву</mat-error>
            }
          </mat-form-field>
          <button mat-flat-button type="submit" class="mt-2" [disabled]="busy()" i18n="@@workflows.form.submit">Створити</button>
          <button mat-button type="button" class="mt-2" (click)="cancelCreate()" i18n="@@workflows.form.cancel">Скасувати</button>
        </form>
      }

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (!loading() && workflows().length === 0 && !error()) {
        <p class="opacity-70" data-testid="empty" i18n="@@workflows.empty">
          Ще немає жодного сценарію. Створіть перший і зберіть його на канвасі.
        </p>
      }

      @if (workflows().length > 0) {
        <table class="w-full text-left" data-testid="workflows-table">
          <thead>
            <tr>
              <th class="p-2" i18n="@@workflows.col.name">Назва</th>
              <th class="p-2" i18n="@@workflows.col.active">Активний</th>
              <th class="p-2" i18n="@@workflows.col.version">Версія</th>
              <th class="p-2" i18n="@@workflows.col.updatedAt">Змінено</th>
              <th class="p-2"><span class="sr-only" i18n="@@workflows.col.actions">Дії</span></th>
            </tr>
          </thead>
          <tbody>
            @for (w of workflows(); track w.id) {
              <tr [attr.data-workflow]="w.id">
                <td class="p-2">
                  @if (renamingId() === w.id) {
                    <form class="flex items-center gap-2" data-testid="rename-form" [formGroup]="renameForm" (ngSubmit)="saveRename(w.id)">
                      <input
                        class="rounded border px-2 py-1"
                        [formControl]="renameName"
                        name="rename"
                        data-testid="rename-input"
                        aria-label="Нова назва"
                        i18n-aria-label="@@workflows.rename.label"
                        (keydown.escape)="cancelRename()"
                      />
                      <button mat-button type="submit" [disabled]="busy()" i18n="@@workflows.rename.save">Зберегти</button>
                      <button mat-button type="button" (click)="cancelRename()" i18n="@@workflows.rename.cancel">Скасувати</button>
                    </form>
                  } @else {
                    <a class="font-medium hover:underline" [routerLink]="['/workflows', w.id]" data-testid="open-link">{{ w.name }}</a>
                  }
                </td>
                <td class="p-2">
                  <mat-slide-toggle
                    data-testid="active-toggle"
                    [checked]="w.active"
                    [disabled]="busy()"
                    (change)="toggleActive(w, $event.checked, $event.source)"
                    [attr.aria-label]="w.active ? deactivateLabel : activateLabel"
                  />
                </td>
                <td class="p-2">{{ w.version }}</td>
                <td class="p-2">{{ w.updatedAt | date: 'short' }}</td>
                <td class="p-2 whitespace-nowrap text-right">
                  @if (deletingId() === w.id) {
                    <span class="mr-2" i18n="@@workflows.delete.confirm">Видалити сценарій разом з історією виконань?</span>
                    <button mat-button type="button" color="warn" data-testid="delete-confirm" [disabled]="busy()" (click)="confirmDelete(w.id)" i18n="@@workflows.delete.yes">
                      Так, видалити
                    </button>
                    <button mat-button type="button" (click)="deletingId.set(null)" i18n="@@workflows.delete.no">Ні</button>
                  } @else {
                    <a mat-button [routerLink]="['/workflows', w.id]" i18n="@@workflows.open">Відкрити</a>
                    <button mat-button type="button" data-testid="rename-button" (click)="startRename(w)" i18n="@@workflows.rename">
                      Перейменувати
                    </button>
                    <button
                      mat-button
                      type="button"
                      data-testid="delete-button"
                      [disabled]="w.active"
                      [attr.title]="w.active ? deleteActiveHint : null"
                      (click)="startDelete(w.id)"
                      i18n="@@workflows.delete"
                    >
                      Видалити
                    </button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>
  `,
})
export class WorkflowsPage implements OnInit {
  private readonly api = inject(WorkflowsApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly creating = signal(false);
  readonly renamingId = signal<string | null>(null);
  readonly deletingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly newName = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly renameName = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly createForm = new FormGroup({ name: this.newName });
  readonly renameForm = new FormGroup({ name: this.renameName });

  readonly activateLabel = $localize`:@@workflows.activate:Активувати`;
  readonly deactivateLabel = $localize`:@@workflows.deactivate:Деактивувати`;
  readonly deleteActiveHint = $localize`:@@workflows.delete.activeHint:Спершу деактивуйте сценарій`;

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => {
          this.workflows.set(page.items);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  startCreate(): void {
    this.newName.reset('');
    this.creating.set(true);
  }

  cancelCreate(): void {
    this.creating.set(false);
  }

  /** `POST /workflows` з порожнім визначенням → перехід у канвас нового сценарію. */
  create(): void {
    const name = this.newName.value.trim();
    if (!name) {
      this.newName.setValue('');
      this.newName.markAsTouched();
      return;
    }
    this.run(this.api.create(name), (detail) => {
      this.creating.set(false);
      this.upsert(detail);
      void this.router.navigate(['/workflows', detail.id]);
    });
  }

  startRename(w: WorkflowSummary): void {
    this.deletingId.set(null);
    this.renameName.reset(w.name);
    this.renamingId.set(w.id);
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  saveRename(id: string): void {
    const name = this.renameName.value.trim();
    if (!name) return;
    const current = this.workflows().find((w) => w.id === id);
    if (current?.name === name) {
      this.renamingId.set(null);
      return;
    }
    this.run(this.api.rename(id, name), (detail) => {
      this.renamingId.set(null);
      this.upsert(detail);
    });
  }

  /** Активація валідує сценарій на сервері; при відмові перемикач повертається у попередній стан. */
  toggleActive(w: WorkflowSummary, active: boolean, toggle?: MatSlideToggle): void {
    const request = active ? this.api.activate(w.id) : this.api.deactivate(w.id);
    this.run(
      request,
      (detail) => this.upsert(detail),
      () => {
        if (toggle) toggle.checked = w.active;
      },
    );
  }

  startDelete(id: string): void {
    this.renamingId.set(null);
    this.deletingId.set(id);
  }

  confirmDelete(id: string): void {
    this.run(this.api.delete(id), () => {
      this.deletingId.set(null);
      this.workflows.update((list) => list.filter((w) => w.id !== id));
    });
  }

  private run<T>(
    request: Observable<T>,
    onSuccess: (value: T) => void,
    onError?: () => void,
  ): void {
    this.busy.set(true);
    this.error.set(null);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (value) => {
        this.busy.set(false);
        onSuccess(value);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(apiErrorMessage(e));
        onError?.();
      },
    });
  }

  private upsert(detail: WorkflowDetail): void {
    const summary = toSummary(detail);
    this.workflows.update((list) =>
      list.some((w) => w.id === summary.id)
        ? list.map((w) => (w.id === summary.id ? summary : w))
        : [summary, ...list],
    );
  }
}
