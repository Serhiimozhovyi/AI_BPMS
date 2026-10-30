import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { FieldType, FieldTypeConfig } from '@ngx-formly/core';
import { NodeFieldProps } from './schema-fields';
import {
  BotCommandRow,
  InlineAction,
  InlineButton,
  KeyboardIssue,
  KeyboardMode,
  KeyboardModel,
  MAX_INLINE_ROW,
  ReplyButton,
  ReplyRequest,
  commandIssue,
  keyboardIssues,
  moveItem,
  parseCommands,
  parseKeyboard,
  serializeCommands,
  serializeKeyboard,
  toggleChoice,
} from './widgets';

/**
 * Редактор кнопок `reply_markup` (F6-30, `x-openbp-widget: keyboard`): інлайн-кнопки під повідомленням, клавіатура
 * замість поля вводу або її прибирання. Значення у вузлі — той самий об'єкт Bot API, формат вузла не змінюється;
 * розмітка, яку редактор не вміє показати без втрат (вираз, web_app…), лишається «як є» в режимі «Код».
 */
@Component({
  selector: 'app-node-keyboard-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <div class="kb" [attr.data-field]="key">
      <span class="kb__label">{{ label }}</span>
      <div class="kb__modes" role="radiogroup" [attr.aria-label]="label">
        @for (m of modes; track m.id) {
          <button
            type="button"
            role="radio"
            class="kb__mode"
            [class.kb__mode--on]="state().mode === m.id"
            [attr.aria-checked]="state().mode === m.id"
            [attr.data-testid]="'kb-mode-' + m.id"
            (click)="setMode(m.id)"
          >{{ m.label }}</button>
        }
      </div>

      @switch (state().mode) {
        @case ('inline') {
          <div class="kb__rows" data-testid="kb-inline">
            @for (row of state().inline; track $index; let r = $index) {
              <div class="kb__row" [attr.data-row]="r">
                @for (b of row; track $index; let c = $index) {
                  <div class="kb__btn" [attr.data-button]="r + '-' + c">
                    <input class="kb__input" [attr.data-testid]="'kb-text-' + r + '-' + c" [value]="b.text" i18n-placeholder="@@kb.text" placeholder="Текст кнопки" i18n-aria-label="@@kb.text" aria-label="Текст кнопки" (change)="editInline(r, c, { text: $any($event.target).value })" />
                    <select class="kb__input" [attr.data-testid]="'kb-action-' + r + '-' + c" i18n-aria-label="@@kb.action" aria-label="Дія кнопки" (change)="editInline(r, c, { action: $any($event.target).value })">
                      <option value="callback" [selected]="b.action === 'callback'" i18n="@@kb.callback">Відправити боту (callback_data)</option>
                      <option value="url" [selected]="b.action === 'url'" i18n="@@kb.url">Відкрити посилання (url)</option>
                    </select>
                    <input
                      class="kb__input"
                      [attr.data-testid]="'kb-value-' + r + '-' + c"
                      [value]="b.value"
                      [placeholder]="b.action === 'url' ? 'https://…' : 'book:{{ $json.id }}'"
                      [attr.aria-label]="b.action === 'url' ? urlLabel : callbackLabel"
                      (change)="editInline(r, c, { value: $any($event.target).value })"
                    />
                    <span class="kb__tools">
                      <button type="button" class="kb__icon" [disabled]="c === 0" [attr.data-testid]="'kb-left-' + r + '-' + c" i18n-title="@@kb.left" title="Ліворуч" (click)="moveButton(r, c, -1)">←</button>
                      <button type="button" class="kb__icon" [disabled]="c === row.length - 1" [attr.data-testid]="'kb-right-' + r + '-' + c" i18n-title="@@kb.right" title="Праворуч" (click)="moveButton(r, c, 1)">→</button>
                      <button type="button" class="kb__icon" [attr.data-testid]="'kb-remove-' + r + '-' + c" i18n-title="@@kb.removeButton" title="Видалити кнопку" (click)="removeButton(r, c)">✕</button>
                    </span>
                  </div>
                }
                <div class="kb__rowtools">
                  <button mat-stroked-button type="button" [attr.data-testid]="'kb-add-button-' + r" (click)="addButton(r)" i18n="@@kb.addButton">Додати кнопку</button>
                  <button type="button" class="kb__icon" [disabled]="r === 0" [attr.data-testid]="'kb-row-up-' + r" i18n-title="@@kb.rowUp" title="Рядок вище" (click)="moveRow(r, -1)">↑</button>
                  <button type="button" class="kb__icon" [disabled]="r === state().inline.length - 1" [attr.data-testid]="'kb-row-down-' + r" i18n-title="@@kb.rowDown" title="Рядок нижче" (click)="moveRow(r, 1)">↓</button>
                  <button type="button" class="kb__icon" [attr.data-testid]="'kb-row-remove-' + r" i18n-title="@@kb.removeRow" title="Видалити рядок" (click)="removeRow(r)">✕</button>
                </div>
              </div>
            }
            <button mat-stroked-button type="button" data-testid="kb-add-row" (click)="addRow()" i18n="@@kb.addRow">Додати рядок</button>
          </div>
        }
        @case ('reply') {
          <div class="kb__rows" data-testid="kb-reply">
            @for (row of state().reply; track $index; let r = $index) {
              <div class="kb__row" [attr.data-row]="r">
                @for (b of row; track $index; let c = $index) {
                  <div class="kb__btn" [attr.data-button]="r + '-' + c">
                    <input class="kb__input" [attr.data-testid]="'kb-text-' + r + '-' + c" [value]="b.text" i18n-placeholder="@@kb.text" placeholder="Текст кнопки" i18n-aria-label="@@kb.text" aria-label="Текст кнопки" (change)="editReply(r, c, { text: $any($event.target).value })" />
                    <select class="kb__input" [attr.data-testid]="'kb-request-' + r + '-' + c" i18n-aria-label="@@kb.request" aria-label="Що запросити" (change)="editReply(r, c, { request: $any($event.target).value })">
                      <option value="none" [selected]="b.request === 'none'" i18n="@@kb.requestNone">Лише текст</option>
                      <option value="contact" [selected]="b.request === 'contact'" i18n="@@kb.requestContact">Запросити контакт</option>
                      <option value="location" [selected]="b.request === 'location'" i18n="@@kb.requestLocation">Запросити геолокацію</option>
                    </select>
                    <span class="kb__tools">
                      <button type="button" class="kb__icon" [disabled]="c === 0" [attr.data-testid]="'kb-left-' + r + '-' + c" i18n-title="@@kb.left" title="Ліворуч" (click)="moveButton(r, c, -1)">←</button>
                      <button type="button" class="kb__icon" [disabled]="c === row.length - 1" [attr.data-testid]="'kb-right-' + r + '-' + c" i18n-title="@@kb.right" title="Праворуч" (click)="moveButton(r, c, 1)">→</button>
                      <button type="button" class="kb__icon" [attr.data-testid]="'kb-remove-' + r + '-' + c" i18n-title="@@kb.removeButton" title="Видалити кнопку" (click)="removeButton(r, c)">✕</button>
                    </span>
                  </div>
                }
                <div class="kb__rowtools">
                  <button mat-stroked-button type="button" [attr.data-testid]="'kb-add-button-' + r" (click)="addButton(r)" i18n="@@kb.addButton">Додати кнопку</button>
                  <button type="button" class="kb__icon" [disabled]="r === 0" [attr.data-testid]="'kb-row-up-' + r" i18n-title="@@kb.rowUp" title="Рядок вище" (click)="moveRow(r, -1)">↑</button>
                  <button type="button" class="kb__icon" [disabled]="r === state().reply.length - 1" [attr.data-testid]="'kb-row-down-' + r" i18n-title="@@kb.rowDown" title="Рядок нижче" (click)="moveRow(r, 1)">↓</button>
                  <button type="button" class="kb__icon" [attr.data-testid]="'kb-row-remove-' + r" i18n-title="@@kb.removeRow" title="Видалити рядок" (click)="removeRow(r)">✕</button>
                </div>
              </div>
            }
            <button mat-stroked-button type="button" data-testid="kb-add-row" (click)="addRow()" i18n="@@kb.addRow">Додати рядок</button>
            <div class="kb__opts">
              <label><input type="checkbox" data-testid="kb-resize" [checked]="state().resize" (change)="setFlag('resize', $any($event.target).checked)" /> <span i18n="@@kb.resize">Стиснути за висотою (resize)</span></label>
              <label><input type="checkbox" data-testid="kb-onetime" [checked]="state().oneTime" (change)="setFlag('oneTime', $any($event.target).checked)" /> <span i18n="@@kb.oneTime">Сховати після натискання (one_time)</span></label>
            </div>
          </div>
        }
        @case ('remove') {
          <p class="kb__hint" data-testid="kb-remove-hint" i18n="@@kb.removeHint">Кнопки клавіатури користувача буде прибрано (remove_keyboard).</p>
        }
        @case ('raw') {
          <textarea class="kb__raw" rows="4" data-testid="kb-raw" [value]="rawText()" i18n-aria-label="@@kb.raw" aria-label="Розмітка кнопок" (change)="setRaw($any($event.target).value)"></textarea>
          <p class="kb__hint" i18n="@@kb.rawHint">Розмітка не вкладається в редактор (вираз або поля, яких він не знає), тому показано як є. Оберіть режим вище, щоб зібрати кнопки наново.</p>
        }
      }

      @if (issues().length > 0) {
        <ul class="kb__issues" data-testid="kb-issues">
          @for (i of issues(); track i) {
            <li>{{ issueText(i) }}</li>
          }
        </ul>
      }
      @if (state().mode === 'inline' || state().mode === 'reply') {
        <p class="kb__hint" i18n="@@kb.exprHint">У текстах і діях можна писати вирази {{ '{{ $json.поле }}' }}.</p>
        <div class="kb__preview" data-testid="kb-preview" [class.kb__preview--reply]="state().mode === 'reply'" aria-hidden="true">
          @for (row of previewRows(); track $index) {
            <div class="kb__prow">
              @for (text of row; track $index) {
                <span class="kb__pbtn">{{ text || '…' }}</span>
              }
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .kb { display: flex; flex-direction: column; gap: 8px; margin: 0 0 12px; font: var(--bp-font-p3); }
    .kb__label { font-size: 13px; }
    .kb__modes { display: flex; flex-wrap: wrap; gap: 4px; }
    .kb__mode { padding: 4px 10px; border: 1px solid var(--ac-line); border-radius: 14px; background: var(--ac-white); font: inherit; cursor: pointer; }
    .kb__mode--on { border-color: var(--ac-vivid); color: var(--ac-vivid); font-weight: 500; }
    .kb__rows { display: flex; flex-direction: column; gap: 8px; }
    .kb__row { display: flex; flex-direction: column; gap: 4px; padding: 8px; border: 1px solid var(--ac-line); border-radius: var(--ac-radius-sm); background: var(--ac-cool-grey); }
    .kb__btn { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; }
    .kb__input { min-width: 120px; min-height: 28px; padding: 0 6px; border: 1px solid var(--ac-line); border-radius: var(--ac-radius-sm); background: var(--ac-white); font: inherit; }
    .kb__tools, .kb__rowtools { display: flex; gap: 2px; align-items: center; }
    .kb__icon { padding: 2px 6px; border: none; background: none; color: var(--ac-vivid); cursor: pointer; }
    .kb__icon:disabled { opacity: 0.3; cursor: default; }
    .kb__opts { display: flex; flex-wrap: wrap; gap: 12px; }
    .kb__raw { width: 100%; font: 12px/16px monospace; }
    .kb__hint { margin: 0; color: var(--ac-cool-grey-4); font-size: 12px; }
    .kb__issues { margin: 0; padding-left: 18px; color: var(--ac-red, #c62828); font-size: 12px; }
    .kb__preview { display: flex; flex-direction: column; gap: 4px; padding: 8px; border-radius: 8px; background: var(--ac-cool-grey-2); }
    .kb__prow { display: flex; gap: 4px; }
    .kb__pbtn { flex: 1; padding: 4px 8px; border-radius: 6px; background: var(--ac-white); text-align: center; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .kb__preview--reply .kb__pbtn { background: var(--ac-cool-grey); border: 1px solid var(--ac-line); }
  `,
})
export class NodeKeyboardFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  protected readonly state = signal<KeyboardModel>(parseKeyboard(undefined));
  protected readonly issues = computed(() => keyboardIssues(this.state()));
  protected readonly previewRows = computed(() => {
    const m = this.state();
    return m.mode === 'inline' ? m.inline.map((r) => r.map((b) => b.text)) : m.mode === 'reply' ? m.reply.map((r) => r.map((b) => b.text)) : [];
  });
  protected readonly rawText = computed(() => {
    const raw = this.state().raw;
    return typeof raw === 'string' ? raw : raw === undefined ? '' : JSON.stringify(raw, null, 2);
  });
  protected readonly urlLabel = $localize`:@@kb.urlLabel:Посилання кнопки`;
  protected readonly callbackLabel = $localize`:@@kb.callbackLabel:Дані для бота (callback_data)`;
  protected readonly modes: { id: KeyboardMode; label: string }[] = [
    { id: 'none', label: $localize`:@@kb.modeNone:Без кнопок` },
    { id: 'inline', label: $localize`:@@kb.modeInline:Інлайн-кнопки` },
    { id: 'reply', label: $localize`:@@kb.modeReply:Клавіатура` },
    { id: 'remove', label: $localize`:@@kb.modeRemove:Прибрати клавіатуру` },
  ];

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  ngOnInit(): void {
    this.state.set(parseKeyboard(this.formControl.value));
    // Зовнішня зміна значення (скидання, вставка) перечитується, а наша власна — ні, щоб не губити порожні рядки.
    this.formControl.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((value) => {
      if (JSON.stringify(serializeKeyboard(this.state())) !== JSON.stringify(value)) this.state.set(parseKeyboard(value));
    });
  }

  protected issueText(issue: KeyboardIssue): string {
    switch (issue) {
      case 'empty-text':
        return $localize`:@@kb.issue.emptyText:У кнопки немає тексту.`;
      case 'empty-callback':
        return $localize`:@@kb.issue.emptyCallback:У кнопки немає даних для бота (callback_data).`;
      case 'long-callback':
        return $localize`:@@kb.issue.longCallback:callback_data довший за 64 байти — Telegram відхилить повідомлення.`;
      case 'bad-url':
        return $localize`:@@kb.issue.badUrl:Посилання має починатися з http://, https:// або tg://.`;
      case 'long-row':
        return $localize`:@@kb.issue.longRow:В інлайн-рядку не більше ${MAX_INLINE_ROW}:max: кнопок.`;
      case 'too-many':
        return $localize`:@@kb.issue.tooMany:Усього не більше 100 кнопок.`;
    }
  }

  protected setMode(mode: KeyboardMode): void {
    const current = this.state();
    if (current.mode === mode) return;
    // Режим «Код» не пропонується кнопкою: у нього потрапляють лише значення, які редактор не розуміє.
    this.write({ ...current, mode, raw: mode === 'raw' ? current.raw : undefined });
  }

  protected setFlag(flag: 'resize' | 'oneTime', value: boolean): void {
    this.write({ ...this.state(), [flag]: value });
  }

  protected setRaw(text: string): void {
    const trimmed = text.trim();
    this.write({ ...this.state(), mode: 'raw', raw: trimmed === '' ? undefined : parseRaw(trimmed) });
  }

  protected addRow(): void {
    const m = this.state();
    this.write(m.mode === 'inline' ? { ...m, inline: [...m.inline, [newInline()]] } : { ...m, reply: [...m.reply, [newReply()]] });
  }

  protected removeRow(r: number): void {
    const m = this.state();
    this.write(m.mode === 'inline' ? { ...m, inline: m.inline.filter((_, i) => i !== r) } : { ...m, reply: m.reply.filter((_, i) => i !== r) });
  }

  protected moveRow(r: number, delta: number): void {
    const m = this.state();
    this.write(m.mode === 'inline' ? { ...m, inline: moveItem(m.inline, r, delta) } : { ...m, reply: moveItem(m.reply, r, delta) });
  }

  protected addButton(r: number): void {
    const m = this.state();
    this.write(
      m.mode === 'inline'
        ? { ...m, inline: m.inline.map((row, i) => (i === r ? [...row, newInline()] : row)) }
        : { ...m, reply: m.reply.map((row, i) => (i === r ? [...row, newReply()] : row)) },
    );
  }

  protected removeButton(r: number, c: number): void {
    const m = this.state();
    this.write(
      m.mode === 'inline'
        ? { ...m, inline: m.inline.map((row, i) => (i === r ? row.filter((_, j) => j !== c) : row)) }
        : { ...m, reply: m.reply.map((row, i) => (i === r ? row.filter((_, j) => j !== c) : row)) },
    );
  }

  protected moveButton(r: number, c: number, delta: number): void {
    const m = this.state();
    this.write(
      m.mode === 'inline'
        ? { ...m, inline: m.inline.map((row, i) => (i === r ? moveItem(row, c, delta) : row)) }
        : { ...m, reply: m.reply.map((row, i) => (i === r ? moveItem(row, c, delta) : row)) },
    );
  }

  protected editInline(r: number, c: number, patch: { text?: string; action?: InlineAction; value?: string }): void {
    const m = this.state();
    this.write({ ...m, inline: m.inline.map((row, i) => (i === r ? row.map((b, j) => (j === c ? ({ ...b, ...patch } as InlineButton) : b)) : row)) });
  }

  protected editReply(r: number, c: number, patch: { text?: string; request?: ReplyRequest }): void {
    const m = this.state();
    this.write({ ...m, reply: m.reply.map((row, i) => (i === r ? row.map((b, j) => (j === c ? ({ ...b, ...patch } as ReplyButton) : b)) : row)) });
  }

  private write(model: KeyboardModel): void {
    this.state.set(model);
    this.formControl.setValue(serializeKeyboard(model));
    this.formControl.markAsDirty();
  }
}

function newInline(): InlineButton {
  return { text: '', action: 'callback', value: '' };
}

function newReply(): ReplyButton {
  return { text: '', request: 'none' };
}

function parseRaw(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/** Таблиця команд меню бота (F6-30, `x-openbp-widget: commands`): «команда — опис», додавання й видалення рядків. */
@Component({
  selector: 'app-node-commands-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <div class="cm" [attr.data-field]="key">
      <span class="cm__label">{{ label }}</span>
      @if (rows(); as list) {
        <table class="cm__table" data-testid="cm-table">
          <thead>
            <tr>
              <th i18n="@@cm.command">Команда</th>
              <th i18n="@@cm.description">Опис у меню</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (row of list; track $index; let i = $index) {
              <tr [attr.data-command]="i">
                <td>
                  <span class="cm__slash">/</span>
                  <input class="cm__input" [attr.data-testid]="'cm-command-' + i" [value]="row.command" placeholder="start" i18n-aria-label="@@cm.command" aria-label="Команда" (change)="edit(i, { command: $any($event.target).value })" />
                </td>
                <td>
                  <input class="cm__input cm__input--wide" [attr.data-testid]="'cm-description-' + i" [value]="row.description" i18n-placeholder="@@cm.descriptionHint" placeholder="Головне меню" i18n-aria-label="@@cm.description" aria-label="Опис у меню" (change)="edit(i, { description: $any($event.target).value })" />
                </td>
                <td>
                  <button type="button" class="cm__icon" [attr.data-testid]="'cm-remove-' + i" i18n-title="@@cm.remove" title="Видалити команду" (click)="remove(i)">✕</button>
                </td>
              </tr>
              @if (issueOf(row); as issue) {
                <tr class="cm__issue" data-testid="cm-issue">
                  <td colspan="3">{{ issue === 'command' ? commandIssueText : descriptionIssueText }}</td>
                </tr>
              }
            }
          </tbody>
        </table>
        <button mat-stroked-button type="button" data-testid="cm-add" (click)="add()" i18n="@@cm.add">Додати команду</button>
      } @else {
        <textarea class="cm__raw" rows="3" data-testid="cm-raw" [value]="rawText()" i18n-aria-label="@@cm.raw" aria-label="Команди" (change)="setRaw($any($event.target).value)"></textarea>
        <p class="cm__hint" i18n="@@cm.rawHint">Значення не є списком команд (наприклад, вираз), тому показано як є.</p>
      }
    </div>
  `,
  styles: `
    .cm { display: flex; flex-direction: column; gap: 8px; margin: 0 0 12px; font: var(--bp-font-p3); }
    .cm__table { border-collapse: collapse; }
    .cm__table th, .cm__table td { padding: 4px; border: 1px solid var(--ac-line); text-align: left; }
    .cm__table th { background: var(--ac-cool-grey); font-weight: 500; }
    .cm__slash { color: var(--ac-cool-grey-4); }
    .cm__input { min-width: 100px; min-height: 28px; padding: 0 6px; border: 1px solid var(--ac-line); border-radius: var(--ac-radius-sm); background: var(--ac-white); font: inherit; }
    .cm__input--wide { min-width: 200px; }
    .cm__icon { padding: 2px 6px; border: none; background: none; color: var(--ac-vivid); cursor: pointer; }
    .cm__issue td { border: none; color: var(--ac-red, #c62828); font-size: 12px; }
    .cm__raw { width: 100%; font: 12px/16px monospace; }
    .cm__hint { margin: 0; color: var(--ac-cool-grey-4); font-size: 12px; }
  `,
})
export class NodeCommandsFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  protected readonly rows = signal<BotCommandRow[] | null>([]);
  private readonly value = signal<unknown>(undefined);
  protected readonly rawText = computed(() => {
    const v = this.value();
    return typeof v === 'string' ? v : v === undefined ? '' : JSON.stringify(v, null, 2);
  });
  protected readonly commandIssueText = $localize`:@@cm.issue.command:Команда — 1–32 малі латинські літери, цифри або _, без «/».`;
  protected readonly descriptionIssueText = $localize`:@@cm.issue.description:Опис — від 3 до 256 символів.`;

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  ngOnInit(): void {
    this.read(this.formControl.value);
    this.formControl.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((v) => {
      if (JSON.stringify(serializeCommands(this.rows() ?? [])) !== JSON.stringify(v)) this.read(v);
    });
  }

  private read(value: unknown): void {
    this.value.set(value);
    this.rows.set(parseCommands(value));
  }

  protected issueOf(row: BotCommandRow): 'command' | 'description' | null {
    return row.command === '' && row.description === '' ? null : commandIssue(row);
  }

  protected add(): void {
    this.write([...(this.rows() ?? []), { command: '', description: '' }]);
  }

  protected remove(i: number): void {
    this.write((this.rows() ?? []).filter((_, j) => j !== i));
  }

  protected edit(i: number, patch: Partial<BotCommandRow>): void {
    this.write((this.rows() ?? []).map((r, j) => (j === i ? { ...r, ...patch } : r)));
  }

  protected setRaw(text: string): void {
    const trimmed = text.trim();
    const value = trimmed === '' ? undefined : parseRaw(trimmed);
    this.value.set(value);
    this.formControl.setValue(value);
    this.formControl.markAsDirty();
    this.rows.set(parseCommands(value));
  }

  private write(rows: BotCommandRow[]): void {
    this.rows.set(rows);
    this.formControl.setValue(serializeCommands(rows));
    this.formControl.markAsDirty();
  }
}

/** Множинний вибір із переліку варіантів (`type: array` з `items.enum`): прапорці, значення — масив рядків. */
@Component({
  selector: 'app-node-multi-choice-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="mc" [attr.data-field]="key">
      <legend class="mc__label">{{ label }}</legend>
      <div class="mc__options">
        @for (o of choices; track o) {
          <label class="mc__option">
            <input type="checkbox" [attr.data-testid]="'multi-' + key + '-' + o" [checked]="selected().has(o)" (change)="toggle(o, $any($event.target).checked)" />
            <span>{{ o }}</span>
          </label>
        }
      </div>
      @if (description) {
        <p class="mc__hint">{{ description }}</p>
      }
    </fieldset>
  `,
  styles: `
    .mc { margin: 0 0 12px; padding: 0; border: none; font: var(--bp-font-p3); }
    .mc__label { padding: 0; font-size: 13px; }
    .mc__options { display: flex; flex-wrap: wrap; gap: 4px 14px; margin-top: 4px; }
    .mc__option { display: flex; gap: 4px; align-items: center; }
    .mc__hint { margin: 4px 0 0; color: var(--ac-cool-grey-4); font-size: 12px; }
  `,
})
export class NodeMultiChoiceFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  private readonly value = signal<unknown>(undefined);
  protected readonly selected = computed(() => new Set(Array.isArray(this.value()) ? (this.value() as unknown[]).map(String) : []));

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  protected get description(): string | undefined {
    return (this.props as NodeFieldProps).description;
  }

  protected get choices(): string[] {
    return ((this.props as NodeFieldProps).rules?.options ?? []).map(String);
  }

  ngOnInit(): void {
    this.value.set(this.formControl.value);
    this.formControl.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((v) => this.value.set(v));
  }

  protected toggle(option: string, on: boolean): void {
    const next = toggleChoice(this.value(), option, this.choices, on);
    this.value.set(next);
    this.formControl.setValue(next);
    this.formControl.markAsDirty();
  }
}
