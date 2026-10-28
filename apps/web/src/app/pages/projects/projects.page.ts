import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
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
import { WorkflowGroup, WorkflowsApiService } from '../../workflows/workflows-api.service';

/**
 * `/projects` (F8-03): картки проєктів, які бачить користувач — іконка, назва, опис, кількість сценаріїв і моя роль.
 * Картка відкриває сторінку проєкту. «Створити проєкт» бачить лише глобальний editor чи admin; новий проєкт одразу
 * відкривається.
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
            class="block rounded-lg border border-ac-line p-4 no-underline hover:shadow"
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
