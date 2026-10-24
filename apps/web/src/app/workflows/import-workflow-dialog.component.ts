import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { ConnectorsApiService, apiErrorMessage } from '../connectors/connectors-api.service';
import { CredentialsApiService } from '../credentials/credentials-api.service';
import {
  ImportMapping,
  ReferenceKind,
  UnmappedReference,
  WorkflowDefinition,
  WorkflowImportResult,
  WorkflowManifest,
  WorkflowsApiService,
  unmappedOf,
  validationErrorsOf,
} from './workflows-api.service';

export interface ImportWorkflowDialogData {
  /** Група, у яку покласти новий сценарій. */
  groupId?: string | null;
}

/** Кандидат-відповідник у цьому середовищі. */
export interface MappingCandidate {
  id: string;
  name: string;
  /** Транспорт конектора або тип credential'а — для відбору за маніфестом. */
  type: string | null;
}

/** Файл експорту: `{ definition, manifest }` або саме визначення. */
export interface ParsedExport {
  definition: WorkflowDefinition;
  manifest: WorkflowManifest | null;
}

/** Розбирає вміст файлу експорту; `null` — не JSON або без `nodes`. */
export function parseExport(text: string): ParsedExport | null {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return null;
  }
  if (!value || typeof value !== 'object') return null;
  const obj = value as { definition?: unknown; manifest?: unknown; nodes?: unknown };
  const definition = (obj.definition ?? obj) as WorkflowDefinition;
  if (!definition || typeof definition !== 'object' || !Array.isArray(definition.nodes)) return null;
  return { definition, manifest: obj.definition ? ((obj.manifest as WorkflowManifest | undefined) ?? null) : null };
}

/** Ключ вибору в {@link ImportWorkflowDialog.choices}. */
export const choiceKey = (u: Pick<UnmappedReference, 'kind' | 'ref'>) => `${u.kind}:${u.ref}`;

/**
 * Діалог імпорту сценарію з файлу експорту (F6-09): Api сам знаходить конектори, credentials і сценарій помилок за
 * назвою й типом; для незнайдених діалог показує вибір відповідника і повторює імпорт з `mapping`. Закривається
 * {@link WorkflowImportResult} або `undefined`.
 */
