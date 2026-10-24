import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { RouterLink } from '@angular/router';
import { forkJoin, map, of, switchMap } from 'rxjs';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { VersionDiff, diffDefinitions, isEmptyDiff } from './version-diff';
import { WorkflowDefinition, WorkflowVersionSummary, WorkflowsApiService } from './workflows-api.service';

/** Дані діалогу. */
export interface VersionHistoryDialogData {
  workflowId: string;
  /** Поточна версія — її не відновлюють. */
  currentVersion: number;
  readonly: boolean;
}

/** Результат діалогу: визначення версії для відновлення. */
export interface VersionRestore {
  version: number;
  definition: WorkflowDefinition;
}

/** Скільки останніх версій показувати з diff (визначення завантажуються для кожної). */
export const HISTORY_LIMIT = 30;

interface Row {
  summary: WorkflowVersionSummary;
  definition: WorkflowDefinition;
  /** Diff до попередньої версії; `null` — перша версія. */
  diff: VersionDiff | null;
}

/**
 * «Історія версій» (F3-61): версії з датою, автором і коротким diff до попередньої; «Переглянути» відкриває канвас версії
 * лише для читання, «Відновити» повертає визначення редактору — він збереже його як нову версію (історія не переписується).
 */
@Component({
  selector: 'app-version-history-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatDialogModule, RouterLink],
  template: `
    <h2 mat-dialog-title i18n="@@versions.title">Історія версій</h2>
    <mat-dialog-content>
      @if (error()) {
        <p role="alert" class="text-red-700" data-testid="versions-error">{{ error() }}</p>
      }
      <ol class="m-0 flex list-none flex-col gap-2 p-0" data-testid="versions">
        @for (row of rows(); track row.summary.id) {
          <li class="rounded border border-[var(--ac-line)] p-3" data-testid="version" [attr.data-version]="row.summary.version">
            <div class="flex flex-wrap items-center gap-2">
              <strong i18n="@@versions.number">Версія {{ row.summary.version }}</strong>
              @if (row.summary.version === data.currentVersion) {
                <span class="rounded bg-[var(--ac-button)] px-1.5 text-xs" i18n="@@versions.current">поточна</span>
              }
              <span class="text-sm text-[var(--ac-cool-grey-4)]">{{ row.summary.createdAt | date: 'dd.MM.yyyy HH:mm' }} · {{ row.summary.createdByEmail ?? system }}</span>
              <span class="flex-1"></span>
              <a mat-button data-testid="version-view" [routerLink]="['/workflows', data.workflowId]" [queryParams]="{ version: row.summary.version }" (click)="ref.close()" i18n="@@versions.view">Переглянути</a>
              @if (!data.readonly && row.summary.version !== data.currentVersion) {
                <button mat-stroked-button type="button" data-testid="version-restore" (click)="restore(row)" i18n="@@versions.restore">Відновити</button>
              }
            </div>
            <p class="m-0 mt-1 text-sm" data-testid="version-diff">{{ describe(row.diff) }}</p>
          </li>
        } @empty {
          @if (!loading()) {
            <li class="opacity-70" i18n="@@versions.empty">Версій немає.</li>
          }
        }
      </ol>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close i18n="@@versions.close">Закрити</button>
    </mat-dialog-actions>
  `,
})
export class VersionHistoryDialogComponent implements OnInit {
  protected readonly data = inject<VersionHistoryDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<VersionHistoryDialogComponent, VersionRestore>>(MatDialogRef);
  private readonly workflows = inject(WorkflowsApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly rows = signal<Row[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly system = $localize`:@@versions.system:система`;

  ngOnInit(): void {
    this.workflows
      .versions(this.data.workflowId)
      .pipe(
        // Ще одна версія понад ліміт — щоб найстаріша показана мала diff.
        switchMap((list) => {
          const shown = list.slice(0, HISTORY_LIMIT + 1);
          return shown.length === 0
            ? of([])
            : forkJoin(shown.map((v) => this.workflows.version(this.data.workflowId, v.version))).pipe(
                map((definitions) => shown.map((summary, i) => ({ summary, definition: definitions[i] }))),
              );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (loaded) => {
          this.rows.set(
            loaded.slice(0, HISTORY_LIMIT).map((v, i) => ({
              ...v,
              diff: loaded[i + 1] ? diffDefinitions(loaded[i + 1].definition, v.definition) : null,
            })),
          );
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  protected restore(row: Row): void {
    this.ref.close({ version: row.summary.version, definition: row.definition });
  }

  protected describe(diff: VersionDiff | null): string {
    if (!diff) return $localize`:@@versions.diff.first:Перша версія.`;
    if (isEmptyDiff(diff)) return $localize`:@@versions.diff.none:Без змін у вузлах і з'єднаннях.`;
    const parts: string[] = [];
    if (diff.addedNodes.length) parts.push($localize`:@@versions.diff.added:додано: ${diff.addedNodes.join(', ')}:names:`);
    if (diff.removedNodes.length) parts.push($localize`:@@versions.diff.removed:видалено: ${diff.removedNodes.join(', ')}:names:`);
    if (diff.changedNodes.length) parts.push($localize`:@@versions.diff.changed:змінено: ${diff.changedNodes.join(', ')}:names:`);
    if (diff.movedNodes.length) parts.push($localize`:@@versions.diff.moved:переміщено: ${diff.movedNodes.length}:count:`);
    const edges = diff.addedEdges.length + diff.removedEdges.length;
    if (edges) parts.push($localize`:@@versions.diff.edges:з'єднань +${diff.addedEdges.length}:added: / −${diff.removedEdges.length}:removed:`);
    if (diff.otherChanged) parts.push($localize`:@@versions.diff.other:налаштування чи дані`);
    return parts.join('; ');
  }
}
