import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { BpButtonDirective } from '../shared/button.directive';
import { SelectSearchComponent } from '../shared/select-search/select-search.component';
import { ProjectMember, WorkflowGroup, WorkflowsApiService } from '../workflows/workflows-api.service';
import { ViewAsMode } from './view-as.service';

export interface ViewAsDialogData {
  project: WorkflowGroup;
  /** Admin може дивитись очима конкретного учасника. */
  isAdmin: boolean;
}

/** Варіант у списку ролей: `value` — `editor`, `viewer`, `app:<ключ>` або `member`. */
export interface ViewAsOption {
  value: string;
  label: string;
  hint: string;
}

/** Ролі, якими можна переглянути проєкт: редактор, учасник без ролі, кожна роль застосунку, для admin — учасник. */
export function viewAsOptions(project: WorkflowGroup, isAdmin: boolean): ViewAsOption[] {
  const options: ViewAsOption[] = [
    {
      value: 'editor',
      label: $localize`:@@viewAs.option.editor:Редактор проєкту`,
      hint: $localize`:@@viewAs.option.editor.hint:Сценарії, таблиці й усі сторінки проєкту, без адміністрування`,
    },
    {
      value: 'viewer',
      label: $localize`:@@viewAs.option.viewer:Учасник без ролі застосунку`,
      hint: $localize`:@@viewAs.option.viewer.hint:Лише читання, сторінки для всіх, чутливі колонки приховано`,
    },
    ...(project.appRoles ?? []).map((r) => ({
      value: `app:${r.key}`,
      label: r.name || r.key,
      hint: $localize`:@@viewAs.option.app.hint:Роль застосунку: її сторінки й меню, як у звичайного користувача`,
    })),
  ];
  if (isAdmin) {
    options.push({
      value: 'member',
      label: $localize`:@@viewAs.option.member:Конкретний учасник…`,
      hint: $localize`:@@viewAs.option.member.hint:Його записи, задачі й фільтри «мої»`,
    });
  }
  return options;
}

/** Режим для вибору в діалозі; `member` — потрібен ще учасник. */
export function viewAsModeFor(project: WorkflowGroup, value: string, member: ProjectMember | null): ViewAsMode | null {
  const base = { projectId: project.id, projectName: project.name };
  if (value === 'editor') return { ...base, role: 'editor', label: $localize`:@@viewAs.option.editor:Редактор проєкту` };
  if (value === 'viewer') return { ...base, role: 'viewer', label: $localize`:@@viewAs.option.viewer:Учасник без ролі застосунку` };
  if (value.startsWith('app:')) {
    const key = value.slice(4);
    const role = (project.appRoles ?? []).find((r) => r.key === key);
    return role ? { ...base, role: 'viewer', appRole: key, label: role.name || key } : null;
  }
  if (value === 'member' && member) return { ...base, memberId: member.userId, label: member.email };
  return null;
}

/**
 * «Переглянути як» (F12-02): вибір ролі проєкту, очима якої показати AI_BPMS і застосунок. Закривається
 * {@link ViewAsMode} або `undefined`.
 */
@Component({
  selector: 'app-view-as-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatSelectModule, SelectSearchComponent],
  template: `
    <h2 mat-dialog-title i18n="@@viewAs.title">Переглянути як користувач</h2>
    <button mat-icon-button type="button" bpVariant="close" class="bp-dialog-close" mat-dialog-close data-testid="dialog-close" i18n-aria-label="@@dialog.close" aria-label="Закрити вікно"><mat-icon>close</mat-icon></button>
    <mat-dialog-content>
      <p class="view-as__lead" i18n="@@viewAs.lead">
        Проєкт «{{ data.project.name }}» відкриється так, як його бачить учасник обраної ролі: ті самі меню, сторінки й права. Змінювати дані в цьому режимі не можна.
      </p>
      <div class="flex flex-col gap-2 pt-2 pb-4">
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label i18n="@@viewAs.role">Роль</mat-label>
          <mat-select data-testid="view-as-role" [value]="choice()" (selectionChange)="choice.set($event.value)">
            <bp-select-search />
            @for (o of options; track o.value) {
              <mat-option [value]="o.value" [attr.data-value]="o.value">{{ o.label }}</mat-option>
            }
          </mat-select>
          @if (hint(); as text) {
            <mat-hint data-testid="view-as-hint">{{ text }}</mat-hint>
          }
        </mat-form-field>
        @if (choice() === 'member') {
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label i18n="@@viewAs.member">Учасник</mat-label>
            <mat-select data-testid="view-as-member" [value]="memberId()" (selectionChange)="memberId.set($event.value)">
              <bp-select-search />
              @for (m of members(); track m.userId) {
                <mat-option [value]="m.userId" [attr.data-value]="m.email">{{ m.email }}{{ roleName(m) }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" data-testid="view-as-cancel" mat-dialog-close i18n="@@viewAs.cancel">Скасувати</button>
      <button mat-flat-button type="button" data-testid="view-as-apply" [disabled]="!mode()" (click)="apply()" i18n="@@viewAs.apply">Переглянути</button>
    </mat-dialog-actions>
  `,
  styles: `
    .view-as__lead {
      margin: 0;
      max-width: 440px;
      color: var(--bp-text-muted);
    }
  `,
})
export class ViewAsDialogComponent {
  protected readonly data = inject<ViewAsDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ViewAsDialogComponent, ViewAsMode>>(MatDialogRef);
  private readonly api = inject(WorkflowsApiService);

  protected readonly options = viewAsOptions(this.data.project, this.data.isAdmin);
  protected readonly choice = signal<string>(this.options.find((o) => o.value.startsWith('app:'))?.value ?? 'viewer');
  protected readonly members = signal<ProjectMember[]>([]);
  protected readonly memberId = signal<string | null>(null);
  protected readonly hint = computed(() => this.options.find((o) => o.value === this.choice())?.hint ?? '');
  protected readonly mode = computed(() =>
    viewAsModeFor(this.data.project, this.choice(), this.members().find((m) => m.userId === this.memberId()) ?? null),
  );

  constructor() {
    if (this.data.isAdmin) {
      this.api.listMembers(this.data.project.id).subscribe({ next: (list) => this.members.set(list), error: () => this.members.set([]) });
    }
  }

  /** « · Психолог» після пошти учасника — його роль застосунку. */
  protected roleName(member: ProjectMember): string {
    const role = (this.data.project.appRoles ?? []).find((r) => r.key === member.appRole);
    return role ? ` · ${role.name}` : '';
  }

  protected apply(): void {
    const mode = this.mode();
    if (mode) this.ref.close(mode);
  }
}
