import { HttpErrorResponse } from '@angular/common/http';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injectable,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { Observable, Subject, catchError, debounceTime, distinctUntilChanged, map, of, switchMap } from 'rxjs';
import { ExpressionPreview, WorkflowsApiService } from '../workflows/workflows-api.service';
import { ExpressionContext, suggestExpression } from './expression-suggest';
import { EXPRESSION_FUNCTIONS, ExpressionFunction, filterFunctions, formatPreview, hasExpression, insertSnippet, previewType } from './expression-tools';
import { CodeEditorHandle, MONACO_LOADER } from './monaco-loader';

/** Що відкривається в редакторі виразів. */
export interface ExpressionEditorData {
  /** Поточний текст поля. */
  value: string;
  /** Назва поля — у заголовку. */
  label: string;
  /** Сценарій для попереднього перегляду; `null` — результату не буде. */
  workflowId: string | null;
  /** Вузол, у параметрі якого вираз. */
  nodeId: string | null;
  /** Вхідний item і виходи вузлів з редактора (як для підказок, F2-19). */
  context: ExpressionContext;
  /** Лише перегляд: без «Застосувати». */
  readonly?: boolean;
}

/** Затримка попереднього перегляду після введення. */
export const PREVIEW_DEBOUNCE_MS = 300;

type PreviewState = { status: 'idle' | 'loading' } | { status: 'ready'; value: unknown } | { status: 'error'; message: string };

/**
 * Повноекранний редактор виразу (F3-56): ліворуч Monaco з підсвіткою `{{ }}` і підказками F2-19, праворуч — результат на
 * першому вхідному item (через Api, ті самі ліміти, що й у виконанні) і каталог функцій зі вставкою кліком.
 * Закривається новим текстом поля або `undefined` при скасуванні.
 */