@Component({
  selector: 'app-import-workflow-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title i18n="@@import.title">Імпорт сценарію</h2>
    <mat-dialog-content>
      @if (result(); as r) {
        <div data-testid="import-done">
          <p class="mb-2" i18n="@@import.done">Сценарій «{{ r.workflow.name }}» створено (неактивний).</p>
          <p class="text-amber-700" data-testid="import-missing-vars" i18n="@@import.missingVars">
            Немає змінних: {{ r.missingVariables.join(', ') }}. Додайте їх у Налаштуваннях, інакше $vars дасть undefined.
          </p>
        </div>
      } @else {
        <div class="flex flex-col gap-3 pt-2">
          <p class="opacity-70" i18n="@@import.hint">
            Файл з «Експортувати» або з <code>openbp export</code>. Конектори й облікові дані знаходяться за назвою і типом.
          </p>
          <input type="file" accept="application/json,.json" data-testid="import-file" (change)="readFile($event)" />
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label i18n="@@import.json">Або вставте JSON</mat-label>
            <textarea matInput rows="6" class="font-mono" data-testid="import-json" [value]="text()" (input)="setText($any($event.target).value)"></textarea>
          </mat-form-field>
          @if (parseError()) {
            <p class="text-red-700" data-testid="import-parse-error" i18n="@@import.parseError">Це не файл експорту сценарію.</p>
          }

          @if (unmapped().length > 0) {
            <div data-testid="import-mapping">
              <p class="mb-2 font-semibold" i18n="@@import.mapping">Виберіть відповідники в цьому середовищі</p>
              @for (u of unmapped(); track key(u)) {
                <mat-form-field appearance="outline" class="w-full" subscriptSizing="dynamic">
                  <mat-label>{{ kindLabel(u.kind) }} «{{ u.name ?? u.ref }}»@if (u.type) { ({{ u.type }})}</mat-label>
                  <mat-select [attr.data-unmapped]="key(u)" [value]="choices()[key(u)] ?? null" (selectionChange)="choose(u, $event.value)">
                    @for (c of candidatesFor(u); track c.id) {
                      <mat-option [value]="c.id">{{ c.name }}</mat-option>
                    }
                  </mat-select>
                  @if (candidatesFor(u).length === 0) {
                    <mat-hint data-testid="import-no-candidates" i18n="@@import.noCandidates">Немає відповідного — створіть його й повторіть імпорт</mat-hint>
                  } @else if (u.reason === 'ambiguous') {
                    <mat-hint i18n="@@import.ambiguous">Кілька з такою назвою — виберіть один</mat-hint>
                  }
                </mat-form-field>
              }
            </div>
          }

          @if (error()) {
            <p role="alert" class="text-red-700" data-testid="import-error">{{ error() }}</p>
          }
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (result(); as r) {
        <button mat-flat-button type="button" data-testid="import-close" (click)="ref.close(r)" i18n="@@import.closeDone">Готово</button>
      } @else {
        <button mat-button type="button" (click)="ref.close()" i18n="@@import.cancel">Скасувати</button>
        <button mat-flat-button type="button" data-testid="import-submit" [disabled]="!canSubmit()" (click)="submit()" i18n="@@import.submit">
          Імпортувати
        </button>
      }
    </mat-dialog-actions>
  `,
})
export class ImportWorkflowDialog {
  readonly ref = inject<MatDialogRef<ImportWorkflowDialog, WorkflowImportResult>>(MatDialogRef);
  private readonly data = inject<ImportWorkflowDialogData | null>(MAT_DIALOG_DATA, { optional: true });
  private readonly workflows = inject(WorkflowsApiService);
  private readonly connectors = inject(ConnectorsApiService);
  private readonly credentials = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly text = signal('');
  readonly parsed = computed(() => (this.text().trim() ? parseExport(this.text()) : null));
  readonly parseError = computed(() => this.text().trim() !== '' && this.parsed() === null);
  readonly unmapped = signal<UnmappedReference[]>([]);
  readonly candidates = signal<Record<ReferenceKind, MappingCandidate[]>>({ connector: [], credential: [], workflow: [] });
  readonly choices = signal<Record<string, string>>({});
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly result = signal<WorkflowImportResult | null>(null);

  readonly canSubmit = computed(
    () => !this.busy() && this.parsed() !== null && this.unmapped().every((u) => !!this.choices()[choiceKey(u)]),
  );

  readonly key = choiceKey;

  kindLabel(kind: ReferenceKind): string {
    switch (kind) {
      case 'connector':
        return $localize`:@@import.kind.connector:Конектор`;
      case 'credential':
        return $localize`:@@import.kind.credential:Облікові дані`;
      default:
        return $localize`:@@import.kind.workflow:Сценарій помилок`;
    }
  }

  /** Кандидати того самого виду; для конектора — того ж транспорту, для credential'а — того ж типу. */
  candidatesFor(u: UnmappedReference): MappingCandidate[] {
    return this.candidates()[u.kind].filter((c) => !u.type || !c.type || c.type === u.type);
  }

  setText(value: string): void {
    this.text.set(value);
    this.unmapped.set([]);
    this.choices.set({});
    this.error.set(null);
  }

  readFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    void file.text().then((content) => this.setText(content));
  }

  choose(u: UnmappedReference, id: string): void {
    this.choices.update((c) => ({ ...c, [choiceKey(u)]: id }));
  }

  submit(): void {
    const parsed = this.parsed();
    if (!parsed || !this.canSubmit()) return;
    this.busy.set(true);
    this.error.set(null);
    this.workflows
      .importWorkflow({ definition: parsed.definition, manifest: parsed.manifest, mapping: this.mapping(), groupId: this.data?.groupId ?? null })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.busy.set(false);
          if (result.missingVariables.length > 0) {
            this.result.set(result);
          } else {
            this.ref.close(result);
          }
        },
        error: (e: unknown) => {
          this.busy.set(false);
          const unmapped = unmappedOf(e);
          if (unmapped) {
            this.showMapping(unmapped);
            return;
          }
          const errors = validationErrorsOf(e);
          this.error.set(errors.length > 0 ? errors.map((x) => x.message).join(' ') : apiErrorMessage(e));
        },
      });
  }

  private mapping(): ImportMapping {
    const mapping: Required<Record<keyof ImportMapping, Record<string, string>>> = { connectors: {}, credentials: {}, workflows: {} };
    for (const u of this.unmapped()) {
      const chosen = this.choices()[choiceKey(u)];
      if (chosen) mapping[`${u.kind}s` as keyof ImportMapping][u.ref] = chosen;
    }
    return mapping;
  }

  private showMapping(unmapped: UnmappedReference[]): void {
    this.unmapped.set(unmapped);
    // Невдалий явний вибір (invalid) скидається, решта лишається.
    this.choices.update((c) => Object.fromEntries(Object.entries(c).filter(([k]) => !unmapped.some((u) => u.reason === 'invalid' && choiceKey(u) === k))));
    const kinds = new Set(unmapped.map((u) => u.kind));
    forkJoin({
      connector: kinds.has('connector')
        ? this.connectors.list().pipe(map((p) => p.items.map((c) => ({ id: c.id, name: c.name, type: c.transport as string | null }))))
        : of([]),
      credential: kinds.has('credential') ? this.credentials.options().pipe(map((list) => list.map((c) => ({ id: c.id, name: c.name, type: c.type as string | null })))) : of([]),
      workflow: kinds.has('workflow') ? this.workflows.list().pipe(map((p) => p.items.map((w) => ({ id: w.id, name: w.name, type: null })))) : of([]),
    })
      .pipe(
        catchError((e: unknown) => {
          this.error.set(apiErrorMessage(e));
          return of({ connector: [], credential: [], workflow: [] });
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((candidates) => this.candidates.set(candidates));
  }
}
