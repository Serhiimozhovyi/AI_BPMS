import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { WorkflowNode } from '../workflows/workflows-api.service';

/** Дії шапки панелі вузла; редактор виконує їх сам (стан шапка не тримає). */
export type NodeHeaderAction = 'test' | 'until' | 'pin' | 'unpin' | 'pinManual' | 'explain' | 'fix' | 'expression' | 'close';

/**
 * Шапка панелі вузла (F3-88): кольорова мітка групи, назва вузла (клік або `F2` — inline-перейменування), людська назва типу
 * (технічний тип — у підказці), головна дія «Тестувати крок», меню «⋯» з рештою дій і «×» «Закрити панель».
 */
@Component({
  selector: 'app-node-panel-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  host: { '(document:pointerdown)': 'outsidePointer($event)', '(keydown.escape)': 'escape($event)' },
  template: `
    <header class="head" data-testid="node-panel-header" [style.--group-color]="groupColor()">
      <span class="head__mark" aria-hidden="true"></span>
      <div class="head__title">
        @if (renaming()) {
          <input #nameInput class="head__input" data-testid="node-panel-name-input" i18n-aria-label="@@nodeheader.name.label" aria-label="Назва вузла" [value]="node().name" (keydown.enter)="commit($event)" (keydown.escape)="cancel($event)" (blur)="commit($event)" />
        } @else if (readonly()) {
          <h2 class="head__name mat-font-title-sm m-0" data-testid="node-panel-name">{{ node().name }}</h2>
        } @else {
          <h2 class="head__name mat-font-title-sm m-0">
            <button type="button" class="head__name-button" data-testid="node-panel-name" i18n-title="@@nodeform.renameHint" title="Перейменувати (F2)" (click)="startRename()">{{ node().name }}</button>
          </h2>
        }
        <span class="head__type" data-testid="node-panel-type" [title]="node().type">{{ typeLabel() }}</span>
      </div>
      @if (!readonly()) {
        <div class="head__actions" data-testid="node-actions">
          <button mat-stroked-button type="button" class="head__test" data-testid="run-node" [disabled]="running()" [attr.aria-busy]="running() || null" (click)="action.emit('test')" i18n-title="@@workfloweditor.runNode.hint" title="Виконати лише цей вузол на вхідних даних з останнього запуску">
            @if (running()) {
              <ng-container i18n="@@workfloweditor.runNode.running">Виконується…</ng-container>
            } @else {
              <ng-container i18n="@@workfloweditor.runNode">Тестувати крок</ng-container>
            }
          </button>
          <span class="head__more">
            <button type="button" class="head__icon" data-testid="node-more" aria-haspopup="menu" [attr.aria-expanded]="menuOpen()" (click)="menuOpen.set(!menuOpen())" i18n-title="@@nodeheader.more.hint" title="Інші дії з вузлом" i18n-aria-label="@@nodeheader.more" aria-label="Ще дії з вузлом">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z" /></svg>
            </button>
            @if (menuOpen()) {
              <div class="head__menu" role="menu" data-testid="node-menu">
                <button type="button" role="menuitem" data-testid="run-until" [disabled]="running()" (click)="pick('until')" i18n-title="@@workfloweditor.runUntil.hint" title="Виконати цей вузол і всі попередні; решта сценарію не виконується" i18n="@@workfloweditor.runUntil">До цього вузла</button>
                @if (pinned()) {
                  <button type="button" role="menuitem" data-testid="unpin-output" (click)="pick('unpin')" i18n-title="@@workfloweditor.unpin.hint" title="Відкріпити output (P)" i18n="@@workfloweditor.unpin">Відкріпити</button>
                } @else if (canPin()) {
                  <button type="button" role="menuitem" data-testid="pin-output" (click)="pick('pin')" i18n-title="@@workfloweditor.pin.hint" title="Тестові запуски віддаватимуть цей output, не виконуючи вузол (P)" i18n="@@workfloweditor.pin">Закріпити output</button>
                } @else {
                  <button type="button" role="menuitem" data-testid="pin-manual" (click)="pick('pinManual')" i18n-title="@@workfloweditor.pinManual.hint" title="Закріпити output, введений вручну: тестові запуски не виконуватимуть вузол (P)" i18n="@@workfloweditor.pinManual">Ввести дані вручну</button>
                }
                <button type="button" role="menuitem" data-testid="assistant-explain" (click)="pick('explain')" i18n="@@workfloweditor.assistant.explain">Пояснити вузол</button>
                @if (hasError()) {
                  <button type="button" role="menuitem" data-testid="assistant-fix" (click)="pick('fix')" i18n="@@workfloweditor.assistant.fix">Виправити помилку</button>
                }
                <button type="button" role="menuitem" data-testid="assistant-expression" (click)="pick('expression')" i18n="@@workfloweditor.assistant.expression">Написати вираз</button>
              </div>
            }
          </span>
        </div>
      }
      <button type="button" class="head__icon" data-testid="panel-close" (click)="action.emit('close')" i18n-title="@@nodeheader.close.hint" title="Закрити панель (Esc)" i18n-aria-label="@@nodeheader.close" aria-label="Закрити панель">
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z" /></svg>
      </button>
    </header>
  `,
  styles: `
    :host {
      display: block;
      flex-shrink: 0;
    }
    .head {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 8px 8px 12px;
      border-bottom: 1px solid var(--ac-line);
    }
    .head__mark {
      align-self: stretch;
      flex-shrink: 0;
      width: 4px;
      border-radius: 2px;
      background: var(--group-color, var(--ac-kit));
    }
    .head__title {
      display: flex;
      flex: 1 1 auto;
      flex-direction: column;
      min-width: 0;
    }
    .head__name {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .head__name-button {
      max-width: 100%;
      padding: 2px 6px;
      margin-left: -6px;
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
    .head__name-button:hover,
    .head__name-button:focus-visible {
      border-color: var(--ac-line);
    }
    .head__input {
      width: 100%;
      min-width: 0;
      padding: 2px 6px;
      border: 1px solid var(--ac-kit);
      border-radius: var(--ac-radius-sm);
      font: inherit;
      font-size: 16px;
    }
    .head__type {
      overflow: hidden;
      font-size: 12px;
      color: var(--ac-cool-grey-4, #57696b);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .head__actions {
      display: flex;
      flex-shrink: 0;
      align-items: center;
      gap: 2px;
    }
    .head__icon {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 36px;
      height: 36px;
      padding: 0;
      border: 0;
      border-radius: var(--ac-radius-sm);
      background: transparent;
      color: var(--ac-vivid);
      cursor: pointer;
    }
    .head__icon svg {
      width: 20px;
      height: 20px;
      fill: currentColor;
    }
    .head__icon:hover {
      background: var(--ac-button);
    }
    .head__icon:focus-visible,
    .head__menu button:focus-visible {
      outline: 2px solid var(--ac-kit);
      outline-offset: -2px;
    }
    .head__more {
      position: relative;
    }
    .head__menu {
      position: absolute;
      top: 100%;
      right: 0;
      z-index: 20;
      display: flex;
      flex-direction: column;
      min-width: 200px;
      padding: 4px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius-sm);
      background: var(--ac-white);
      box-shadow: 0 4px 16px rgb(12 43 48 / 16%);
    }
    .head__menu button {
      padding: 8px 12px;
      border: 0;
      border-radius: var(--ac-radius-sm);
      background: transparent;
      font: inherit;
      font-size: 14px;
      color: var(--ac-vivid);
      text-align: left;
      cursor: pointer;
    }
    .head__menu button:hover:not(:disabled) {
      background: var(--ac-button);
    }
    .head__menu button:disabled {
      color: var(--ac-cool-grey-4, #57696b);
      cursor: default;
    }
  `,
})
export class NodePanelHeaderComponent {
  readonly node = input.required<WorkflowNode>();
  /** Людська назва типу вузла; технічний тип показується в підказці. */
  readonly typeLabel = input('');
  readonly groupColor = input('var(--ac-kit)');
  readonly readonly = input(false);
  readonly running = input(false);
  /** Output вузла закріплено. */
  readonly pinned = input(false);
  /** Є успішний крок, output якого можна закріпити. */
  readonly canPin = input(false);
  /** У вузла є помилка останнього запуску — доступне «Виправити помилку». */
  readonly hasError = input(false);
  /** Запит перейменувати ззовні (клавіша `F2`): змінюється на кожен запит. */
  readonly renameRequest = input(0);