@Component({
  selector: 'app-expression-editor-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>
      <span i18n="@@expreditor.title">Вираз</span>
      <span class="expr__label"> · {{ data.label }}</span>
    </h2>
    <mat-dialog-content class="expr" data-testid="expression-editor">
      <div class="expr__source">
        @if (fallback()) {
          <textarea
            #fallbackEl
            class="expr__textarea"
            data-testid="expr-editor-text"
            spellcheck="false"
            [readOnly]="!!data.readonly"
            [value]="value()"
            (input)="typed($any($event.target).value)"
          ></textarea>
        } @else {
          <div #host class="expr__monaco" data-testid="expr-editor" [attr.data-status]="editorReady() ? 'ready' : 'loading'"></div>
        }
        <p class="expr__hint">{{ hint }}</p>
      </div>

      <aside class="expr__side">
        <section class="expr__result" aria-live="polite">
          <h3 class="expr__heading">
            <span i18n="@@expreditor.result">Результат</span>
            @if (resultType(); as type) {
              <span class="expr__type" data-testid="expr-result-type">{{ type }}</span>
            }
          </h3>
          <p class="expr__note" data-testid="expr-input-note">{{ inputNote }}</p>
          @switch (preview().status) {
            @case ('loading') {
              <p class="expr__muted" i18n="@@expreditor.loading">Обчислення…</p>
            }
            @case ('error') {
              <pre class="expr__output expr__output--error" data-testid="expr-result-error">{{ errorText() }}</pre>
            }
            @case ('ready') {
              <pre class="expr__output" data-testid="expr-result">{{ resultText() }}</pre>
            }
            @default {
              <p class="expr__muted" data-testid="expr-result-idle">{{ idleText }}</p>
            }
          }
        </section>

        <section class="expr__catalog">
          <h3 class="expr__heading" i18n="@@expreditor.functions">Функції</h3>
          <input
            class="expr__search"
            type="search"
            data-testid="expr-fn-search"
            i18n-placeholder="@@expreditor.functions.search"
            placeholder="Пошук функції"
            [value]="query()"
            (input)="query.set($any($event.target).value)"
          />
          @for (group of functions(); track group.id) {
            <h4 class="expr__group">{{ group.label }}</h4>
            @for (fn of group.functions; track fn.label) {
              <button
                type="button"
                class="expr__fn"
                data-testid="expr-fn"
                [attr.data-insert]="fn.insert"
                [disabled]="!!data.readonly"
                [title]="fn.example + ' → ' + fn.result"
                (click)="insert(fn)"
              >
                <code class="expr__fn-label">{{ fn.label }}</code>
                <span class="expr__fn-description">{{ fn.description }}</span>
                <span class="expr__fn-example"><code>{{ fn.example }}</code> → <code>{{ fn.result }}</code></span>
              </button>
            }
          } @empty {
            <p class="expr__muted" i18n="@@expreditor.functions.empty">Нічого не знайдено</p>
          }
        </section>
      </aside>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" data-testid="expr-cancel" (click)="ref.close()" i18n="@@expreditor.cancel">Скасувати</button>
      @if (!data.readonly) {
        <button mat-flat-button type="button" data-testid="expr-apply" (click)="ref.close(value())" i18n="@@expreditor.apply">Застосувати</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    .expr {
      display: grid;
      grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
      gap: 16px;
      height: min(70vh, 640px);
      max-height: none;
    }

    @media (max-width: 720px) {
      .expr {
        grid-template-columns: minmax(0, 1fr);
        height: auto;
      }
    }

    .expr__label {
      color: var(--ac-cool-grey-4);
      font-weight: 400;
    }

    .expr__source {
      display: flex;
      flex-direction: column;
      gap: 6px;
      min-height: 240px;
    }

    .expr__monaco,
    .expr__textarea {
      flex: 1;
      min-height: 0;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--ac-radius-sm);
      overflow: hidden;
    }

    .expr__textarea {
      padding: 8px;
      font: 13px/18px monospace;
      resize: none;
    }

    .expr__hint,
    .expr__note,
    .expr__muted {
      margin: 0;
      font-size: 12px;
      color: var(--ac-cool-grey-4);
    }

    .expr__side {
      display: flex;
      flex-direction: column;
      gap: 12px;
      min-height: 0;
      overflow-y: auto;
    }

    .expr__heading {
      display: flex;
      align-items: center;
      gap: 8px;
      margin: 0 0 4px;
      font: 700 11px/16px Ubuntu, sans-serif;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--ac-vivid);
    }

    .expr__type {
      padding: 0 6px;
      border-radius: var(--ac-radius);
      background: var(--ac-button);
      color: var(--ac-kit);
      font-weight: 500;
      text-transform: none;
    }

    .expr__output {
      margin: 4px 0 0;
      padding: 8px;
      max-height: 200px;
      overflow: auto;
      border-radius: var(--ac-radius-sm);
      background: var(--ac-cool-grey-2);
      font: 12px/17px monospace;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .expr__output--error {
      color: var(--mat-sys-error);
    }

    .expr__search {
      width: 100%;
      box-sizing: border-box;
      margin-bottom: 4px;
      padding: 6px 10px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--ac-radius-sm);
      background: transparent;
      color: inherit;
      font: inherit;
    }

    .expr__search:focus {
      outline: none;
      border-color: var(--ac-kit);
    }

    .expr__group {
      margin: 8px 0 2px;
      font-size: 12px;
      font-weight: 500;
    }

    .expr__fn {
      display: flex;
      flex-direction: column;
      gap: 1px;
      width: 100%;
      padding: 6px 8px;
      border: 0;
      border-radius: 6px;
      background: none;
      color: inherit;
      text-align: left;
      font: inherit;
      cursor: pointer;
    }

    .expr__fn:hover:not(:disabled),
    .expr__fn:focus-visible {
      background: var(--mat-sys-surface-container-high);
    }

    .expr__fn:disabled {
      cursor: default;
    }

    .expr__fn-label {
      font-size: 13px;
    }

    .expr__fn-description,
    .expr__fn-example {
      font-size: 11px;
      opacity: 0.7;
    }
  `,
})
export class ExpressionEditorDialogComponent implements AfterViewInit {
  protected readonly data = inject<ExpressionEditorData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<ExpressionEditorDialogComponent, string>>(MatDialogRef);
  private readonly api = inject(WorkflowsApiService);
  private readonly loadEditor = inject(MONACO_LOADER);
  private readonly destroyRef = inject(DestroyRef);

  private readonly host = viewChild<ElementRef<HTMLElement>>('host');
  private readonly fallbackEl = viewChild<ElementRef<HTMLTextAreaElement>>('fallbackEl');
  private editor: CodeEditorHandle | null = null;

  protected readonly value = signal(this.data.value);
  protected readonly fallback = signal(false);
  protected readonly editorReady = signal(false);
  protected readonly query = signal('');
  protected readonly preview = signal<PreviewState>({ status: 'idle' });
  private readonly changes = new Subject<string>();

  protected readonly functions = computed(() => filterFunctions(EXPRESSION_FUNCTIONS, this.query()));
  protected readonly resultText = computed(() => {
    const p = this.preview();
    return p.status === 'ready' ? formatPreview(p.value) : '';
  });
  protected readonly resultType = computed(() => {
    const p = this.preview();
    return p.status === 'ready' ? previewType(p.value) : null;
  });
  protected readonly errorText = computed(() => {
    const p = this.preview();
    return p.status === 'error' ? p.message : '';
  });

  protected readonly hint = $localize`:@@expreditor.hint:Текст поза {{ }} лишається як є; усередині — JavaScript. Ctrl+Пробіл — підказки.`;
  protected readonly inputNote =
    this.data.context.input !== undefined
      ? $localize`:@@expreditor.input:На першому вхідному item вузла.`
      : $localize`:@@expreditor.input.none:Вхідних даних немає: $json — порожній об'єкт. Запустіть попередні вузли або закріпіть їхні дані.`;
  protected readonly idleText = this.data.workflowId
    ? $localize`:@@expreditor.idle:Введіть вираз.`
    : $localize`:@@expreditor.unsaved:Збережіть сценарій, щоб бачити результат.`;

  constructor() {
    this.changes
      .pipe(
        debounceTime(PREVIEW_DEBOUNCE_MS),
        distinctUntilChanged(),
        switchMap((text) => this.evaluate(text)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((state) => this.preview.set(state));
    this.changes.next(this.value());
  }

  ngAfterViewInit(): void {
    this.loadEditor()
      .then((create) => {
        const host = this.host()?.nativeElement;
        if (!host) return;
        const editor = create(host, {
          value: this.value(),
          language: 'openbp-expression',
          ariaLabel: this.data.label,
          readOnly: !!this.data.readonly,
          complete: (text, offset) => suggestExpression(text, offset, this.data.context),
        });
        this.editor = editor;
        editor.onDidChangeModelContent(() => this.typed(editor.getValue()));
        this.destroyRef.onDestroy(() => editor.dispose());
        this.editorReady.set(true);
        editor.focus?.();
      })
      .catch(() => this.fallback.set(true));
  }

  protected typed(text: string): void {
    this.value.set(text);
    this.changes.next(text);
  }

  /** Вставка функції каталогу в позицію курсора; поза `{{ }}` — разом із маркерами. */
  protected insert(fn: ExpressionFunction): void {
    const text = this.value();
    const area = this.fallbackEl()?.nativeElement;
    const from = this.editor?.getCursor?.() ?? area?.selectionStart ?? text.length;
    const to = this.editor ? from : (area?.selectionEnd ?? from);
    const result = insertSnippet(text, from, to, fn.insert);
    if (this.editor) {
      this.editor.setValue(result.text);
      this.editor.setCursor?.(result.cursor);
      this.editor.focus?.();
    } else if (area) {
      area.value = result.text;
      area.focus();
      area.setSelectionRange(result.cursor, result.cursor);
    }
    this.typed(result.text);
  }

  private evaluate(text: string): Observable<PreviewState> {
    if (!hasExpression(text)) return of({ status: 'ready', value: text });
    const workflowId = this.data.workflowId;
    if (!workflowId) return of({ status: 'idle' });
    this.preview.set({ status: 'loading' });
    const { input, nodes } = this.data.context;
    const samples = Object.fromEntries(nodes.filter((n) => n.sample !== undefined).map((n) => [n.name, [{ json: n.sample }]]));
    return this.api
      .evaluateExpression(workflowId, {
        expression: text,
        nodeId: this.data.nodeId ?? undefined,
        item: input !== undefined ? { json: input } : undefined,
        nodes: Object.keys(samples).length > 0 ? samples : undefined,
      })
      .pipe(
        map((r: ExpressionPreview): PreviewState => (r.error ? { status: 'error', message: r.error } : { status: 'ready', value: r.value })),
        catchError((e: unknown) => of<PreviewState>({ status: 'error', message: httpMessage(e) })),
      );
  }
}

function httpMessage(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    const body = e.error as { detail?: string; title?: string } | null;
    return body?.detail ?? body?.title ?? $localize`:@@expreditor.http:Не вдалося обчислити: HTTP ${e.status}:status:`;
  }
  return e instanceof Error ? e.message : String(e);
}

/**
 * Відкриває редактор виразів з полів форми вузла. Надає редактор сценарію (`workflowId` — його сценарій);
 * без сервісу (напр. поза редактором) кнопки «розгорнути» в полях немає.
 */
@Injectable()
export class ExpressionEditorService {
  private readonly dialog = inject(MatDialog);

  /** Сценарій, у якому відкрито форму. */
  workflowId: string | null = null;

  open(data: Omit<ExpressionEditorData, 'workflowId'>): Observable<string | undefined> {
    return this.dialog
      .open<ExpressionEditorDialogComponent, ExpressionEditorData, string>(ExpressionEditorDialogComponent, {
        data: { ...data, workflowId: this.workflowId },
        width: '1100px',
        maxWidth: '96vw',
        autoFocus: false,
      })
      .afterClosed();
  }
}
