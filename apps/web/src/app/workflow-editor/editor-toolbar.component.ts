import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';
import { EditorView } from '../business-view/editor-view';
import { PublishBarComponent } from '../workflows/publish-bar.component';
import { WorkflowDefinition, WorkflowDetail } from '../workflows/workflows-api.service';
import { AutosaveStatus } from '../canvas/autosave';

/** Останній тестовий запуск у шапці: клас стану й підпис. */
export interface ToolbarRun {
  readonly state: string;
  readonly label: string;
}

export type ToolbarAction = 'undo' | 'redo' | 'copy' | 'paste' | 'delete' | 'assistant' | 'history' | 'settings' | 'run';

/**
 * Шапка редактора сценарію (F3-75): один рядок від 1024 px — «← Сценарії», назва з перейменуванням по кліку,
 * перемикач «Бізнес | Технічний», іконки редагування, меню «⋯» (Помічник, Історія, Налаштування, Журнал виконань)
 * і праворуч завжди видимі: статус запуску, автозбереження, публікація та «Виконати». Стан не зберігає —
 * усі дії віддає редактору через `action`.
 */
@Component({
  selector: 'app-editor-toolbar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatButtonModule, PublishBarComponent],
  host: { '(document:pointerdown)': 'outsidePointer($event)', '(document:keydown.escape)': 'moreOpen.set(false)' },
  template: `
    <header class="bar">
      @if (!embedded()) {
        <a mat-button class="bar__back" routerLink="/workflows" i18n-aria-label="@@workfloweditor.back.label" aria-label="До списку сценаріїв">←<span class="bar__back-text" i18n="@@workfloweditor.back.text"> Сценарії</span></a>
      }
      <div class="bar__title">
        @if (renaming()) {
          <input #nameInput class="bar__name-input" data-testid="workflow-name-input" i18n-aria-label="@@workfloweditor.name.label" aria-label="Назва сценарію" [value]="title()" (keydown.enter)="commitRename($event)" (keydown.escape)="cancelRename($event)" (blur)="commitRename($event)" />
        } @else {
          <h1 class="bar__name mat-font-title-md m-0" data-testid="workflow-name" [title]="nameHint()">
            @if (canRename()) {
              <button type="button" class="bar__name-button" data-testid="workflow-name-edit" (click)="startRename()" i18n-title="@@workfloweditor.name.edit" title="Перейменувати сценарій">{{ title() }}</button>
            } @else {
              {{ title() }}
            }
          </h1>
        }
        @if (groupName(); as group) {
          @if (embedded()) {
            <span class="bar__group" data-testid="workflow-group">{{ group }}</span>
          } @else {
            <a class="bar__group" data-testid="workflow-group" [routerLink]="['/workflows']" [queryParams]="{ group: workflow()?.groupId }" i18n-title="@@workfloweditor.group.hint" title="Сценарії цієї групи">{{ group }}</a>
          }
        }
        @if (definition(); as def) {
          <span class="sr-only" data-testid="counts" i18n="@@workfloweditor.counts">
            вузлів: {{ def.nodes.length }} · ребер: {{ def.connections.length }}
          </span>
        }
      </div>
      @if (definition()) {
        <span class="bar__views" role="radiogroup" i18n-aria-label="@@workfloweditor.view" aria-label="Вигляд сценарію">
          <button type="button" role="radio" class="bar__view" data-testid="view-business" [class.bar__view--on]="view() === 'business'" [attr.aria-checked]="view() === 'business'" (click)="viewChange.emit('business')" i18n-title="@@workfloweditor.view.business.hint" title="Хто що робить і в якому порядку — без технічних деталей" i18n="@@workfloweditor.view.business">Бізнес</button>
          <button type="button" role="radio" class="bar__view" data-testid="view-technical" [class.bar__view--on]="view() === 'technical'" [attr.aria-checked]="view() === 'technical'" (click)="viewChange.emit('technical')" i18n-title="@@workfloweditor.view.technical.hint" title="Вузли, порти й параметри" i18n="@@workfloweditor.view.technical">Технічний</button>
        </span>
      }
      @if (readonly()) {
        <span class="bar__readonly" data-testid="readonly" i18n="@@workfloweditor.readonly">Лише перегляд</span>
        @if (version(); as v) {
          <span class="bar__readonly" data-testid="version-banner" i18n="@@workfloweditor.versionBanner">Версія {{ v }}</span>
          @if (!embedded()) {
            <a mat-button data-testid="version-current" [routerLink]="['/workflows', workflow()?.id]" i18n="@@workfloweditor.versionCurrent">До поточної версії</a>
          }
        }
      } @else if (definition()) {
        <span class="bar__tools" role="toolbar" i18n-aria-label="@@workfloweditor.tools" aria-label="Редагування">
          <button type="button" class="bar__icon" data-testid="undo" [disabled]="!canUndo()" (click)="action.emit('undo')" i18n-title="@@workfloweditor.undo.hint" title="Скасувати (Ctrl+Z)" i18n-aria-label="@@workfloweditor.undo" aria-label="Скасувати">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12.5 8c-2.65 0-5.05.99-6.9 2.6L2 7v9h9l-3.62-3.62c1.39-1.16 3.16-1.88 5.12-1.88 3.54 0 6.55 2.31 7.6 5.5l2.37-.78C21.08 11.03 17.15 8 12.5 8z" /></svg>
          </button>
          <button type="button" class="bar__icon" data-testid="redo" [disabled]="!canRedo()" (click)="action.emit('redo')" i18n-title="@@workfloweditor.redo.hint" title="Повторити (Ctrl+Shift+Z)" i18n-aria-label="@@workfloweditor.redo" aria-label="Повторити">
            <svg viewBox="0 0 24 24" aria-hidden="true" style="transform: scaleX(-1)"><path d="M12.5 8c-2.65 0-5.05.99-6.9 2.6L2 7v9h9l-3.62-3.62c1.39-1.16 3.16-1.88 5.12-1.88 3.54 0 6.55 2.31 7.6 5.5l2.37-.78C21.08 11.03 17.15 8 12.5 8z" /></svg>
          </button>
          <button type="button" class="bar__icon" data-testid="copy" [disabled]="!hasNodeSelection()" (click)="action.emit('copy')" i18n-title="@@workfloweditor.copy.hint" title="Копіювати (Ctrl+C)" i18n-aria-label="@@workfloweditor.copy" aria-label="Копіювати">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z" /></svg>
          </button>
          <button type="button" class="bar__icon" data-testid="paste" [disabled]="!canPaste()" (click)="action.emit('paste')" i18n-title="@@workfloweditor.paste.hint" title="Вставити (Ctrl+V)" i18n-aria-label="@@workfloweditor.paste" aria-label="Вставити">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 2h-4.18C14.4.84 13.3 0 12 0c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm7 18H5V4h2v3h10V4h2v16z" /></svg>
          </button>
          <button type="button" class="bar__icon" data-testid="delete" [disabled]="!hasSelection()" (click)="action.emit('delete')" i18n-title="@@workfloweditor.delete.hint" title="Видалити (Delete)" i18n-aria-label="@@workfloweditor.delete" aria-label="Видалити">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z" /></svg>
          </button>
          <span class="bar__more">
            <button type="button" class="bar__icon" data-testid="toolbar-more" aria-haspopup="menu" [attr.aria-expanded]="moreOpen()" (click)="moreOpen.set(!moreOpen())" i18n-title="@@workfloweditor.more.hint" title="Помічник, історія, налаштування, журнал виконань" i18n-aria-label="@@workfloweditor.more" aria-label="Ще">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z" /></svg>
            </button>
            @if (moreOpen()) {
              <div class="bar__menu" role="menu" data-testid="toolbar-menu">
                <button type="button" role="menuitem" data-testid="assistant-toggle" [attr.aria-pressed]="assistantOpen()" (click)="pick('assistant')" i18n-title="@@workfloweditor.assistant.hint" title="ШІ-помічник: описати процес, пояснити чи виправити вузол" i18n="@@workfloweditor.assistant">Помічник</button>
                <button type="button" role="menuitem" data-testid="history" (click)="pick('history')" i18n-title="@@workfloweditor.history.hint" title="Версії сценарію: переглянути або відновити" i18n="@@workfloweditor.history">Історія</button>
                <button type="button" role="menuitem" data-testid="settings" (click)="pick('settings')" i18n-title="@@workfloweditor.settings.hint" title="Часовий пояс, таймаут, сценарій помилок" i18n="@@workfloweditor.settings">Налаштування</button>
                @if (lastRun(); as last) {
                  @if (!embedded()) {
                    <a role="menuitem" data-testid="last-run-menu" [routerLink]="['/executions']" [queryParams]="{ workflowId: workflow()?.id }" (click)="moreOpen.set(false)">{{ lastRunTitle() }}</a>
                  }
                }
                @if (!embedded()) {
                  <a role="menuitem" data-testid="executions-link" (click)="moreOpen.set(false)" [routerLink]="['/executions']" [queryParams]="{ workflowId: workflow()?.id }" i18n="@@workfloweditor.executions">Журнал виконань</a>
                }
              </div>
            }
          </span>
        </span>
      }
      <span class="bar__end">
        @if (lastRun(); as last) {
          @if (!embedded()) {
            <a class="bar__last bar__last--{{ last.state }}" data-testid="last-run" [routerLink]="['/executions']" [queryParams]="{ workflowId: workflow()?.id }" [title]="lastRunTitle()">{{ lastRunTitle() }}</a>
          }
        }
        @if (run(); as r) {
          <span class="bar__run bar__run--{{ r.state }}" data-testid="run-status" [attr.data-state]="r.state" [title]="r.label">{{ r.label }}</span>
        }
        @switch (saveStatus()) {
          @case ('pending') {
            <span class="bar__save" data-testid="dirty" data-status="pending" i18n="@@workfloweditor.dirty">Є незбережені зміни</span>
          }
          @case ('saving') {
            <span class="bar__save" data-testid="dirty" data-status="saving" i18n="@@workfloweditor.saving">Збереження…</span>
          }
          @case ('error') {
            <span class="bar__save bar__save--error" data-testid="dirty" data-status="error" i18n="@@workfloweditor.saveError">Помилка збереження</span>
          }
          @case ('saved') {
            <span class="bar__save bar__save--ok" data-testid="saved" [title]="savedHint()" i18n="@@workfloweditor.saved">Збережено</span>
          }
        }
        @if (!version()) {
          <app-publish-bar [workflow]="workflow()" [draft]="definition()" [saving]="saveStatus() === 'pending' || saveStatus() === 'saving'" [readonly]="readonly()" (published)="published.emit($event)" (discarded)="discarded.emit($event)" />
        }
        @if (definition() && !readonly()) {
          <button mat-flat-button type="button" class="bar__run-button" data-testid="run" [disabled]="running() || !definition()?.nodes?.length" (click)="action.emit('run')" i18n-title="@@workfloweditor.run.hint" title="Зберегти і виконати з тестовими даними (pinData)">
            @if (running()) {
              <ng-container i18n="@@workfloweditor.running">Виконується…</ng-container>
            } @else {
              <ng-container i18n="@@workfloweditor.run">Виконати</ng-container>
            }
          </button>
        }
      </span>
    </header>
  `,
  styles: `
    :host {
      display: block;
      flex-shrink: 0;
    }

    .bar {
      display: flex;
      align-items: center;
      gap: 12px;
      min-height: 48px;
      max-height: 56px;
      padding: 0 12px;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      background: var(--ac-white);
      flex-wrap: nowrap;
      overflow: visible;
    }

    .bar > :not(.bar__title),
    .bar__end > * {
      flex-shrink: 0;
    }

    .bar__back {
      min-width: 0;
      white-space: nowrap;
    }

    .bar__title {
      display: flex;
      flex: 1 1 120px;
      align-items: center;
      gap: 8px;
      min-width: 48px;
    }

    .bar__name {
      min-width: 0;
      overflow: hidden;
      font-size: 18px;
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    .bar__name-button {
      max-width: 100%;
      padding: 2px 6px;
      overflow: hidden;
      border: 1px solid transparent;
      border-radius: var(--ac-radius-sm);
      background: transparent;
      font: inherit;
      color: inherit;
      text-align: left;
      text-overflow: ellipsis;
      white-space: nowrap;
      cursor: text;
    }

    .bar__name-button:hover,
    .bar__name-button:focus-visible {
      border-color: var(--ac-line);
    }

    .bar__name-input {
      width: 100%;
      min-width: 0;
      padding: 2px 6px;
      border: 1px solid var(--ac-kit);
      border-radius: var(--ac-radius-sm);
      font: inherit;
      font-size: 18px;
    }

    .bar__group {
      flex-shrink: 0;
      padding: 2px 10px;
      border-radius: 12px;
      background: var(--ac-button);
      font-size: 12px;
      text-decoration: none;
    }

    @media (max-width: 1440px) {
      .bar__group {
        display: none;
      }
    }

    .bar__views {
      display: inline-flex;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius-sm);
      overflow: hidden;
    }

    .bar__view {
      padding: 4px 12px;
      white-space: nowrap;
      border: 0;
      font: inherit;
      font-size: 13px;
      color: var(--ac-vivid);
      background: var(--ac-white);
      cursor: pointer;
    }

    .bar__view--on {
      color: var(--ac-kit);
      background: var(--ac-button);
      font-weight: 500;
    }

    .bar__readonly {
      font-size: 13px;
      opacity: 0.6;
    }

    .bar__tools {
      display: inline-flex;
      align-items: center;
      gap: 2px;
    }

    .bar__icon {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 36px;
      height: 36px;
      padding: 0;
      border: 0;
      border-radius: var(--ac-radius-sm);
      background: transparent;
      font: inherit;
      color: var(--ac-vivid);
      cursor: pointer;
    }

    .bar__icon svg {
      width: 20px;
      height: 20px;
      fill: currentColor;
    }

    .bar__icon:hover:not(:disabled) {
      background: var(--ac-button);
    }

    .bar__icon:focus-visible,
    .bar__menu button:focus-visible,
    .bar__menu a:focus-visible {
      outline: 2px solid var(--ac-kit);
      outline-offset: -2px;
    }

    .bar__icon:disabled {
      color: var(--ac-cool-grey-4, #8d86a3);
      cursor: default;
    }

    .bar__more {
      position: relative;
    }

    .bar__menu {
      position: absolute;
      top: 100%;
      right: 0;
      z-index: 20;
      display: flex;
      flex-direction: column;
      min-width: 180px;
      padding: 4px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius-sm);
      background: var(--ac-white);
      box-shadow: 0 4px 16px rgb(38 31 75 / 16%);
    }

    .bar__menu button,
    .bar__menu a {
      padding: 8px 12px;
      border: 0;
      border-radius: var(--ac-radius-sm);
      background: transparent;
      font: inherit;
      font-size: 14px;
      color: var(--ac-vivid);
      text-align: left;
      text-decoration: none;
      cursor: pointer;
    }

    .bar__menu button:hover,
    .bar__menu a:hover,
    .bar__menu button[aria-pressed='true'] {
      background: var(--ac-button);
    }

    .bar__end {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-left: auto;
    }

    .bar__save {
      font-size: 13px;
      white-space: nowrap;
      color: var(--ac-cool-grey-4, #8d86a3);
    }

    .bar__save--error {
      color: var(--mat-sys-error);
    }

    /* Підпис останнього запуску — лише на широких екранах; на вужчих він у меню «⋯». */
    .bar__last {
      display: none;
      font-size: 13px;
      white-space: nowrap;
      color: var(--ac-cool-grey-4, #8d86a3);
      text-decoration: none;
    }

    .bar__last:hover {
      text-decoration: underline;
    }

    .bar__last--error,
    .bar__last--timed_out {
      color: var(--ac-red);
    }

    @media (min-width: 1500px) {
      .bar__last {
        display: inline;
      }
    }

    .bar__run {
      max-width: 160px;
      padding: 2px 10px;
      overflow: hidden;
      border-radius: 12px;
      font-size: 13px;
      white-space: nowrap;
      text-overflow: ellipsis;
      background: var(--mat-sys-surface-variant);
    }

    .bar__run--success {
      color: #fff;
      background: var(--ac-green);
    }

    .bar__run--error,
    .bar__run--timed_out {
      color: #fff;
      background: var(--ac-red);
    }

    @media (max-width: 1200px) {
      .bar {
        gap: 8px;
        padding: 0 8px;
      }

      .bar__back-text {
        display: none;
      }

      .bar__back {
        min-width: 0;
        padding: 0 8px;
      }

      .bar__view {
        padding: 4px 8px;
      }

      .bar__icon {
        width: 32px;
      }

      .bar__run {
        max-width: 90px;
      }
    }

    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
      white-space: nowrap;
    }
  `,
})
export class EditorToolbarComponent {
  readonly workflow = input<WorkflowDetail | null>(null);
  readonly definition = input<WorkflowDefinition | null>(null);
  readonly defaultTitle = input('');
  readonly groupName = input<string | null>(null);
  readonly embedded = input(false);
  readonly readonly = input(false);
  readonly version = input<number | null>(null);
  readonly view = input<EditorView>('technical');
  readonly canUndo = input(false);
  readonly canRedo = input(false);
  readonly canPaste = input(false);
  readonly hasSelection = input(false);
  readonly hasNodeSelection = input(false);
  readonly assistantOpen = input(false);
  readonly running = input(false);
  readonly run = input<ToolbarRun | null>(null);
  /** Останнє виконання при відкритті редактора (F3-76): підпис у шапці та в меню «⋯». */
  readonly lastRun = input<ToolbarRun | null>(null);
  readonly saveStatus = input<AutosaveStatus>('idle');

