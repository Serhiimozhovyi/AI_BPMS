import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Observable } from 'rxjs';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import {
  CaseTrend,
  EVALUATION_CHECKS,
  EvaluationCaseInput,
  EvaluationCheck,
  EvaluationDetail,
  EvaluationRun,
  EvaluationsApiService,
  caseTrends,
  jsonText,
  parseJsonField,
} from './evaluations-api.service';
import { BpButtonDirective } from '../shared/button.directive';

/** Кейс у формі: JSON-поля — текстом, налаштування перевірки — окремими полями. */
interface CaseDraft {
  name: string;
  input: string;
  expected: string;
  check: EvaluationCheck;
  path: string;
  expression: string;
  credentialId: string;
  model: string;
  criteria: string;
}

const CHECK_LABELS: Record<EvaluationCheck, string> = {
  exact: $localize`:@@evaluations.check.exact:Точний збіг`,
  jsonPath: $localize`:@@evaluations.check.jsonPath:Поле за шляхом`,
  expression: $localize`:@@evaluations.check.expression:Вираз`,
  llm: $localize`:@@evaluations.check.llm:Модель-суддя`,
};

function emptyDraft(): CaseDraft {
  return { name: '', input: '{}', expected: '', check: 'exact', path: '', expression: '', credentialId: '', model: '', criteria: '' };
}

/** Кейс з Api → чернетка форми; вхід — `json` першого item'а, якщо item один. */
export function toDraft(c: EvaluationCaseInput): CaseDraft {
  const config = c.config ?? {};
  const text = (key: string) => (typeof config[key] === 'string' ? (config[key] as string) : '');
  return {
    name: c.name ?? '',
    input: c.input.length === 1 ? jsonText(c.input[0].json) : jsonText(c.input),
    expected: jsonText(c.expected),
    check: c.check,
    path: text('path'),
    expression: text('expression'),
    credentialId: text('credentialId'),
    model: text('model'),
    criteria: text('criteria'),
  };
}

/** Чернетка → кейс для Api: об'єкт входу стає одним item'ом, масив — items як є. */
export function fromDraft(d: CaseDraft, index: number): EvaluationCaseInput {
  const label = d.name.trim() || $localize`:@@evaluations.caseN:Кейс ${index + 1}:n:`;
  const input = parseJsonField(d.input, `${label}: ${$localize`:@@evaluations.input:Вхід`}`) ?? {};
  const items = Array.isArray(input) ? (input as EvaluationCaseInput['input']) : [{ json: input as Record<string, unknown> }];
  const config: Record<string, unknown> = {};
  if (d.check === 'jsonPath') config['path'] = d.path.trim();
  if (d.check === 'expression') config['expression'] = d.expression.trim();
  if (d.check === 'llm') {
    config['credentialId'] = d.credentialId.trim();
    config['model'] = d.model.trim();
    if (d.criteria.trim()) config['criteria'] = d.criteria.trim();
  }
  const expected = parseJsonField(d.expected, `${label}: ${$localize`:@@evaluations.expected:Очікуване`}`);
  return { name: d.name.trim() || null, input: items, expected, check: d.check, config };
}

/**
 * Вкладка «Оцінки» вузла ai.llm/ai.agent (F6-17): кейси «вхід → очікуване», прогін на збереженій поточній версії
 * (як «Тестувати крок» з items) і результати з порівнянням з попереднім прогоном. Змінює й запускає editor.
 */
