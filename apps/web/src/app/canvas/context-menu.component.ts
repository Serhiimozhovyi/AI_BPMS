import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, inject, input, output } from '@angular/core';

/** Дія контекстного меню канвасу (F3-58). */
export type CanvasMenuAction = 'run' | 'toggle' | 'duplicate' | 'rename' | 'pin' | 'copy' | 'paste' | 'delete' | 'insert' | 'add' | 'selectAll' | 'addNote' | 'editNote';

/**
 * Контекстне меню канвасу (F3-58): ПКМ на вузлі — дії з вузлом (ті самі, що в тулбарі, плюс «Закріпити output», копіювати
 * й вставити), на ребрі — вставити крок або видалити, на порожньому місці — додати крок, вставити, виділити все,
 * додати нотатку (F3-62); на нотатці — редагувати чи видалити. Лише показує пункти; що робить пункт — вирішує редактор.
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
        @case ('note') {
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-edit-note" (click)="action.emit('editNote')" i18n="@@canvas.menu.editNote">Редагувати нотатку</button>
          <button type="button" role="menuitem" class="menu__item menu__item--danger" data-testid="menu-delete" (click)="action.emit('delete')"><ng-container i18n="@@canvas.menu.deleteNote">Видалити нотатку</ng-container><kbd>Del</kbd></button>
        }
        @default {
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-add" (click)="action.emit('add')"><ng-container i18n="@@canvas.menu.add">Додати крок</ng-container><kbd>Tab</kbd></button>
          <button type="button" role="menuitem" class="menu__item" data-testid="menu-add-note" (click)="action.emit('addNote')"><ng-container i18n="@@canvas.menu.addNote">Додати нотатку</ng-container><kbd>Shift+S</kbd></button>
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
      border: 1px solid var(--bp-bg);
      border-radius: 8px;
      background: var(--bp-white);
      box-shadow: 0 4px 16px rgb(12 43 48 / 0.18);
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
      background: var(--bp-bg);
    }

    .menu__item:disabled {
      opacity: 0.5;
      cursor: default;
    }

    .menu__item--danger {
      color: var(--bp-danger);
    }

    .menu__item kbd {
      color: var(--bp-text-muted);
      font: 11px/1.4 Ubuntu, sans-serif;
    }
  `,
})
export class CanvasContextMenuComponent implements AfterViewInit {
  /** Що під вказівником. */
  readonly kind = input.required<'node' | 'edge' | 'note' | 'canvas'>();

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
