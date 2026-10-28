import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { FormlyFieldConfig, FormlyForm } from '@ngx-formly/core';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { providePublicForm } from '../../forms/form-field-types';
import { formFields, toPayload } from '../../forms/form-fields';
import {
  PageAction,
  PageRecord,
  PageRow,
  PageRows,
  PageViewColumn,
  ProjectPageView,
  ProjectPagesApiService,
  cellText,
} from '../../projects/project-pages-api.service';

interface FieldError {
  field: string;
  message: string;
}

/**
 * `/projects/:id/app/<key>[/<rowKey>]` — сторінка застосунку (F8-04): список (пошук, сортування, сторінки, дії), картка
 * рядка з пов'язаними списками або форма (поля з типів колонок тим самим рушієм форм, що й публічні форми F6-03).
 * Фільтри сторінки й права застосовує Api; тут — лише показ і переходи.
 */
@Component({
  selector: 'app-app-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, FormlyForm],
  providers: [providePublicForm()],
  template: `
    <section class="px-6 pb-6">
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-2 text-red-700" data-testid="page-error">{{ error() }}</p>
      }
      @if (view(); as v) {
        <div class="mb-3 flex flex-wrap items-center gap-3">
          <h2 class="text-lg font-semibold" data-testid="app-page-title">{{ v.page.name }}</h2>
          @if (v.page.kind === 'list' && v.page.definition.create; as create) {
            <button mat-flat-button type="button" data-testid="app-create" (click)="open(create.page)" i18n="@@app.page.create">Створити</button>
          }
        </div>

        @switch (v.page.kind) {
          @case ('list') {
            @if (v.page.definition.search?.length) {
              <mat-form-field appearance="outline" class="w-72">
                <mat-label i18n="@@app.page.search">Пошук</mat-label>
                <input matInput data-testid="app-search" [value]="search()" (keydown.enter)="find($any($event.target).value)" (blur)="find($any($event.target).value)" />
              </mat-form-field>
            }
            @if (rows(); as r) {
              @if (r.items.length === 0) {
                <p class="opacity-70" data-testid="app-rows-empty" i18n="@@app.page.empty">Записів немає.</p>
              } @else {
                <div class="overflow-x-auto">
                  <table class="w-full max-w-5xl text-left" data-testid="app-rows">
                    <thead>
                      <tr>
                        @for (c of v.columns; track c.column) {
                          <th class="p-2">
                            <button type="button" class="font-semibold" [attr.data-sort]="c.column" (click)="sortBy(c.column)">
                              {{ c.label }}
                              @if (sort() === c.column) {
                                <span aria-hidden="true">{{ descending() ? '↓' : '↑' }}</span>
                              }
                            </button>
                          </th>
                        }
                        <th class="p-2"></th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of r.items; track row.key) {
                        <tr [attr.data-row]="row.key">
                          @for (c of v.columns; track c.column) {
                            <td class="p-2">{{ cell(row, c) }}</td>
                          }
                          <td class="whitespace-nowrap p-2 text-right">
                            @for (i of row.actions; track i) {
                              <button mat-button type="button" [attr.data-action]="i" (click)="act(v.page.definition.actions![i], i, row)">
                                {{ v.page.definition.actions![i].label }}
                              </button>
                            }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                @if (r.total > r.pageSize) {
                  <div class="mt-2 flex items-center gap-2" data-testid="app-pager">
                    <button mat-button type="button" [disabled]="r.page <= 1" (click)="go(r.page - 1)" i18n="@@app.page.prev">Назад</button>
                    <span>{{ r.page }} / {{ pages(r) }}</span>
                    <button mat-button type="button" [disabled]="r.page >= pages(r)" (click)="go(r.page + 1)" i18n="@@app.page.next">Далі</button>
                  </div>
                }
              }
            }
          }
          @case ('detail') {
            @if (record(); as rec) {
              <dl class="mb-4 grid max-w-3xl grid-cols-[minmax(8rem,auto)_1fr] gap-x-4 gap-y-2" data-testid="app-detail">
                @for (c of v.columns; track c.column) {
                  <dt class="opacity-70">{{ c.label }}</dt>
                  <dd [attr.data-field]="c.column">{{ cell(rec.row, c) }}</dd>
                }
              </dl>
              @if (rec.row.actions.length) {
                <div class="mb-4 flex gap-2">
                  @for (i of rec.row.actions; track i) {
                    <button mat-stroked-button type="button" [attr.data-action]="i" (click)="act(v.page.definition.actions![i], i, rec.row)">
                      {{ v.page.definition.actions![i].label }}
                    </button>
                  }
                </div>
              }
              @for (rel of rec.related; track $index; let ri = $index) {
                <h3 class="mb-2 mt-4 font-semibold" [attr.data-related]="ri">{{ rel.label }}</h3>
                @if (rel.rows.length === 0) {
                  <p class="opacity-70" i18n="@@app.page.empty">Записів немає.</p>
                } @else {
                  <table class="w-full max-w-5xl text-left">
                    <thead>
                      <tr>
                        @for (c of rel.columns; track c.column) {
                          <th class="p-2">{{ c.label }}</th>
                        }
                        <th class="p-2"></th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of rel.rows; track row.key) {
                        <tr [attr.data-row]="row.key">
                          @for (c of rel.columns; track c.column) {
                            <td class="p-2">{{ cell(row, c) }}</td>
                          }
                          <td class="whitespace-nowrap p-2 text-right">
                            @for (i of row.actions; track i) {
                              <button mat-button type="button" (click)="open(v.page.definition.related![ri].actions![i].page!, row.key)">
                                {{ v.page.definition.related![ri].actions![i].label }}
                              </button>
                            }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                }
              }
            }
          }
          @case ('form') {
            <form class="max-w-xl" [formGroup]="form" (submit)="$event.preventDefault(); submit()" novalidate data-testid="app-form">
              <formly-form [form]="form" [fields]="fields()" [model]="model" />
              @if (submitError()) {
                <p role="alert" class="mb-2 text-red-700" data-testid="app-form-error">{{ submitError() }}</p>
              }
              @if (saved()) {
                <p role="status" class="mb-2" data-testid="app-form-saved" i18n="@@app.page.saved">Збережено.</p>
              }
              <button mat-flat-button type="submit" data-testid="app-submit" [disabled]="submitting()" i18n="@@app.page.submit">Зберегти</button>
            </form>
          }
        }
      }
    </section>
  `,
})
export class AppPagePage implements OnInit {
  private readonly api = inject(ProjectPagesApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** `projects/:id` → `app` → `:key`: проєкт — у діда маршруту. */
  readonly projectId = this.route.parent?.parent?.snapshot.paramMap.get('id') ?? '';
  readonly view = signal<ProjectPageView | null>(null);
  readonly rows = signal<PageRows | null>(null);
  readonly record = signal<PageRecord | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly search = signal('');
  readonly sort = signal<string | null>(null);
  readonly descending = signal(false);
  readonly page = signal(1);

  readonly form = new FormGroup({});
  readonly fields = signal<FormlyFieldConfig[]>([]);
  model: Record<string, unknown> = {};
  readonly submitting = signal(false);
  readonly submitError = signal('');
  readonly saved = signal(false);

  private key = '';
  private rowKey: string | null = null;

  ngOnInit(): void {
    // Перехід між сторінками застосунку перевикористовує компонент — слухаємо параметри.
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      this.key = params.get('key') ?? '';
      this.rowKey = params.get('rowKey');
      this.load();
    });
  }

  cell(row: PageRow, column: PageViewColumn): string {
    return cellText(row, column.column, column.type, column.format);
  }

  pages(rows: PageRows): number {
    return Math.max(1, Math.ceil(rows.total / rows.pageSize));
  }

  find(text: string): void {
    if (text === this.search()) return;
    this.search.set(text);
    this.page.set(1);
    this.loadRows();
  }

  sortBy(column: string): void {
    if (this.sort() === column) this.descending.update((d) => !d);
    else {
      this.sort.set(column);
      this.descending.set(false);
    }
    this.loadRows();
  }

  go(page: number): void {
    this.page.set(page);
    this.loadRows();
  }

  /** Сторінка застосунку з ключем рядка (картка, форма зміни). */
  open(page: string, rowKey?: string): void {
    void this.router.navigate(['/projects', this.projectId, 'app', page, ...(rowKey ? [rowKey] : [])]);
  }

  act(action: PageAction, index: number, row: PageRow): void {
    if (action.page) {
      this.open(action.page, row.key);
      return;
    }
    if (action.confirm && !window.confirm(action.confirm)) return;
    this.error.set(null);
    this.api.runAction(this.projectId, this.key, row.key, index).subscribe({
      next: () => (this.view()?.page.kind === 'detail' ? this.open(this.view()!.page.definition.create?.page ?? this.key) : this.loadRows()),
      error: (e: unknown) => this.error.set(apiErrorMessage(e)),
    });
  }

  submit(): void {
    const view = this.view();
    if (!view || this.submitting()) return;
    this.submitError.set('');
    this.saved.set(false);
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.submitError.set($localize`:@@form.invalid:Перевірте позначені поля.`);
      return;
    }
    this.submitting.set(true);
    this.api.submit(this.projectId, this.key, toPayload(view.schema, this.model).fields, this.rowKey).subscribe({
      next: (result) => {
        this.submitting.set(false);
        if (result.then) this.open(result.then);
        else this.saved.set(true);
      },
      error: (e: unknown) => {
        this.submitting.set(false);
        const problem = e instanceof HttpErrorResponse && typeof e.error === 'object' && e.error ? (e.error as { code?: string; errors?: FieldError[] }) : null;
        if (problem?.code === 'PAGE_FORM_INVALID' && problem.errors?.length) {
          this.showFieldErrors(problem.errors);
          return;
        }
        this.submitError.set(apiErrorMessage(e));
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.rows.set(null);
    this.record.set(null);
    this.saved.set(false);
    this.submitError.set('');
    this.api.get(this.projectId, this.key).subscribe({
      next: (view) => {
        this.view.set(view);
        this.loading.set(false);
        this.sort.set(view.page.definition.sort?.column ?? null);
        this.descending.set(view.page.definition.sort?.direction === 'desc');
        this.search.set('');
        this.page.set(1);
        if (view.page.kind === 'list') this.loadRows();
        else if (view.page.kind === 'detail') this.loadRecord();
        else this.prepareForm(view);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private loadRows(): void {
    this.loading.set(true);
    this.api
      .rows(this.projectId, this.key, {
        search: this.search() || undefined,
        sort: this.sort() ?? undefined,
        direction: this.sort() ? (this.descending() ? 'desc' : 'asc') : undefined,
        page: this.page(),
      })
      .subscribe({
        next: (rows) => {
          this.rows.set(rows);
          this.loading.set(false);
        },
        error: (e: unknown) => this.fail(e),
      });
  }

  private loadRecord(): void {
    if (!this.rowKey) {
      this.loading.set(false);
      this.error.set($localize`:@@app.page.noRow:Картку відкривають зі списку: не вказано запис.`);
      return;
    }
    this.api.row(this.projectId, this.key, this.rowKey).subscribe({
      next: (record) => {
        this.record.set(record);
        this.loading.set(false);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private prepareForm(view: ProjectPageView): void {
    this.form.reset();
    this.model = {};
    this.fields.set(formFields(view.schema));
    if (!this.rowKey) return;
    this.loading.set(true);
    this.api.row(this.projectId, this.key, this.rowKey).subscribe({
      next: (record) => {
        this.model = { ...record.row.data };
        this.fields.set(formFields(view.schema));
        this.loading.set(false);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  /** Помилки полів з Api — під полями; без поля — над кнопкою. */
  private showFieldErrors(errors: FieldError[]): void {
    const general: string[] = [];
    for (const { field, message } of errors) {
      const control = field ? this.form.get(field) : null;
      if (control) {
        control.setErrors({ ...(control.errors ?? {}), server: message });
        control.markAsTouched();
      } else {
        general.push(message);
      }
    }
    this.submitError.set(general.length ? general.join(' ') : $localize`:@@form.invalid:Перевірте позначені поля.`);
  }

  private fail(e: unknown): void {
    this.loading.set(false);
    this.error.set(
      e instanceof HttpErrorResponse && e.status === 404
        ? $localize`:@@app.page.notFound:Сторінку чи запис не знайдено або вони не для вашої ролі.`
        : apiErrorMessage(e),
    );
  }
}