@Component({
  selector: 'app-node-evaluations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, MatProgressBarModule],
  template: `
    <section class="mt-4 flex flex-col gap-3 border-t border-bp-line pt-4" data-testid="evaluations">
      <div class="flex items-center gap-2">
        <h3 class="m-0 font-semibold" i18n="@@evaluations.title">Оцінки</h3>
        @if (detail(); as d) {
          <span class="text-sm opacity-70" data-testid="evaluation-name">{{ d.summary.name }}</span>
        }
      </div>
      <p class="m-0 text-sm opacity-70" i18n="@@evaluations.hint">
        Набір кейсів «вхід → очікуване» для цього вузла. Прогін виконує вузол на кожному кейсі на збереженій версії сценарію.
      </p>
      @if (busy()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="m-0 text-sm text-red-700" data-testid="evaluations-error">{{ error() }}</p>
      }

      @if (!detail() && !busy()) {
        @if (!readonly()) {
          <button mat-stroked-button type="button" class="self-start" data-testid="evaluation-create" (click)="create()" i18n="@@evaluations.create">
            Створити оцінку
          </button>
        } @else {
          <p class="m-0 text-sm opacity-70" i18n="@@evaluations.none">Оцінок для вузла ще немає.</p>
        }
      }

      @if (detail()) {
        <ol class="m-0 flex list-none flex-col gap-3 p-0" data-testid="evaluation-cases">
          @for (c of drafts(); track $index; let i = $index) {
            <li class="flex flex-col gap-2 rounded-lg border border-bp-line p-3" [attr.data-case]="i">
              <div class="flex items-center gap-2">
                <input
                  class="min-w-0 flex-1 rounded border border-bp-line px-2 py-1 text-sm"
                  data-testid="case-name"
                  [value]="c.name"
                  [placeholder]="caseLabel(i)"
                  [readOnly]="readonly()"
                  [attr.aria-label]="nameLabel"
                  (input)="edit(i, 'name', $event)"
                />
                <select
                  class="rounded border border-bp-line px-2 py-1 text-sm"
                  data-testid="case-check"
                  [value]="c.check"
                  [disabled]="readonly()"
                  [attr.aria-label]="checkLabel"
                  (change)="edit(i, 'check', $event)"
                >
                  @for (check of checks; track check) {
                    <option [value]="check">{{ checkLabels[check] }}</option>
                  }
                </select>
                @if (!readonly()) {
                  <button mat-button type="button" data-testid="case-remove" (click)="remove(i)" i18n="@@evaluations.remove" bpVariant="danger">Прибрати</button>
                }
              </div>
              <div class="grid gap-2 md:grid-cols-2">
                <label class="flex flex-col gap-1 text-xs">
                  <span i18n="@@evaluations.inputLabel">Вхід (json item'а або масив items)</span>
                  <textarea class="min-h-16 rounded border border-bp-line p-1 font-mono text-xs" data-testid="case-input" [value]="c.input" [readOnly]="readonly()" (input)="edit(i, 'input', $event)"></textarea>
                </label>
                <label class="flex flex-col gap-1 text-xs">
                  <span i18n="@@evaluations.expectedLabel">Очікуване (JSON)</span>
                  <textarea class="min-h-16 rounded border border-bp-line p-1 font-mono text-xs" data-testid="case-expected" [value]="c.expected" [readOnly]="readonly()" (input)="edit(i, 'expected', $event)"></textarea>
                </label>
              </div>
              @switch (c.check) {
                @case ('jsonPath') {
                  <input class="rounded border border-bp-line px-2 py-1 font-mono text-xs" data-testid="case-path" placeholder="text" [value]="c.path" [readOnly]="readonly()" [attr.aria-label]="pathLabel" (input)="edit(i, 'path', $event)" />
                }
                @case ('expression') {
                  <input class="rounded border border-bp-line px-2 py-1 font-mono text-xs" data-testid="case-expression" placeholder="$json.text === $node['expected'].json.text" [value]="c.expression" [readOnly]="readonly()" [attr.aria-label]="expressionLabel" (input)="edit(i, 'expression', $event)" />
                }
                @case ('llm') {
                  <div class="grid gap-2 md:grid-cols-2">
                    <input class="rounded border border-bp-line px-2 py-1 text-xs" data-testid="case-credential" [value]="c.credentialId" [readOnly]="readonly()" [attr.aria-label]="credentialLabel" [placeholder]="credentialLabel" (input)="edit(i, 'credentialId', $event)" />
                    <input class="rounded border border-bp-line px-2 py-1 text-xs" data-testid="case-model" [value]="c.model" [readOnly]="readonly()" [attr.aria-label]="modelLabel" [placeholder]="modelLabel" (input)="edit(i, 'model', $event)" />
                  </div>
                  <textarea class="rounded border border-bp-line p-1 text-xs" data-testid="case-criteria" [value]="c.criteria" [readOnly]="readonly()" [attr.aria-label]="criteriaLabel" [placeholder]="criteriaLabel" (input)="edit(i, 'criteria', $event)"></textarea>
                }
              }
            </li>
          }
        </ol>

        @if (!readonly()) {
          <div class="flex flex-wrap gap-2">
            <button mat-button type="button" data-testid="case-add" (click)="add()" i18n="@@evaluations.add">Додати кейс</button>
            <button mat-stroked-button type="button" data-testid="evaluation-save" [disabled]="busy() || !dirty()" (click)="save()" i18n="@@evaluations.save">Зберегти кейси</button>
            <button mat-flat-button type="button" data-testid="evaluation-run" [disabled]="busy() || dirty() || drafts().length === 0" (click)="run()" i18n="@@evaluations.run">Запустити оцінку</button>
          </div>
        }

        @if (latest(); as r) {
          <div data-testid="evaluation-result">
            <p class="m-0 mb-2 text-sm">
              <strong data-testid="evaluation-score">{{ r.passed }}/{{ r.total }}</strong>
              <span class="opacity-70" i18n="@@evaluations.onVersion"> на версії {{ r.version }}</span>
              @if (previous(); as p) {
                <span class="opacity-70" data-testid="evaluation-previous" i18n="@@evaluations.previous"> · попередній прогін: {{ p.passed }}/{{ p.total }} (версія {{ p.version }})</span>
              }
            </p>
            <div class="overflow-x-auto">
              <table class="w-full text-left text-xs" data-testid="evaluation-results">
                <thead>
                  <tr>
                    <th class="p-1" i18n="@@evaluations.col.case">Кейс</th>
                    <th class="p-1" i18n="@@evaluations.col.result">Результат</th>
                    <th class="p-1" i18n="@@evaluations.col.actual">Фактичне</th>
                    <th class="p-1" i18n="@@evaluations.col.reason">Чому</th>
                    <th class="p-1" i18n="@@evaluations.col.duration">Час, мс</th>
                    <th class="p-1" i18n="@@evaluations.col.tokens">Токени</th>
                  </tr>
                </thead>
                <tbody>
                  @for (res of r.results; track res.caseId) {
                    <tr class="border-t border-bp-line align-top" [attr.data-result]="res.passed ? 'pass' : 'fail'">
                      <td class="p-1">{{ res.name }}</td>
                      <td class="p-1" [class.text-green-700]="res.passed" [class.text-red-700]="!res.passed">
                        {{ res.passed ? '✓' : '✗' }}
                        @if (trends()[res.caseId]; as t) {
                          @if (t === 'fixed' || t === 'broken') {
                            <span data-testid="case-trend">{{ trendLabels[t] }}</span>
                          }
                        }
                      </td>
                      <td class="max-w-48 p-1 font-mono break-words">{{ show(res.actual) }}</td>
                      <td class="p-1">{{ res.error ?? res.reason ?? '' }}</td>
                      <td class="p-1">{{ res.durationMs }}</td>
                      <td class="p-1">{{ res.tokens ?? '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>
        }
      }
    </section>
  `,
})
export class NodeEvaluationsComponent {
  private readonly api = inject(EvaluationsApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workflowId = input.required<string>();
  readonly nodeId = input.required<string>();
  readonly readonly = input(false);

  readonly detail = signal<EvaluationDetail | null>(null);
  readonly drafts = signal<CaseDraft[]>([]);
  readonly dirty = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly latest = computed<EvaluationRun | null>(() => this.detail()?.runs[0] ?? null);
  readonly previous = computed<EvaluationRun | null>(() => this.detail()?.runs[1] ?? null);
  readonly trends = computed<Record<string, CaseTrend>>(() => {
    const latest = this.latest();
    return latest ? caseTrends(latest, this.previous()) : {};
  });

  readonly checks = EVALUATION_CHECKS;
  readonly checkLabels = CHECK_LABELS;
  readonly trendLabels: Record<'fixed' | 'broken', string> = {
    fixed: $localize`:@@evaluations.trend.fixed:(виправлено)`,
    broken: $localize`:@@evaluations.trend.broken:(зламалось)`,
  };
  readonly nameLabel = $localize`:@@evaluations.nameLabel:Назва кейсу`;
  readonly checkLabel = $localize`:@@evaluations.checkLabel:Спосіб перевірки`;
  readonly pathLabel = $localize`:@@evaluations.pathLabel:Шлях до поля, напр. text або items[0].name`;
  readonly expressionLabel = $localize`:@@evaluations.expressionLabel:Вираз, що дає true`;
  readonly credentialLabel = $localize`:@@evaluations.credentialLabel:Id облікових даних моделі-судді`;
  readonly modelLabel = $localize`:@@evaluations.modelLabel:Модель судді`;
  readonly criteriaLabel = $localize`:@@evaluations.criteriaLabel:Критерії для судді`;

  constructor() {
    effect(() => {
      const workflowId = this.workflowId();
      const nodeId = this.nodeId();
      untracked(() => this.load(workflowId, nodeId));
    });
  }

  caseLabel(index: number): string {
    return $localize`:@@evaluations.caseN:Кейс ${index + 1}:n:`;
  }

  show(value: unknown): string {
    if (value === undefined || value === null) return '—';
    const text = JSON.stringify(value);
    return text.length > 200 ? `${text.slice(0, 200)}…` : text;
  }

  edit(index: number, field: keyof CaseDraft, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
    this.drafts.update((list) => list.map((d, i) => (i === index ? { ...d, [field]: value } : d)));
    this.dirty.set(true);
  }

  add(): void {
    this.drafts.update((list) => [...list, emptyDraft()]);
    this.dirty.set(true);
  }

  remove(index: number): void {
    this.drafts.update((list) => list.filter((_, i) => i !== index));
    this.dirty.set(true);
  }

  create(): void {
    this.call(this.api.create(this.workflowId(), this.nodeId(), null, []), (d) => {
      this.apply(d);
      this.add();
    });
  }

  save(): void {
    const current = this.detail();
    if (!current) return;
    let cases: EvaluationCaseInput[];
    try {
      cases = this.drafts().map(fromDraft);
    } catch (e) {
      this.error.set((e as Error).message);
      return;
    }
    this.call(this.api.update(current.summary.id, null, cases), (d) => this.apply(d));
  }

  run(): void {
    const current = this.detail();
    if (!current) return;
    this.call(this.api.run(current.summary.id), (r) => {
      this.detail.update((d) => (d ? { ...d, runs: [r, ...d.runs] } : d));
    });
  }

  private load(workflowId: string, nodeId: string): void {
    this.detail.set(null);
    this.drafts.set([]);
    this.dirty.set(false);
    this.call(this.api.list(workflowId, nodeId), (list) => {
      if (list.length > 0) {
        this.call(this.api.get(list[0].id), (d) => this.apply(d));
      }
    });
  }

  private apply(detail: EvaluationDetail): void {
    this.detail.set(detail);
    this.drafts.set(detail.cases.map(toDraft));
    this.dirty.set(false);
  }

  private call<T>(request: Observable<T>, next: (value: T) => void): void {
    this.busy.set(true);
    this.error.set(null);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (value) => {
        this.busy.set(false);
        next(value);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }
}
