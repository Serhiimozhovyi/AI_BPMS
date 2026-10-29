import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, inject, input, output, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { PageSubmitResult, ProjectPageView, ProjectPagesApiService } from '../../projects/project-pages-api.service';
import { PageFormComponent } from './page-form.component';

/**
 * Бокова панель з формою сторінки застосунку (F8-16, `mode: drawer`): форма відкривається поверх списку без переходу,
 * після збереження панель закривається, а список перечитується. Фокус переходить у панель, `Esc` закриває її й повертає
 * фокус на кнопку, що її відкрила. Сторінка не форма — `unsupported`: хост відкриє її переходом.
 */
@Component({
  selector: 'app-page-drawer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, PageFormComponent],
  host: { '(document:keydown.escape)': 'close()' },
  template: `
    <div class="fixed inset-0 z-40 bg-black/30" role="presentation" data-testid="drawer-backdrop" (click)="close()" (keyup.escape)="close()"></div>
    <aside
      #panel
      class="fixed inset-y-0 right-0 z-50 flex w-[28rem] max-w-full flex-col overflow-y-auto bg-white p-5 shadow-xl"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="titleId"
      data-testid="app-drawer"
      tabindex="-1"
    >
      <div class="mb-3 flex items-center gap-3">
        <h2 class="grow text-lg font-semibold" [id]="titleId" data-testid="drawer-title">{{ view()?.page?.name }}</h2>
        <button mat-icon-button type="button" aria-label="Закрити" i18n-aria-label="@@app.page.close" data-testid="drawer-close" (click)="close()">
          <span aria-hidden="true">✕</span>
        </button>
      </div>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="text-red-700" data-testid="drawer-error">{{ error() }}</p>
      }
      @if (view(); as v) {
        <app-page-form [projectId]="projectId()" [view]="v" [rowKey]="rowKey()" [prefill]="prefill()" (submitted)="done($event)" />
      }
    </aside>
  `,
})
export class PageDrawerComponent implements OnInit, OnDestroy {
  private readonly api = inject(ProjectPagesApiService);
  private readonly panel = viewChild<ElementRef<HTMLElement>>('panel');
  private opener: HTMLElement | null = null;

  readonly projectId = input.required<string>();
  /** Ключ сторінки-форми. */
  readonly pageKey = input.required<string>();
  readonly rowKey = input<string | null>(null);
  readonly prefill = input<Record<string, unknown>>({});

  /** Панель закрито без збереження. */
  readonly closed = output<void>();
  /** Форму збережено: хост перечитає дані й покаже повідомлення сценарію. */
  readonly saved = output<PageSubmitResult>();
  /** Сторінка не форма: її відкривають переходом. */
  readonly unsupported = output<void>();

  protected readonly titleId = `drawer-title-${Math.random().toString(36).slice(2, 8)}`;
  readonly view = signal<ProjectPageView | null>(null);
  readonly loading = signal(true);
  readonly error = signal('');

  ngOnInit(): void {
    this.opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setTimeout(() => this.panel()?.nativeElement.focus());
    this.api.get(this.projectId(), this.pageKey()).subscribe({
      next: (view) => {
        this.loading.set(false);
        if (view.page.kind !== 'form') {
          this.unsupported.emit();
          return;
        }
        this.view.set(view);
      },
      error: (e: unknown) => {
        this.loading.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }

  ngOnDestroy(): void {
    this.opener?.focus?.();
  }

  close(): void {
    this.closed.emit();
  }

  done(result: PageSubmitResult): void {
    this.saved.emit(result);
  }
}