  readonly action = output<NodeHeaderAction>();
  readonly renamed = output<string>();

  protected readonly menuOpen = signal(false);
  protected readonly renaming = signal(false);
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('nameInput');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private lastRenameRequest = 0;

  constructor() {
    effect(() => {
      const input = this.nameInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
    effect(() => {
      const request = this.renameRequest();
      if (request !== this.lastRenameRequest) {
        this.lastRenameRequest = request;
        this.startRename();
      }
    });
  }

  protected readonly canRename = computed(() => !this.readonly());

  protected startRename(): void {
    if (this.canRename()) this.renaming.set(true);
  }

  protected commit(event: Event): void {
    if (!this.renaming()) return;
    const value = (event.target as HTMLInputElement).value.trim();
    this.renaming.set(false);
    if (value && value !== this.node().name) this.renamed.emit(value);
  }

  protected cancel(event: Event): void {
    event.stopPropagation();
    this.renaming.set(false);
  }

  protected pick(action: NodeHeaderAction): void {
    this.menuOpen.set(false);
    this.action.emit(action);
  }

  /** Esc у меню закриває лише меню, а не всю панель. */
  protected escape(event: Event): void {
    if (this.menuOpen()) {
      event.stopPropagation();
      this.menuOpen.set(false);
    }
  }

  protected outsidePointer(event: Event): void {
    if (this.menuOpen() && !this.host.nativeElement.querySelector('.head__more')?.contains(event.target as Node)) this.menuOpen.set(false);
  }
}
