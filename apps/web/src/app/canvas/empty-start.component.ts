import { ChangeDetectionStrategy, Component, DestroyRef, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, map, of } from 'rxjs';
import { ConnectorsApiService } from '../connectors/connectors-api.service';
import { WorkflowTemplate, WorkflowsApiService } from '../workflows/workflows-api.service';
import { ScreenPoint } from './quick-add';

/**
 * Порожній сценарій (F3-59): картка по центру канвасу — «Додати тригер» (спливаюча палітра лише з тригерами) або
 * «Почати з прикладу» (шаблони з `GET /workflows/templates`) або
 * «Описати процес» (помічник, F3-60). Що робити з вибором — вирішує редактор. Шаблон, якому бракує конекторів з каталогу
 * (F6-16), показує їх із посиланням «Встановити».
 */

/** Конектор каталогу, якого шаблону бракує. */
export interface MissingConnector {
  id: string;
  name: string;
}

/** Залежності шаблону, яких немає серед встановлених конекторів (`catalogId`). */
export function missingConnectors(template: WorkflowTemplate, installed: ReadonlySet<string>, names: ReadonlyMap<string, string>): MissingConnector[] {
  return (template.connectors ?? []).filter((id) => !installed.has(id)).map((id) => ({ id, name: names.get(id) ?? id }));
}
@Component({
  selector: 'app-empty-start',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, RouterLink],
  template: `
    <section class="start" data-testid="empty-start" aria-labelledby="empty-start-title">
      @if (templates(); as list) {
        <header class="start__head">
          <h2 id="empty-start-title" class="start__title" i18n="@@emptystart.templates.title">Почати з прикладу</h2>
          <button mat-button type="button" data-testid="templates-back" (click)="templates.set(null)" i18n="@@emptystart.back">Назад</button>
        </header>
        <ul class="start__list">
          @for (t of list; track t.id) {
            <li>
              <button type="button" class="start__template" data-testid="template" [attr.data-template]="t.id" (click)="pick.emit(t)">
                <span class="start__name">{{ t.name }}</span>
                <span class="start__description">{{ t.description }}</span>
              </button>
              @if (missing()[t.id]; as need) {
                <p class="start__missing" data-testid="template-missing" [attr.data-template-missing]="t.id">
                  <span i18n="@@emptystart.templates.missing">Потрібні конектори:</span>
                  @for (c of need; track c.id) {
                    <a [routerLink]="['/connections']" [queryParams]="{ tab: 'catalog', catalog: c.id }" [attr.data-install]="c.id">{{ c.name }}</a>
                  }
                  <span i18n="@@emptystart.templates.missingHint">— встановіть з каталогу.</span>
                </p>
              }
            </li>
          } @empty {
            <li class="start__description" i18n="@@emptystart.templates.empty">Прикладів немає.</li>
          }
        </ul>
      } @else {
        <h2 id="empty-start-title" class="start__title" i18n="@@emptystart.title">З чого почнеться процес?</h2>
        <p class="start__description" i18n="@@emptystart.hint">Сценарій починається з тригера: ручний запуск, webhook, розклад або подія в системі.</p>
        <div class="start__actions">
          <button mat-flat-button type="button" data-testid="start-trigger" (click)="addTrigger.emit(anchorOf($event))" i18n="@@emptystart.trigger">Додати тригер</button>
          <button mat-stroked-button type="button" data-testid="start-template" [disabled]="loading()" (click)="loadTemplates()" i18n="@@emptystart.template">
            Почати з прикладу
          </button>
          <button mat-stroked-button type="button" data-testid="start-describe" (click)="describe.emit()" i18n="@@emptystart.describe">Описати процес</button>
        </div>
        @if (error()) {
          <p role="alert" class="start__error" data-testid="templates-error">{{ error() }}</p>
        }
      }
    </section>
  `,
  styles: `
    .start {
      display: flex;
      flex-direction: column;
      gap: 12px;
      width: min(440px, calc(100vw - 32px));
      max-height: 70vh;
      padding: 24px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius-lg);
      background: var(--ac-white);
      box-shadow: var(--ac-shadow);
      color: var(--ac-vivid);
    }

    .start__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }

    .start__title {
      margin: 0;
      font: var(--bp-font-h4);
    }

    .start__description {
      margin: 0;
      color: var(--ac-cool-grey-4);
      font: var(--bp-font-p3);
    }

    .start__actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }

    .start__error {
      margin: 0;
      color: var(--ac-red);
      font-size: 13px;
    }

    .start__list {
      display: flex;
      flex-direction: column;
      gap: 4px;
      margin: 0;
      padding: 0;
      overflow-y: auto;
      list-style: none;
    }

    .start__template {
      display: flex;
      flex-direction: column;
      gap: 2px;
      width: 100%;
      padding: 8px 12px;
      border: 1px solid transparent;
      border-radius: var(--ac-radius);
      background: none;
      color: inherit;
      text-align: left;
      cursor: pointer;
    }

    .start__template:hover,
    .start__template:focus-visible {
      border-color: var(--ac-kit);
      background: var(--ac-cool-grey);
    }

    .start__missing {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin: 0 12px 4px;
      color: var(--ac-cool-grey-4);
      font: 400 12px/16px Ubuntu, sans-serif;
    }

    .start__missing a {
      color: var(--ac-kit);
    }

    .start__name {
      font: var(--bp-font-h7);
    }
  `,
})
export class EmptyStartComponent {
  private readonly workflows = inject(WorkflowsApiService);
  private readonly connectors = inject(ConnectorsApiService);
  private readonly destroyRef = inject(DestroyRef);

  /** «Додати тригер»: точка, біля якої відкрити спливаючу палітру. */
  readonly addTrigger = output<ScreenPoint>();
  /** «Описати процес»: відкрити помічника (F3-60). */
  readonly describe = output<void>();
  /** Вибраний шаблон. */
  readonly pick = output<WorkflowTemplate>();

  /** Список шаблонів після «Почати з прикладу»; `null` — головна картка. */
  protected readonly templates = signal<WorkflowTemplate[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Конектори, яких бракує шаблонам (id шаблону → список); порожні не зберігаються. */
  protected readonly missing = signal<Record<string, MissingConnector[]>>({});

  protected loadTemplates(): void {
    this.loading.set(true);
    this.error.set(null);
    this.workflows
      .templates()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.loading.set(false);
          this.templates.set(list);
          this.checkConnectors(list);
        },
        error: () => {
          this.loading.set(false);
          this.error.set($localize`:@@emptystart.templates.error:Не вдалося завантажити приклади. Спробуйте ще раз.`);
        },
      });
  }

  /** Перевіряє залежності шаблонів; без доступу до конекторів чи каталогу підказок просто немає. */
  private checkConnectors(list: WorkflowTemplate[]): void {
    if (!list.some((t) => (t.connectors ?? []).length > 0)) return;
    forkJoin({
      installed: this.connectors.list().pipe(map((page) => new Set(page.items.map((c) => c.catalogId).filter((id): id is string => !!id)))),
      names: this.connectors.catalog().pipe(map((entries) => new Map(entries.map((e) => [e.manifest.id, e.manifest.name] as const)))),
    })
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        if (!result) return;
        const missing: Record<string, MissingConnector[]> = {};
        for (const t of list) {
          const need = missingConnectors(t, result.installed, result.names);
          if (need.length > 0) missing[t.id] = need;
        }
        this.missing.set(missing);
      });
  }

  protected anchorOf(event: MouseEvent): ScreenPoint {
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    return { x: box.left, y: box.bottom + 8 };
  }
}
