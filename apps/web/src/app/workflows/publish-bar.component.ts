import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { catchError, of, switchMap } from 'rxjs';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { describeDiff, diffDefinitions, isEmptyDiff } from './version-diff';
import { WorkflowDefinition, WorkflowDetail, WorkflowsApiService, validationErrorsOf } from './workflows-api.service';

/** Стан публікації для заголовка редактора (F6-10). */
export type PublishState = 'published' | 'changed' | 'never';

/** `published` — тригери виконують чернетку; `changed` — чернетка новіша за опубліковану; `never` — ще не публікувався. */
export function publishStateOf(workflow: Pick<WorkflowDetail, 'version' | 'publishedVersion'>): PublishState {
  if (workflow.publishedVersion == null) return 'never';
  return workflow.publishedVersion === workflow.version ? 'published' : 'changed';
}

/**
 * «Чернетка і публікація» (F6-10) у заголовку редактора: стан, короткий diff до опублікованої версії,
 * «Опублікувати» і «Скасувати зміни». Тригери виконують опубліковану версію; автозбереження пише чернетку.
 */
@Component({
  selector: 'app-publish-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    @if (workflow(); as wf) {
      <span class="inline-flex min-w-0 flex-wrap items-center gap-2" data-testid="publish-bar">
        @switch (state()) {
          @case ('published') {
            <span class="text-sm text-[var(--bp-text-muted)]" data-testid="publish-state" data-state="published" i18n="@@publish.state.published">Опубліковано · версія {{ wf.publishedVersion }}</span>
          }
          @case ('changed') {
            <span class="rounded bg-[var(--bp-yellow-15)] px-1.5 text-sm" data-testid="publish-state" data-state="changed" [title]="diffText() ?? ''" i18n="@@publish.state.changed">Є неопубліковані зміни</span>
            @if (diffText(); as text) {
              <span class="max-w-[16rem] truncate text-xs text-[var(--bp-text-muted)]" data-testid="publish-diff" [title]="text">{{ text }}</span>
            }
          }
          @case ('never') {
            <span class="rounded bg-[var(--bp-yellow-15)] px-1.5 text-sm" data-testid="publish-state" data-state="never" i18n="@@publish.state.never">Не опубліковано</span>
          }
        }
        @if (!readonly() && state() !== 'published') {
          <button mat-stroked-button type="button" data-testid="publish" [disabled]="busy() || saving()" (click)="publish(wf)" i18n-title="@@publish.hint" title="Перевірити чернетку і передати її тригерам (webhook, розклад, опитування)" i18n="@@publish">Опублікувати</button>
          @if (state() === 'changed') {
            <button mat-button type="button" data-testid="discard-draft" [disabled]="busy() || saving()" (click)="discard(wf)" i18n-title="@@publish.discard.hint" title="Повернути чернетку до опублікованої версії" i18n="@@publish.discard">Скасувати зміни</button>
          }
        }
        @if (error(); as message) {
          <span role="alert" class="text-sm text-red-700" data-testid="publish-error">{{ message }}</span>
        }
      </span>
    }
  `,
})
export class PublishBarComponent {
  private readonly workflows = inject(WorkflowsApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workflow = input<WorkflowDetail | null>(null);
  /** Поточна чернетка в редакторі (може бути новішою за збережену). */
  readonly draft = input<WorkflowDefinition | null>(null);
  /** Є незбережені зміни — публікувати можна лише збережену чернетку. */
  readonly saving = input(false);
  readonly readonly = input(false);

  /** Відповідь Api на публікацію. */
  readonly published = output<WorkflowDetail>();
  /** Відповідь Api на «Скасувати зміни»: визначення — опублікована версія. */
  readonly discarded = output<WorkflowDetail>();

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly state = computed(() => {
    const wf = this.workflow();
    return wf ? publishStateOf(wf) : 'never';
  });

  /** Визначення опублікованої версії — для diff, лише коли чернетка від неї відрізняється. */
  private readonly publishedDefinition = signal<WorkflowDefinition | null>(null);
  protected readonly diffText = computed(() => {
    const before = this.publishedDefinition();
    const after = this.draft();
    if (!before || !after || this.state() !== 'changed') return null;
    const diff = diffDefinitions(before, after);
    return isEmptyDiff(diff) ? null : describeDiff(diff);
  });

  constructor() {
    toObservable(computed(() => {
      const wf = this.workflow();
      return wf && publishStateOf(wf) === 'changed' ? `${wf.id}/${wf.publishedVersion}` : null;
    }))
      .pipe(
        switchMap((key) => {
          if (!key) return of(null);
          const [id, version] = key.split('/');
          return this.workflows.version(id, Number(version)).pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((definition) => this.publishedDefinition.set(definition));
  }

  protected publish(wf: WorkflowDetail): void {
    this.run(this.workflows.publish(wf.id), (detail) => this.published.emit(detail));
  }

  protected discard(wf: WorkflowDetail): void {
    this.run(this.workflows.discardDraft(wf.id), (detail) => this.discarded.emit(detail));
  }

  private run(request: ReturnType<WorkflowsApiService['publish']>, done: (detail: WorkflowDetail) => void): void {
    this.busy.set(true);
    this.error.set(null);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (detail) => {
        this.busy.set(false);
        done(detail);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        const errors = validationErrorsOf(e);
        this.error.set(errors.length ? $localize`:@@publish.invalid:Не опубліковано: ${errors.map((x) => x.message).join('; ')}:errors:` : apiErrorMessage(e));
      },
    });
  }
}
