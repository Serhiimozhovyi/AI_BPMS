import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, inject, input, output } from '@angular/core';

/** Дія контекстного меню канвасу (F3-58). */
export type CanvasMenuAction = 'run' | 'toggle' | 'duplicate' | 'rename' | 'pin' | 'copy' | 'paste' | 'delete' | 'insert' | 'add' | 'selectAll';

/**
 * Контекстне меню канвасу (F3-58): ПКМ на вузлі — дії з вузлом (ті самі, що в тулбарі, плюс «Закріпити output», копіювати
 * й вставити), на ребрі — вставити крок або видалити, на порожньому місці — додати крок, вставити, виділити все.
 * Лише показує пункти; що робить пункт — вирішує редактор.
 */
@Component({
  selector: 'app-canvas-context-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="menu" role="menu" tabindex="-1" data-testid="context-menu" [attr.data-kind]="kind()" (keydown.escape)="closed.emit()">
      @switch (kind()) {
        @case ('node') {
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-run" [disabled]="running()" (click)="action.emit('run')" i18n="@@canvas.menu.run">Виконати вузол</button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-toggle" (click)="action.emit('toggle')">
            @if (nodeDisabled()) {
              <ng-container i18n="@@canvas.menu.enable">Увімкнути</ng-container>
            } @else {
              <ng-container i18n="@@canvas.menu.disable">Вимкнути</ng-container>
            }
            <kbd>D</kbd>
          </button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-duplicate" (click)="action.emit('duplicate')"><ng-container i18n="@@canvas.menu.duplicate">Дублювати</ng-container><kbd>Ctrl+D</kbd></button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-rename" (click)="action.emit('rename')"><ng-container i18n="@@canvas.menu.rename">Перейменувати</ng-container><kbd>F2</kbd></button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-pin" (click)="action.emit('pin')">
            @if (nodePinned()) {
              <ng-container i18n="@@canvas.menu.unpin">Відкріпити output</ng-container>
            } @else {
              <ng-container i18n="@@canvas.menu.pin">Закріпити output</ng-container>
            }
            <kbd>P</kbd>
          </button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-copy" (click)="action.emit('copy')"><ng-container i18n="@@canvas.menu.copy">Копіювати</ng-container><kbd>Ctrl+C</kbd></button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-paste" [disabled]="!canPaste()" (click)="action.emit('paste')"><ng-container i18n="@@canvas.menu.paste">Вставити</ng-container><kbd>Ctrl+V</kbd></button>
          <button type="button" role="menuitem" class="menu__item menu__item--danger" data-testid="menu-delete" (click)="action.emit('delete')"><ng-container i18n="@@canvas.menu.delete">Видалити</ng-container><kbd>Del</kbd></button>
        }
        @case ('edge') {
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-insert" (click)="action.emit('insert')" i18n="@@canvas.menu.insert">Вставити крок між вузлами</button>
          <button type="button" role="menuitem" class="menu__item menu__item--danger" data-testid="menu-delete" (click)="action.emit('delete')"><ng-container i18n="@@canvas.menu.deleteEdge">Видалити з'єднання</ng-container><kbd>Del</kbd></button>
        }
        @default {
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-add" (click)="action.emit('add')"><ng-container i18n="@@canvas.menu.add">Додати крок</ng-container><kbd>Tab</kbd></button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-paste" [disabled]="!canPaste()" (click)="action.emit('paste')"><ng-container i18n="@@canvas.menu.paste">Вставити</ng-container><kbd>Ctrl+V</kbd></button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-select-all" (click)="action.emit('selectAll')"><ng-container i18n="@@canvas.menu.selectAll">Виділити все</ng-container><kbd>Ctrl+A</kbd></button>
        }
      }
    </div>
  `,
  styles: `
    .menu {
      display: flex;
      flex-direction: column;
      min-width: 220px;
      padding: 4px 0;
      border: 1px solid var(--ac-cool-grey);
      border-radius: 8px;
      background: var(--ac-white);
      box-shadow: 0 4px 16px rgb(38 31 75 / 0.18);
    }

    .menu__item {
      display: flex;
      justify-content: space-between;
      gap: 16px;
      padding: 6px 12px;
      border: 0;
      background: none;
      color: inherit;
      font: 13px/1.4 Ubuntu, sans-serif;
      text-align: left;
      cursor: pointer;
    }

    .menu__item:hover:not(:disabled),
    .menu__item:focus-visible {
      background: var(--ac-cool-grey);
    }

    .menu__item:disabled {
      opacity: 0.5;
      cursor: default;
    }

    .menu__item--danger {
      color: var(--ac-red);
    }

    .menu__item kbd {
      color: var(--ac-cool-grey-4);
      font: 11px/1.4 Ubuntu, sans-serif;
    }
  `,
})
export class CanvasContextMenuComponent implements AfterViewInit {
  /** Що під вказівником. */
  readonly kind = input.required<'node' | 'edge' | 'canvas'>();

  /** Вузол вимкнений — пункт «Увімкнути» замість «Вимкнути». */
  readonly nodeDisabled = input(false);

  /** Output вузла закріплено — пункт «Відкріпити output». */
  readonly nodePinned = input(false);

  readonly canPaste = input(false);

  /** Йде запуск — «Виконати вузол» недоступне. */
  readonly running = input(false);

  readonly action = output<CanvasMenuAction>();

  /** Escape. */
  readonly closed = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  ngAfterViewInit(): void {
    // Клавіатура: фокус на першому доступному пункті.
    this.host.nativeElement.querySelector<HTMLElement>('button:not(:disabled)')?.focus();
  }
}
