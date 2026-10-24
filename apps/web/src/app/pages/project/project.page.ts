import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectMember, WorkflowGroup, WorkflowsApiService } from '../../workflows/workflows-api.service';

type MemberRole = ProjectMember['role'];

/**
 * `/projects/:id` (F6-14): проєкт (група сценаріїв F3-63) і його учасники. Додає учасника за email, змінює роль і прибирає
 * admin або editor проєкту; viewer бачить список лише для читання. Права перевіряє Api — сторінка лише ховає кнопки.
 */
@Component({
  selector: 'app-project-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <section class="p-6">
      <a class="mb-2 block text-sm" [routerLink]="['/workflows']" [queryParams]="{ group: projectId }" i18n="@@project.back">← До сценаріїв проєкту</a>
      <h1 class="mat-font-headline-sm mb-1" data-testid="project-name">{{ project()?.name ?? '…' }}</h1>
      @if (project(); as p) {
        <p class="mb-4 opacity-70" data-testid="project-role">
          <ng-container i18n="@@project.myRole">Моя роль:</ng-container> {{ roleLabel(p.role ?? 'viewer') }}
        </p>
      }

      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="project-error">{{ error() }}</p>
      }

      <h2 class="mb-2 text-lg font-semibold" i18n="@@project.members.title">Учасники</h2>
      <p class="mb-4 max-w-3xl opacity-70" i18n="@@project.members.hint">
        Editor проєкту створює й змінює його сценарії, облікові дані й конектори; viewer лише переглядає. Адміністратори бачать
        усі проєкти без членства.
      </p>

      @if (canManage()) {
        <div class="mb-4 flex max-w-3xl flex-wrap items-start gap-2" data-testid="member-form">
          <mat-form-field appearance="outline" class="w-72">
            <mat-label i18n="@@project.members.email">Email користувача</mat-label>
            <input matInput type="email" [formControl]="email" name="email" data-testid="member-email" (keydown.enter)="add()" />
          </mat-form-field>
          <select class="border" data-testid="member-role" aria-label="Роль" i18n-aria-label="@@project.members.role" [value]="newRole()" (change)="newRole.set($any($event.target).value)">
            <option value="editor" i18n="@@project.role.editor">Editor</option>
            <option value="viewer" i18n="@@project.role.viewer">Viewer</option>
          </select>
          <button mat-flat-button type="button" data-testid="member-add" [disabled]="saving() || email.invalid" (click)="add()" i18n="@@project.members.add">Додати</button>
        </div>
      }

      @if (!loading() && members().length === 0 && !error()) {
        <p class="opacity-70" data-testid="members-empty" i18n="@@project.members.empty">Учасників ще немає.</p>
      }

      @if (members().length > 0) {
        <table class="w-full max-w-3xl text-left" data-testid="members-table">
          <thead>
            <tr>
              <th class="p-2" i18n="@@project.members.col.email">Email</th>
              <th class="p-2" i18n="@@project.members.col.role">Роль у проєкті</th>
              @if (canManage()) {
                <th class="p-2"></th>
              }
            </tr>
          </thead>
          <tbody>
            @for (m of members(); track m.userId) {
              <tr [attr.data-member]="m.email">
                <td class="p-2">{{ m.email }}</td>
                <td class="p-2">
                  @if (canManage()) {
                    <select class="border" data-testid="member-role-change" [value]="m.role" (change)="changeRole(m, $any($event.target).value)">
                      <option value="editor" i18n="@@project.role.editor">Editor</option>
                      <option value="viewer" i18n="@@project.role.viewer">Viewer</option>
                    </select>
                  } @else {
                    {{ roleLabel(m.role) }}
                  }
                </td>
                @if (canManage()) {
                  <td class="whitespace-nowrap p-2 text-right">
                    <button mat-button type="button" data-testid="member-remove" (click)="remove(m)" i18n="@@project.members.remove">Прибрати</button>
                  </td>
                }
              </tr>
            }
          </tbody>
        </table>
      }
    </section>
  `,
})
export class ProjectPage implements OnInit {
  private readonly api = inject(WorkflowsApiService);
  private readonly context = inject(ProjectContextService);
  private readonly destroyRef = inject(DestroyRef);

  readonly projectId = inject(ActivatedRoute).snapshot.paramMap.get('id') ?? '';
  readonly project = signal<WorkflowGroup | null>(null);
  readonly members = signal<ProjectMember[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly newRole = signal<MemberRole>('editor');
  readonly canManage = computed(() => this.project()?.role === 'admin' || this.project()?.role === 'editor');

  readonly email = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] });

  ngOnInit(): void {
    this.loading.set(true);
    forkJoin({ groups: this.api.listGroups(), members: this.api.listMembers(this.projectId) })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ groups, members }) => {
          this.project.set(groups.find((g) => g.id === this.projectId) ?? null);
          this.members.set(members);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  roleLabel(role: string): string {
    switch (role) {
      case 'admin':
        return $localize`:@@project.role.admin:Адміністратор`;
      case 'editor':
        return $localize`:@@project.role.editor:Editor`;
      default:
        return $localize`:@@project.role.viewer:Viewer`;
    }
  }

  add(): void {
    const email = this.email.value.trim();
    if (!email || this.email.invalid) return;
    this.save(email, this.newRole(), () => this.email.reset(''));
  }

  changeRole(member: ProjectMember, role: MemberRole): void {
    if (role !== member.role) this.save(member.email, role);
  }

  remove(member: ProjectMember): void {
    this.saving.set(true);
    this.error.set(null);
    this.api.removeMember(this.projectId, member.userId).subscribe({
      next: () => {
        this.members.update((list) => list.filter((m) => m.userId !== member.userId));
        this.saving.set(false);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private save(email: string, role: MemberRole, done?: () => void): void {
    this.saving.set(true);
    this.error.set(null);
    this.api.setMember(this.projectId, email, role).subscribe({
      next: (member) => {
        this.members.update((list) =>
          [...list.filter((m) => m.userId !== member.userId), member].sort((a, b) => a.email.localeCompare(b.email)),
        );
        this.saving.set(false);
        done?.();
        this.context.reload();
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private fail(e: unknown): void {
    this.error.set(apiErrorMessage(e));
    this.saving.set(false);
  }
}
