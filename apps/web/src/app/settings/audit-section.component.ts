import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { AUDIT_ENTITY_TYPES, AuditApiService, AuditEntry, auditActionLabel, auditEntityLabel, auditSummary } from './audit-api.service';
import { SelectSearchComponent } from '../shared/select-search/select-search.component';

/** Записів на сторінці журналу. */
export const AUDIT_PAGE_SIZE = 50;

/**
 * Блок «Журнал аудиту» у `/settings` (F6-15), лише для admin: хто й коли створив, змінив чи видалив сценарій, облікові
 * дані, конектор, ключ, змінну чи таблицю. Нові записи першими; фільтр за типом сутності.
 */
@Component({
  selector: 'app-audit-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, SelectSearchComponent],
  template: `
    <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.audit.title">Журнал аудиту</h2>
    <p class="mb-4 max-w-3xl opacity-70" i18n="@@settings.audit.hint">
      Хто й коли змінював сценарії, облікові дані, конектори, ключі, змінні й таблиці. Значення секретів сюди не потрапляють.
    </p>

    <div class="mb-2 flex flex-wrap items-start gap-2">
      <mat-form-field appearance="outline" class="w-56">
        <mat-label i18n="@@settings.audit.entity">Що змінено</mat-label>
        <mat-select data-testid="audit-entity" [value]="entityType()" (selectionChange)="filterBy($event.value)">
          <bp-select-search />
          <mat-option value="" i18n="@@settings.audit.all">Усе</mat-option>
          @for (t of entityTypes; track t) {
            <mat-option [value]="t">{{ entityLabel(t) }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
    </div>

    @if (error()) {
      <p role="alert" class="mb-4 text-red-700" data-testid="audit-error">{{ error() }}</p>
    }

    @if (!loading() && entries().length === 0 && !error()) {
      <p class="mb-6 opacity-70" data-testid="audit-empty" i18n="@@settings.audit.empty">Записів немає.</p>
    }

    @if (entries().length > 0) {
      <div class="overflow-x-auto">
        <table class="mb-2 w-full max-w-5xl text-left" data-testid="audit-table">
          <thead>
            <tr>
              <th class="p-2" i18n="@@settings.audit.col.at">Коли</th>
              <th class="p-2" i18n="@@settings.audit.col.who">Хто</th>
              <th class="p-2" i18n="@@settings.audit.col.what">Що</th>
              <th class="p-2" i18n="@@settings.audit.col.details">Подробиці</th>
            </tr>
          </thead>
          <tbody>
            @for (e of entries(); track e.id) {
              <tr [attr.data-audit]="e.action">
                <td class="whitespace-nowrap p-2">{{ e.at | date: 'dd.MM.yyyy HH:mm:ss' }}</td>
                <td class="p-2">
                  @if (e.userId) {
                    {{ e.userEmail ?? e.userId }}
                  } @else {
                    <span class="opacity-70" i18n="@@settings.audit.system">система</span>
                  }
                  @if (e.apiKeyId) {
                    <span class="block text-sm opacity-60" data-testid="audit-key" i18n="@@settings.audit.viaKey">
                      ключ {{ e.apiKeyName ?? e.apiKeyId }}
                    </span>
                  }
                </td>
                <td class="p-2">{{ entityLabel(e.entityType) }} — {{ actionLabel(e.action) }}</td>
                <td class="max-w-md p-2">{{ summary(e) }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      @if (pages() > 1) {
        <div class="mb-6 flex items-center gap-2">
          <button mat-button type="button" data-testid="audit-prev" [disabled]="page() <= 1" (click)="load(page() - 1)" i18n="@@settings.audit.prev">
            Новіші
          </button>
          <span>{{ page() }} / {{ pages() }}</span>
          <button mat-button type="button" data-testid="audit-next" [disabled]="page() >= pages()" (click)="load(page() + 1)" i18n="@@settings.audit.next">
            Старіші
          </button>
        </div>
      }
    }
  `,
})
export class AuditSection implements OnInit {
  private readonly api = inject(AuditApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly entityTypes = AUDIT_ENTITY_TYPES;
  readonly entries = signal<AuditEntry[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly entityType = signal('');
  readonly page = signal(1);
  readonly total = signal(0);
  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / AUDIT_PAGE_SIZE)));

  readonly entityLabel = auditEntityLabel;
  readonly actionLabel = auditActionLabel;
  readonly summary = auditSummary;

  ngOnInit(): void {
    this.load(1);
  }

  filterBy(entityType: string): void {
    this.entityType.set(entityType);
    this.load(1);
  }

  load(page: number): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .list({ entityType: this.entityType() }, page, AUDIT_PAGE_SIZE)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.entries.set(result.items);
          this.total.set(result.total);
          this.page.set(result.page);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }
}
