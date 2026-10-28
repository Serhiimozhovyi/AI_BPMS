import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute } from '@angular/router';
import { forkJoin } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectAppRole, ProjectMember, WorkflowGroup, WorkflowsApiService } from '../../workflows/workflows-api.service';

type MemberRole = ProjectMember['role'];

/**
 * `/projects/:id/members` (F6-14, вкладка «Учасники» з F8-03): проєкт (група сценаріїв F3-63) і його учасники. Додає учасника за email, змінює роль і прибирає
 * admin або editor проєкту; viewer бачить список лише для читання. Права перевіряє Api — сторінка лише ховає кнопки.
 * F8-04: ролі застосунку проєкту (ключ і назва) і роль застосунку кожного учасника — від неї залежить, які сторінки
 * вкладки «Застосунок» він бачить.
 */
@Component({
  selector: 'app-project-members-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <section class="p-6">
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
              @if (appRoles().length > 0) {
                <th class="p-2" i18n="@@project.members.col.appRole">Роль у застосунку</th>
              }
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
                @if (appRoles().length > 0) {
                  <td class="p-2">
                    @if (canManage()) {
                      <select class="border" data-testid="member-app-role" [value]="m.appRole ?? ''" (change)="changeAppRole(m, $any($event.target).value)">
                        <option value="" i18n="@@project.members.noAppRole">—</option>
                        @for (r of appRoles(); track r.key) {
                          <option [value]="r.key">{{ r.name }}</option>
                        }
                      </select>
                    } @else {
                      {{ appRoleName(m.appRole) }}
                    }
                  </td>
                }
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

      <h2 class="mb-2 mt-8 text-lg font-semibold" i18n="@@project.appRoles.title">Ролі застосунку</h2>
      <p class="mb-4 max-w-3xl opacity-70" i18n="@@project.appRoles.hint">
        Ролі застосунку (наприклад, «Оператор», «Психолог») визначають, які сторінки вкладки «Застосунок» бачить учасник і чи бачить він
        чутливі дані таблиць проєкту.
      </p>
      @if (appRoles().length === 0) {
        <p class="mb-4 opacity-70" data-testid="app-roles-empty" i18n="@@project.appRoles.empty">Ролей застосунку ще немає.</p>
      } @else {
        <ul class="mb-4 max-w-3xl" data-testid="app-roles">
          @for (r of appRoles(); track r.key) {
            <li class="flex items-center gap-2 py-1" [attr.data-app-role]="r.key">
              <span class="font-medium">{{ r.name }}</span>
              <code class="opacity-70">{{ r.key }}</code>
              @if (canManage()) {
                <button mat-button type="button" data-testid="app-role-remove" [disabled]="saving()" (click)="removeAppRole(r)" i18n="@@project.appRoles.remove">Прибрати</button>
              }
            </li>
          }
        </ul>
      }
      @if (canManage()) {
        <div class="flex max-w-3xl flex-wrap items-start gap-2" data-testid="app-role-form">
          <mat-form-field appearance="outline" class="w-48">
            <mat-label i18n="@@project.appRoles.key">Ключ (латиницею)</mat-label>
            <input matInput [formControl]="roleKey" name="roleKey" data-testid="app-role-key" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="w-64">
            <mat-label i18n="@@project.appRoles.name">Назва</mat-label>
            <input matInput [formControl]="roleName" name="roleName" data-testid="app-role-name" (keydown.enter)="addAppRole()" />
          </mat-form-field>
          <button mat-flat-button type="button" data-testid="app-role-add" [disabled]="saving() || roleKey.invalid || roleName.invalid" (click)="addAppRole()" i18n="@@project.appRoles.add">Додати роль</button>
        </div>
      }
    </section>
  `,
})
export class ProjectMembersPage implements OnInit {
  private readonly api = inject(WorkflowsApiService);
  private readonly context = inject(ProjectContextService);
  private readonly destroyRef = inject(DestroyRef);

  readonly projectId = inject(ActivatedRoute).parent?.snapshot.paramMap.get('id') ?? '';
  readonly project = signal<WorkflowGroup | null>(null);
  readonly members = signal<ProjectMember[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly newRole = signal<MemberRole>('editor');
  readonly canManage = computed(() => this.project()?.role === 'admin' || this.project()?.role === 'editor');
  readonly appRoles = computed<ProjectAppRole[]>(() => this.project()?.appRoles ?? []);

  readonly email = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] });
  readonly roleKey = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^[a-z][a-z0-9_-]{0,39}$/)] });
  readonly roleName = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(100)] });

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

  appRoleName(key: string | null | undefined): string {
    if (!key) return '—';
    return this.appRoles().find((r) => r.key === key)?.name ?? key;
  }

  add(): void {
    const email = this.email.value.trim();
    if (!email || this.email.invalid) return;
    this.save(email, this.newRole(), () => this.email.reset(''));
  }

  changeRole(member: ProjectMember, role: MemberRole): void {
    if (role !== member.role) this.save(member.email, role, undefined, member.appRole ?? null);
  }

  changeAppRole(member: ProjectMember, appRole: string): void {
    const value = appRole || null;
    if (value !== (member.appRole ?? null)) this.save(member.email, member.role, undefined, value);
  }

  addAppRole(): void {
    const key = this.roleKey.value.trim();
    const name = this.roleName.value.trim();
    if (!key || !name || this.roleKey.invalid || this.roleName.invalid) return;
    this.saveAppRoles([...this.appRoles().filter((r) => r.key !== key), { key, name }], () => {
      this.roleKey.reset('');
      this.roleName.reset('');
    });
  }

  removeAppRole(role: ProjectAppRole): void {
    this.saveAppRoles(this.appRoles().filter((r) => r.key !== role.key));
  }

  private saveAppRoles(appRoles: ProjectAppRole[], done?: () => void): void {
    this.saving.set(true);
    this.error.set(null);
    this.api.updateGroup(this.projectId, { appRoles }).subscribe({
      next: (group) => {
        this.project.update((p) => (p ? { ...p, appRoles: group.appRoles ?? appRoles } : p));
        this.saving.set(false);
        done?.();
        this.context.reload();
      },
      error: (e: unknown) => this.fail(e),
    });
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

  private save(email: string, role: MemberRole, done?: () => void, appRole: string | null = null): void {
    this.saving.set(true);
    this.error.set(null);
    this.api.setMember(this.projectId, email, role, appRole).subscribe({
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
