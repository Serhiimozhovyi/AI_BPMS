import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { WorkflowGroup, WorkflowsApiService } from '../../workflows/workflows-api.service';

/** Вкладка сторінки проєкту: сегмент URL `/projects/:id/<path>`. */
interface ProjectTab {
  path: string;
  testId: string;
  label: string;
}

/**
 * `/projects/:id` (F8-03): сторінка проєкту як робочого простору — заголовок (іконка, назва, опис, моя роль) і вкладки
 * «Застосунок» (перша й типова, коли в проєкті є сторінки застосунку), «Огляд», «Сценарії», «Таблиці», «Підключення»,
 * «Учасники». Вкладки — дочірні маршрути, тому кожна має
 * власну адресу. Сценарії, таблиці й підключення — ті самі сторінки, що й у головному меню: при вході сюди проєкт стає
 * вибраним у шапці ({@link ProjectContextService}), і вони фільтруються за ним. Назву й опис змінює editor проєкту.
 */
@Component({
  selector: 'app-project-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, RouterLinkActive, RouterOutlet, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    @if (!shellOnly()) {
    <section class="px-6 pt-6">
      <a class="mb-2 block text-sm" routerLink="/projects" i18n="@@project.backToList">← Усі проєкти</a>
      @if (editing()) {
        <form class="mb-2 flex max-w-3xl flex-wrap items-start gap-2" data-testid="project-form" (submit)="$event.preventDefault(); save()">
          <mat-form-field appearance="outline" class="w-64">
            <mat-label i18n="@@project.edit.name">Назва</mat-label>
            <input matInput [formControl]="name" name="name" data-testid="project-name-input" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="w-96 max-w-full">
            <mat-label i18n="@@project.edit.description">Опис</mat-label>
            <input matInput [formControl]="description" name="description" maxlength="500" data-testid="project-description-input" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="w-48">
            <mat-label i18n="@@project.edit.icon">Іконка (Material Symbols)</mat-label>
            <input matInput [formControl]="icon" name="icon" maxlength="40" data-testid="project-icon-input" />
          </mat-form-field>
          <button mat-flat-button type="submit" class="mt-2" data-testid="project-save" [disabled]="saving() || name.invalid" i18n="@@project.edit.save">Зберегти</button>
          <button mat-button type="button" class="mt-2" (click)="editing.set(false)" i18n="@@project.edit.cancel">Скасувати</button>
        </form>
      } @else {
        <div class="mb-1 flex items-center gap-3">
          <span class="material-symbols-outlined text-3xl" aria-hidden="true" data-testid="project-icon">{{ project()?.icon || 'folder_shared' }}</span>
          <h1 class="mat-font-headline-sm" data-testid="project-name">{{ project()?.name ?? '…' }}</h1>
          @if (canManage()) {
            <button mat-button type="button" data-testid="project-edit" (click)="startEdit()" i18n="@@project.edit.start">Редагувати</button>
          }
        </div>
        @if (project()?.description) {
          <p class="mb-1 max-w-3xl opacity-80" data-testid="project-description">{{ project()?.description }}</p>
        }
      }
      @if (error()) {
        <p role="alert" class="mb-2 text-red-700" data-testid="project-error">{{ error() }}</p>
      }

      <nav class="ac-tabs mt-3" aria-label="Вкладки проєкту" i18n-aria-label="@@project.tabs.label" data-testid="project-tabs">
        @for (t of visibleTabs(); track t.path) {
          <a
            class="ac-tab"
            [routerLink]="t.path"
            routerLinkActive="active"
            [attr.data-testid]="t.testId"
            >{{ t.label }}</a
          >
        }
      </nav>
    </section>
    }
    <router-outlet />
  `,
})
export class ProjectPage implements OnInit {
  private readonly api = inject(WorkflowsApiService);
  private readonly context = inject(ProjectContextService);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  readonly projectId = inject(ActivatedRoute).snapshot.paramMap.get('id') ?? '';
  readonly project = signal<WorkflowGroup | null>(null);
  readonly error = signal<string | null>(null);
  readonly editing = signal(false);
  readonly saving = signal(false);
  readonly canManage = computed(() => this.project()?.role === 'admin' || this.project()?.role === 'editor');

  readonly name = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly description = new FormControl('', { nonNullable: true });
  readonly icon = new FormControl('', { nonNullable: true });

  readonly tabs: ProjectTab[] = [
    { path: 'app', testId: 'tab-app', label: $localize`:@@project.tab.app:Застосунок` },
    { path: 'overview', testId: 'tab-overview', label: $localize`:@@project.tab.overview:Огляд` },
    { path: 'workflows', testId: 'tab-workflows', label: $localize`:@@project.tab.workflows:Сценарії` },
    { path: 'tables', testId: 'tab-tables', label: $localize`:@@project.tab.tables:Таблиці` },
    { path: 'connections', testId: 'tab-connections', label: $localize`:@@project.tab.connections:Підключення` },
    { path: 'members', testId: 'tab-members', label: $localize`:@@project.tab.members:Учасники` },
  ];

  /** Користувач лише застосунку (F8-04) бачить тільки «Огляд» і «Застосунок». */
  readonly visibleTabs = computed(() =>
    this.context.appOnlyProject(this.auth.user()?.role)?.id === this.projectId
      ? this.tabs.filter((t) => t.path === 'overview' || t.path === 'app')
      : this.tabs,
  );

  /** Користувач лише застосунку, чий проєкт має оболонку (F8-18): рамки проєкту немає — лише оболонка застосунку. */
  readonly shellOnly = computed(() => {
    const group = this.context.appOnlyProject(this.auth.user()?.role);
    return group?.id === this.projectId && !!group.app;
  });

  ngOnInit(): void {
    // Вкладки-сторінки фільтруються за проєктом із шапки.
    this.context.select(this.projectId);
    this.api
      .listGroups()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (groups) => this.project.set(groups.find((g) => g.id === this.projectId) ?? null),
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  startEdit(): void {
    const p = this.project();
    if (!p) return;
    this.name.setValue(p.name);
    this.description.setValue(p.description ?? '');
    this.icon.setValue(p.icon ?? '');
    this.editing.set(true);
  }

  save(): void {
    const name = this.name.value.trim();
    if (!name) return;
    this.saving.set(true);
    this.error.set(null);
    this.api.updateGroup(this.projectId, { name, description: this.description.value.trim(), icon: this.icon.value.trim() }).subscribe({
      next: (group) => {
        this.project.update((p) => (p ? { ...p, ...group, role: p.role } : group));
        this.editing.set(false);
        this.saving.set(false);
        this.context.reload();
      },
      error: (e: unknown) => {
        this.error.set(apiErrorMessage(e));
        this.saving.set(false);
      },
    });
  }
}