  readonly action = output<ToolbarAction>();
  readonly viewChange = output<EditorView>();
  readonly renamed = output<string>();
  readonly published = output<WorkflowDetail>();
  readonly discarded = output<WorkflowDetail>();

  protected readonly moreOpen = signal(false);
  protected readonly renaming = signal(false);
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('nameInput');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly title = computed(() => this.definition()?.name || this.workflow()?.name || this.defaultTitle());
  protected readonly canRename = computed(() => !this.readonly() && !!this.workflow());
  protected readonly nameHint = computed(() => {
    const def = this.definition();
    const counts = def ? ` — ${$localize`:@@workfloweditor.counts.hint:вузлів: ${def.nodes.length}:nodes: · ребер: ${def.connections.length}:edges:`}` : '';
    return `${this.title()}${counts}`;
  });
  protected readonly lastRunTitle = computed(() => $localize`:@@workfloweditor.lastRun:Останній запуск: ${this.lastRun()?.label ?? ''}:label:`);
  protected readonly savedHint = computed(() => $localize`:@@workfloweditor.saved.hint:Версія ${this.workflow()?.version ?? ''}:version:`);

  constructor() {
    effect(() => {
      const input = this.nameInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
  }

  protected pick(action: ToolbarAction): void {
    this.moreOpen.set(false);
    this.action.emit(action);
  }

  protected startRename(): void {
    if (this.canRename()) this.renaming.set(true);
  }

  protected commitRename(event: Event): void {
    if (!this.renaming()) return;
    const value = (event.target as HTMLInputElement).value.trim();
    this.renaming.set(false);
    if (value && value !== this.title()) this.renamed.emit(value);
  }

  protected cancelRename(event: Event): void {
    event.stopPropagation();
    this.renaming.set(false);
  }

  protected outsidePointer(event: Event): void {
    if (this.moreOpen() && !this.host.nativeElement.querySelector('.bar__more')?.contains(event.target as Node)) this.moreOpen.set(false);
  }
}
