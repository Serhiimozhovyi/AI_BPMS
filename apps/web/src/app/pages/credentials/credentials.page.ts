import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { CredentialFormComponent } from '../../credentials/credential-form.component';
import { usageLabel } from '../../credentials/credential-input';
import { CredentialSummary, CredentialTypeInfo, CredentialsApiService } from '../../credentials/credentials-api.service';

/** `/credentials`: облікові дані без значень секретів — список, створення за типом, редагування, видалення (F4-02). */
@Component({
  selector: 'app-credentials-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, CredentialFormComponent],
  template: `
    <section class="p-6">
      <div class="mb-2 flex items-center gap-4">
        <h1 class="mat-font-headline-sm" i18n="@@credentials.title">Облікові дані</h1>
        @if (!adding() && types().length > 0) {
          <button mat-flat-button type="button" data-testid="add-button" (click)="startAdd()" i18n="@@credentials.add">
            Додати облікові дані
          </button>
        }
      </div>
      <p class="mb-4 max-w-3xl opacity-70" i18n="@@credentials.hint">
        Ключі, токени й паролі для конекторів, AI-вузлів і webhook-ів. Значення шифруються й після збереження більше не показуються.
      </p>

      @if (adding()) {
        <div class="mb-6 max-w-2xl rounded-lg border border-ac-line p-4">
          <h2 class="mb-2 font-semibold" i18n="@@credentials.form.newTitle">Нові облікові дані</h2>
          <app-credential-form [types]="types()" (saved)="onSaved($event)" (cancelled)="adding.set(false)" />
        </div>
      }

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (!loading() && credentials().length === 0 && !error()) {
        <p class="opacity-70" data-testid="empty" i18n="@@credentials.empty">Ще немає жодних облікових даних.</p>
      }

      @if (credentials().length > 0) {
        <table class="w-full text-left" data-testid="credentials-table">
          <thead>
            <tr class="border-b border-ac-line">
              <th class="p-2" i18n="@@credentials.col.name">Назва</th>
              <th class="p-2" i18n="@@credentials.col.type">Тип</th>
              <th class="p-2" i18n="@@credentials.col.fields">Заповнено</th>
              <th class="p-2" i18n="@@credentials.col.usage">Де використовується</th>
              <th class="p-2"></th>
            </tr>
          </thead>
          <tbody>
            @for (c of credentials(); track c.id) {
              <tr class="border-b border-ac-line-2 align-top" [attr.data-credential]="c.id">
                <td class="p-2 font-medium">{{ c.name }}</td>
                <td class="p-2">{{ typeLabel(c.type) }}</td>
                <td class="p-2 text-sm">
                  @if (c.error) {
                    <span class="text-red-700" data-testid="decrypt-error">{{ c.error }}</span>
                  } @else {
                    {{ c.fields.join(', ') || '—' }}
                  }
                </td>
                <td class="p-2 text-sm">{{ usage(c) }}</td>
                <td class="p-2 text-right whitespace-nowrap">
                  @if (confirming() === c.id) {
                    <span class="mr-2" i18n="@@credentials.delete.confirm">Видалити?</span>
                    <button mat-flat-button type="button" data-testid="confirm-delete" (click)="remove(c)" i18n="@@credentials.delete.yes">Так</button>
                    <button mat-button type="button" data-testid="cancel-delete" (click)="confirming.set(null)" i18n="@@credentials.delete.no">Ні</button>
                  } @else {
                    <button mat-button type="button" data-testid="edit" (click)="editing.set(c.id)" i18n="@@credentials.edit">Змінити</button>
                    <button mat-button type="button" data-testid="delete" (click)="confirming.set(c.id)" i18n="@@credentials.delete">Видалити</button>
                  }
                </td>
              </tr>
              @if (editing() === c.id) {
                <tr>
                  <td colspan="5" class="p-2">
                    <div class="max-w-2xl rounded-lg border border-ac-line p-4">
                      <app-credential-form [types]="types()" [credential]="c" (saved)="onSaved($event)" (cancelled)="editing.set(null)" />
                    </div>
                  </td>
                </tr>
              }
            }
          </tbody>
        </table>
      }
    </section>
  `,
})
export class CredentialsPage implements OnInit {
  private readonly api = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly credentials = signal<CredentialSummary[]>([]);
  readonly types = signal<CredentialTypeInfo[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly adding = signal(false);
  readonly editing = signal<string | null>(null);
  readonly confirming = signal<string | null>(null);

  readonly usage = usageLabel;

  ngOnInit(): void {
    this.loading.set(true);
    forkJoin([this.api.types(), this.api.list()])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ([types, list]) => {
          this.types.set(types);
          this.credentials.set(list);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  typeLabel(type: string): string {
    return this.types().find((t) => t.type === type)?.displayName ?? type;
  }

  startAdd(): void {
    this.editing.set(null);
    this.adding.set(true);
  }

  onSaved(saved: CredentialSummary): void {
    this.adding.set(false);
    this.editing.set(null);
    this.credentials.update((list) => {
      const rest = list.filter((c) => c.id !== saved.id);
      return [...rest, saved].sort((a, b) => a.name.localeCompare(b.name));
    });
  }

  remove(c: CredentialSummary): void {
    this.error.set(null);
    this.api
      .delete(c.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.confirming.set(null);
          this.credentials.update((list) => list.filter((x) => x.id !== c.id));
        },
        error: (e: unknown) => {
          this.confirming.set(null);
          this.error.set(apiErrorMessage(e));
        },
      });
  }
}
