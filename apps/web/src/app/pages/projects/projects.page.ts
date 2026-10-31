import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import {
  ProjectBundle,
  ProjectImportResult,
  ProjectTransferApiService,
  UnmappedReference,
  mappingFromFields,
  unmappedKey,
} from '../../projects/project-transfer-api.service';
import { WorkflowGroup, WorkflowsApiService } from '../../workflows/workflows-api.service';

/**
 * `/projects` (F8-03): картки проєктів, які бачить користувач — іконка, назва, опис, кількість сценаріїв і моя роль.
 * Картка відкриває сторінку проєкту. «Створити проєкт» і «Імпортувати проєкт» (шаблон з «Експортувати проєкт», F8-06)
 * бачить лише глобальний editor чи admin; новий проєкт одразу відкривається. Якщо конектора чи облікових даних шаблону
 * у цьому середовищі немає, імпорт показує їх списком, і користувач вказує id відповідників.
 */
@Component({
  selector: 'app-projects-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule],
  template: `
    <section class="p-6">
      <div class="mb-4 flex flex-wrap items-center gap-4">
        <h1 class="mat-font-headline-sm" i18n="@@projects.title">Проєкти</h1>
        @if (canCreate() && !creating()) {
          <button mat-flat-button type="button" data-testid="project-new" (click)="creating.set(true)" i18n="@@projects.new">Створити проєкт</button>
        }
        @if (canCreate()) {
          <button mat-stroked-button type="button" data-testid="project-import" [disabled]="importing()" (click)="fileInput.click()" i18n="@@projects.import">Імпортувати проєкт</button>
          <input #fileInput type="file" class="hidden" accept=".json,application/json" data-testid="project-import-file" (change)="pick($event)" />
        }
      </div>

      @if (creating()) {
        <form class="mb-6 flex max-w-3xl flex-wrap items-start gap-2" data-testid="project-create-form" (submit)="$event.preventDefault(); create()">
          <mat-form-field appearance="outline" class="w-64">
            <mat-label i18n="@@projects.create.name">Назва проєкту</mat-label>
            <input matInput [formControl]="name" name="name" data-testid="project-create-name" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="w-96 max-w-full">
            <mat-label i18n="@@projects.create.description">Опис</mat-label>
            <input matInput [formControl]="description" name="description" maxlength="500" data-testid="project-create-description" />
          </mat-form-field>
          <button mat-flat-button type="submit" class="mt-2" data-testid="project-create-submit" [disabled]="saving() || name.invalid" i18n="@@projects.create.submit">Створити</button>
          <button mat-button type="button" class="mt-2" (click)="creating.set(false)" i18n="@@projects.create.cancel">Скасувати</button>
        </form>
      }

      @if (importing()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (unmapped().length > 0) {
        <form class="mb-6 max-w-3xl rounded-lg border border-bp-line p-4" data-testid="import-unmapped" (submit)="$event.preventDefault(); retry()">
          <p class="mb-2 font-semibold" i18n="@@projects.import.unmapped">У цьому середовищі немає відповідників. Вкажіть id наявних об'єктів (Підключення → їхні картки) і повторіть імпорт.</p>
          @for (u of unmapped(); track u.kind + u.ref) {
            <mat-form-field appearance="outline" class="mb-1 block">
              <mat-label>{{ kindLabel(u.kind) }} «{{ u.name || u.ref }}»{{ u.type ? ' · ' + u.type : '' }}</mat-label>
              <input matInput [value]="fields()[key(u)] ?? ''" (input)="setField(u, $any($event.target).value)" data-testid="unmapped-field" [attr.data-ref]="u.ref" />
            </mat-form-field>
          }
          <button mat-flat-button type="submit" data-testid="import-retry" [disabled]="importing()" i18n="@@projects.import.retry">Повторити імпорт</button>
        </form>
      }
      @if (imported(); as r) {
        <div class="mb-6 max-w-3xl rounded-lg border border-bp-line p-4" data-testid="import-result" role="status">
          <p class="font-semibold">
            <ng-container i18n="@@projects.import.done">Імпортовано проєкт</ng-container>
            <a class="underline" [routerLink]="['/projects', r.projectId]" data-testid="import-open">«{{ r.name }}»</a>
          </p>
          <p class="text-sm opacity-80" i18n="@@projects.import.summary">
            Таблиць: {{ r.tablesCreated.length }}, сторінок: {{ r.pagesCreated.length + r.pagesUpdated.length }}, сценаріїв (неактивних): {{ r.workflowsCreated.length + r.workflowsUpdated.length }}.
          </p>
          @if (r.missingVariables.length > 0) {
            <p class="text-sm text-amber-700" data-testid="import-variables">
              <ng-container i18n="@@projects.import.variables">Додайте змінні інстансу:</ng-container> {{ r.missingVariables.join(', ') }}
            </p>
          }
        </div>
      }

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }
      @if (!loading() && projects().length === 0 && !error()) {
        <p class="opacity-70" data-testid="projects-empty" i18n="@@projects.empty">Проєктів ще немає.</p>
      }

      <div class="grid max-w-5xl grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
        @for (p of projects(); track p.id) {
          <a
            class="block rounded-lg border border-bp-line p-4 no-underline hover:shadow"
            [routerLink]="['/projects', p.id]"
            data-testid="project-card"
            [attr.data-project]="p.id"
          >
            <div class="mb-2 flex items-center gap-2">
              <span class="material-symbols-outlined" aria-hidden="true">{{ p.icon || 'folder_shared' }}</span>
              <span class="text-lg font-semibold">{{ p.name }}</span>
            </div>
            @if (p.description) {
              <p class="mb-2 opacity-80">{{ p.description }}</p>
            }
            <p class="text-sm opacity-70">
              <span i18n="@@projects.card.workflows">Сценаріїв</span>: {{ p.workflowCount }} · {{ roleLabel(p.role) }}
            </p>
          </a>
        }
      </div>
    </section>
  `,
})
export class ProjectsPage implements OnInit {
  private readonly api = inject(WorkflowsApiService);
  private readonly auth = inject(AuthService);
  private readonly context = inject(ProjectContextService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly projects = signal<WorkflowGroup[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly creating = signal(false);
  readonly error = signal<string | null>(null);
  readonly canCreate = computed(() => {
    const role = this.auth.user()?.role;
    return role === 'admin' || role === 'editor';
  });

  private readonly transfer = inject(ProjectTransferApiService);
  readonly importing = signal(false);
  readonly imported = signal<ProjectImportResult | null>(null);
  readonly unmapped = signal<UnmappedReference[]>([]);
  readonly fields = signal<Record<string, string>>({});
  private bundle: ProjectBundle | null = null;
  readonly key = unmappedKey;

  readonly name = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly description = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.loading.set(true);
    this.api
      .listGroups()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (groups) => {
          this.projects.set([...groups].sort((a, b) => a.sortOrder - b.sortOrder));
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  roleLabel(role: WorkflowGroup['role']): string {
    switch (role) {
      case 'admin':
        return $localize`:@@project.role.admin:Адміністратор`;
      case 'editor':
        return $localize`:@@project.role.editor:Editor`;
      default:
        return $localize`:@@project.role.viewer:Viewer`;
    }
  }

  kindLabel(kind: UnmappedReference['kind']): string {
    switch (kind) {
      case 'connector':
        return $localize`:@@projects.import.kind.connector:Конектор`;
      case 'credential':
        return $localize`:@@projects.import.kind.credential:Облікові дані`;
      default:
        return $localize`:@@projects.import.kind.workflow:Сценарій`;
    }
  }

  setField(u: UnmappedReference, value: string): void {
    this.fields.update((f) => ({ ...f, [unmappedKey(u)]: value }));
  }

  /** Файл шаблону: читається в браузері й одразу імпортується в новий проєкт. */
  async pick(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.error.set(null);
    this.imported.set(null);
    this.unmapped.set([]);
    this.fields.set({});
    try {
      const parsed = JSON.parse(await file.text()) as ProjectBundle;
      if (typeof parsed?.formatVersion !== 'number' || typeof parsed.project?.name !== 'string') throw new Error('bundle');
      this.bundle = parsed;
    } catch {
      this.bundle = null;
      this.error.set($localize`:@@projects.import.invalid:Це не шаблон проєкту: очікується файл, який створює «Експортувати проєкт».`);
      return;
    }
    this.retry();
  }

  retry(): void {
    if (!this.bundle) return;
    this.importing.set(true);
    this.error.set(null);
    this.transfer.import({ bundle: this.bundle, mapping: mappingFromFields(this.unmapped(), this.fields()) }).subscribe({
      next: (result) => {
        this.importing.set(false);
        this.unmapped.set([]);
        this.imported.set(result);
        this.bundle = null;
        this.context.reload();
        this.refresh();
      },
      error: (e: unknown) => {
        this.importing.set(false);
        const body = e instanceof HttpErrorResponse ? (e.error as { code?: string; unmapped?: UnmappedReference[] } | null) : null;
        if (body?.code === 'IMPORT_UNMAPPED' && body.unmapped?.length) {
          this.unmapped.set(body.unmapped);
          return;
        }
        this.error.set(apiErrorMessage(e));
      },
    });
  }

  private refresh(): void {
    this.api.listGroups().pipe(takeUntilDestroyed(this.destroyRef)).subscribe((groups) => this.projects.set([...groups].sort((a, b) => a.sortOrder - b.sortOrder)));
  }

  create(): void {
    const name = this.name.value.trim();
    if (!name) return;
    this.saving.set(true);
    this.error.set(null);
    this.api.createGroup(name, this.description.value.trim() || undefined).subscribe({
      next: (group) => {
        this.saving.set(false);
        this.context.reload();
        void this.router.navigate(['/projects', group.id]);
      },
      error: (e: unknown) => {
        this.error.set(apiErrorMessage(e));
        this.saving.set(false);
      },
    });
  }
}
