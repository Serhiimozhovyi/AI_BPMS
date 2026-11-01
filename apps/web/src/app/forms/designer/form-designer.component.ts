import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDragPlaceholder, CdkDropList } from '@angular/cdk/drag-drop';
import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { BpButtonDirective } from '../../shared/button.directive';
import { SelectSearchComponent } from '../../shared/select-search/select-search.component';
import { BpTab, BpTabsComponent } from '../../shared/tabs/tabs.component';
import { FormPreviewComponent } from './form-preview.component';
import { RuleSource, VisibleIfEditorComponent } from './visible-if-editor.component';
import type { JsonSchema } from '../form-fields';
import { MAX_ROW_CELLS, UI_KEY, VisibleIf, rowWidths } from '../form-layout';
import {
  DesignerHistory,
  DesignerState,
  FIELD_KINDS,
  FieldKind,
  OptionRow,
  addField,
  optionsOf as propertyOptions,
  ruleSources,
  rulesUsing,
  setFieldVisibleIf,
  widgetOf,
  widgetsFor,
  addSection,
  duplicateField,
  fromSchema,
  keyError,
  kindOf,
  propertyForColumn,
  TableColumn,
  addAllColumns,
  addColumn,
  addColumnToRow,
  addFieldToRow,
  columnsNotOnForm,
  canJoinPrevious,
  joinPrevious,
  moveField,
  placeInRow,
  resizeBoundary,
  resizeField,
  setRowWidths,
  splitFromRow,
  widthPresets,
  moveSection,
  nudgeField,
  optionsOf,
  placeOf,
  removeField,
  removeSection,
  renameKey,
  setFieldProps,
  setFieldUi,
  setOptions,
  setRequired,
  setSectionProps,
  toSchema,
} from './form-designer.model';

type Selection = { kind: 'field'; key: string } | { kind: 'section'; index: number } | null;
type Mode = 'design' | 'preview' | 'json';

/** Заголовок, опис і кнопка вузла для вкладки «Перегляд» (F11-04). */
export interface DesignerPreviewMeta {
  title?: string;
  description?: string;
  submitLabel?: string;
}
/** Місце скидання: рядок `row` секції `section` (для проміжку — індекс нового рядка). */
interface DropSpot {
  section: number;
  row: number;
}

const PALETTE_ID = 'fd-palette';

/** Що тягнуть з палітри: тип нового поля або колонка таблиці (режим таблиці). */
type PaletteItem = { kind: FieldKind } | { column: string };
const GRID_UNITS = 12;

/**
 * Конструктор форми `bp-form-designer` (F11-02, docs/forms-designer.md §3.2): палітра типів полів · канвас із секціями й
 * рядками (форма як на сторінці) · властивості вибраного поля чи секції. Перетягування (F11-03): поле чи тип з палітри
 * у проміжок між рядками — окремим рядком; у рядок — поруч з іншими полями (до 4); межа між полями — ширини з кроком
 * 1/12; секцію — за ручку в заголовку. Клавіатура на картці поля: Alt+↑/↓ — перемістити, Alt+←/→ — ширина в рядку,
 * Delete — видалити, Ctrl+D — дублювати; Ctrl+Z / Ctrl+Shift+Z — скасувати / повторити. У властивостях поля — пресети
 * ширин рядка, «Окремим рядком», «В один рядок із попереднім». Вхід і вихід — JSON Schema форми (`schemaChange` на
 * кожну зміну); режим «JSON» редагує її напряму.
 */
@Component({
  selector: 'bp-form-designer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CdkDrag, CdkDragHandle, CdkDragPlaceholder, CdkDropList, NgTemplateOutlet, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, BpButtonDirective, BpTabsComponent, SelectSearchComponent, FormPreviewComponent, VisibleIfEditorComponent],
  host: { '(keydown)': 'hostKey($event)' },
  template: `
    <div class="fd" data-testid="form-designer">
      <div class="fd__bar">
        @if (!tableMode()) {
          <bp-tabs variant="pills" [tabs]="modes()" [value]="mode()" (valueChange)="setMode($any($event))" label="Режим конструктора" i18n-label="@@fd.modes" testId="fd-mode" />
        }
        <span class="fd__spacer"></span>
        <button mat-icon-button type="button" data-testid="fd-undo" [disabled]="!canUndo()" (click)="undo()" i18n-aria-label="@@fd.undo" aria-label="Скасувати (Ctrl+Z)" i18n-title="@@fd.undo" title="Скасувати (Ctrl+Z)">
          <span class="material-symbols-outlined" aria-hidden="true">undo</span>
        </button>
        <button mat-icon-button type="button" data-testid="fd-redo" [disabled]="!canRedo()" (click)="redo()" i18n-aria-label="@@fd.redo" aria-label="Повторити (Ctrl+Shift+Z)" i18n-title="@@fd.redo" title="Повторити (Ctrl+Shift+Z)">
          <span class="material-symbols-outlined" aria-hidden="true">redo</span>
        </button>
      </div>

      @if (mode() === 'design') {
        <div class="fd__grid">
          <aside class="fd__palette" aria-labelledby="fd-palette-title">
            <h3 id="fd-palette-title" class="fd__heading">{{ tableMode() ? columnsHeading : paletteHeading }}</h3>
            <div
              class="fd__kinds"
              cdkDropList
              [id]="paletteId"
              [cdkDropListConnectedTo]="dropIds()"
              [cdkDropListSortingDisabled]="true"
              [cdkDropListEnterPredicate]="noEnter"
              data-testid="fd-palette"
            >
              @if (tableMode()) {
                @for (c of freeColumns(); track c.name) {
                  <button type="button" class="fd__kind" cdkDrag [cdkDragData]="{ column: c.name }" [attr.data-column]="c.name" [attr.data-testid]="'fd-col-' + c.name" (click)="addColumnClick(c)">
                    <span class="material-symbols-outlined" aria-hidden="true">{{ columnIcon(c) }}</span>
                    <span class="fd__kind-name">{{ c.name }}</span>
                    <span class="fd__kind-type">{{ columnTypeLabel(c.type) }}</span>
                  </button>
                } @empty {
                  <p class="fd__note" data-testid="fd-columns-empty" i18n="@@fd.columns.empty">Усі колонки вже на формі.</p>
                }
              } @else {
                @for (k of kinds; track k.kind) {
                  <button type="button" class="fd__kind" cdkDrag [cdkDragData]="{ kind: k.kind }" [attr.data-kind]="k.kind" [attr.data-testid]="'fd-kind-' + k.kind" (click)="add(k.kind)">
                    <span class="material-symbols-outlined" aria-hidden="true">{{ k.icon }}</span>
                    <span>{{ k.label }}</span>
                  </button>
                }
              }
            </div>
            @if (tableMode() && freeColumns().length > 1) {
              <button mat-stroked-button type="button" class="fd__add-section" data-testid="fd-add-all" (click)="addAll()" i18n="@@fd.columns.addAll">Додати всі</button>
            }
            <button mat-stroked-button type="button" class="fd__add-section" bpVariant="tonal" data-testid="fd-add-section" (click)="newSection()">
              <span class="material-symbols-outlined" aria-hidden="true">add</span>
              <span i18n="@@fd.addSection">Секція</span>
            </button>
          </aside>

          <div class="fd__canvas" cdkDropList [cdkDropListData]="'sections'" (cdkDropListDropped)="sectionDropped($event)" data-testid="fd-canvas">
            @for (section of state().layout.sections; track $index; let si = $index) {
              <section
                class="fd-section"
                cdkDrag
                [cdkDragData]="si"
                [class.fd-section--titled]="!!section.title"
                [class.fd-section--selected]="isSectionSelected(si)"
                data-testid="fd-section"
                [attr.data-section]="si"
              >
                <header class="fd-section__head">
                  <span class="material-symbols-outlined fd__handle" cdkDragHandle i18n-title="@@fd.dragSection" title="Перетягніть секцію" aria-hidden="true">drag_indicator</span>
                  <button type="button" class="fd-section__title" data-testid="fd-section-title" [attr.aria-pressed]="isSectionSelected(si)" (click)="selectSection(si, $event)">{{ section.title || sectionPlaceholder }}</button>
                  @if (section.collapsible) {
                    <span class="material-symbols-outlined fd-section__flag" i18n-title="@@fd.collapsibleFlag" title="Можна згорнути" aria-hidden="true">unfold_less</span>
                  }
                </header>
                @if (section.description) {
                  <p class="fd-section__description">{{ section.description }}</p>
                }
                <div class="fd-rows" data-testid="fd-rows">
                  @for (row of section.rows; track $index; let ri = $index) {
                    <ng-container *ngTemplateOutlet="gap; context: { $implicit: si, row: ri, empty: false }" />
                    @let widths = rowWidthsOf(row);
                    <div
                      class="fd-row"
                      cdkDropList
                      cdkDropListOrientation="horizontal"
                      [id]="rowId(si, ri)"
                      [cdkDropListData]="{ section: si, row: ri }"
                      [cdkDropListConnectedTo]="dropIds()"
                      [cdkDropListEnterPredicate]="canEnterRow"
                      (cdkDropListDropped)="rowDropped($event)"
                      data-testid="fd-row"
                      [attr.data-row]="si + ':' + ri"
                    >
                      @for (cell of row; track cell.field; let ci = $index) {
                        @let p = state().properties[cell.field];
                        <div
                          class="fd-card"
                          cdkDrag
                          [cdkDragData]="cell.field"
                          [style.flex]="widths[ci] + ' 1 0'"
                          [class.fd-card--selected]="isFieldSelected(cell.field)"
                          tabindex="0"
                          role="button"
                          [attr.aria-pressed]="isFieldSelected(cell.field)"
                          [attr.aria-describedby]="'fd-card-help'"
                          [attr.data-field]="cell.field"
                          [attr.data-width]="widths[ci]"
                          [attr.data-testid]="'fd-card-' + cell.field"
                          (click)="selectField(cell.field, $event)"
                          (keydown)="cardKey($event, cell.field)"
                        >
                          <div *cdkDragPlaceholder class="fd-drop-marker"></div>
                          <span class="material-symbols-outlined fd__handle" cdkDragHandle i18n-title="@@fd.dragField" title="Перетягніть поле: між рядками — окремим рядком, у рядок — поруч з іншими" aria-hidden="true">drag_indicator</span>
                          <span class="fd-card__body">
                            <span class="fd-card__label">
                              {{ titleOf(p, cell.field) }}
                              @if (isRequired(cell.field)) {
                                <span class="fd-card__required" aria-hidden="true">*</span>
                              }
                            </span>
                            <span class="fd-card__meta">{{ kindLabel(p) }} · {{ cell.field }}@if (row.length > 1) { · {{ widths[ci] }}/12}</span>
                          </span>
                          @if (ci < row.length - 1) {
                            <span class="fd-resize" data-testid="fd-resize" aria-hidden="true" i18n-title="@@fd.resize" title="Потягніть, щоб змінити ширину" (pointerdown)="startResize($event, si, ri, ci)" (click)="$event.stopPropagation()"></span>
                          }
                        </div>
                      }
                    </div>
                  }
                  <ng-container *ngTemplateOutlet="gap; context: { $implicit: si, row: section.rows.length, empty: !section.rows.length }" />
                </div>
              </section>
            }
          </div>

          <ng-template #gap let-si let-row="row" let-empty="empty">
            <div
              class="fd-gap"
              cdkDropList
              [class.fd-gap--empty]="empty"
              [id]="gapId(si, row)"
              [cdkDropListData]="{ section: si, row: row }"
              [cdkDropListConnectedTo]="dropIds()"
              (cdkDropListDropped)="gapDropped($event)"
              data-testid="fd-gap"
              [attr.data-gap]="si + ':' + row"
            >
              @if (empty) {
                <p class="fd-rows__empty" i18n="@@fd.emptySection">Перетягніть сюди поле з палітри або клацніть тип поля.</p>
              }
            </div>
          </ng-template>
          <p id="fd-card-help" class="fd__sr-only" i18n="@@fd.cardHelp">Alt+стрілки вгору/вниз — перемістити, Alt+стрілки вліво/вправо — ширина в рядку, Delete — видалити, Ctrl+D — дублювати.</p>

          <aside class="fd__props" data-testid="fd-props" aria-labelledby="fd-props-title">
            @if (notice(); as n) {
              <p class="fd__notice" role="status" data-testid="fd-notice">{{ n }}</p>
            }
            @switch (selection()?.kind) {
              @case ('field') {
                @let key = selectedKey()!;
                @let p = state().properties[key];
                @let kind = selectedKind()!;
                <h3 id="fd-props-title" class="fd__heading" i18n="@@fd.fieldProps">Поле</h3>
                <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                  <mat-label i18n="@@fd.prop.title">Підпис</mat-label>
                  <input matInput data-testid="fd-prop-title" [value]="titleOf(p, key)" (input)="setProp(key, 'title', text($event))" />
                </mat-form-field>
                @if (tableMode()) {
                  <p class="fd__note" data-testid="fd-prop-column"><ng-container i18n="@@fd.prop.column">Колонка таблиці:</ng-container> <strong>{{ key }}</strong> · {{ columnTypeOf(key) }}</p>
                } @else {
                  <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                    <mat-label i18n="@@fd.prop.key">Ключ (назва в даних)</mat-label>
                    <input matInput data-testid="fd-prop-key" [value]="keyDraft() ?? key" (input)="keyDraft.set(text($event))" (change)="commitKey(key)" (keydown.enter)="commitKey(key)" />
                    @if (keyDraftError(); as err) {
                      <mat-hint class="fd__error" data-testid="fd-key-error">{{ err }}</mat-hint>
                    }
                  </mat-form-field>
                }
                <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                  <mat-label i18n="@@fd.prop.description">Підказка під полем</mat-label>
                  <input matInput data-testid="fd-prop-description" [value]="str(p['description'])" (input)="setProp(key, 'description', text($event))" />
                </mat-form-field>
                @if (hasPlaceholder(kind)) {
                  <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                    <mat-label i18n="@@fd.prop.placeholder">Текст у порожньому полі</mat-label>
                    <input matInput data-testid="fd-prop-placeholder" [value]="str(ui(p)['placeholder'])" (input)="setUi(key, 'placeholder', text($event))" />
                  </mat-form-field>
                }
                @if (kind === 'textarea' || kind === 'address') {
                  <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                    <mat-label i18n="@@fd.prop.rows">Висота, рядків</mat-label>
                    <input matInput type="number" min="1" max="20" data-testid="fd-prop-rows" [value]="str(ui(p)['rows'])" (input)="setRows(key, $event)" />
                  </mat-form-field>
                }
                @if (widgetsFor(p).length) {
                  <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                    <mat-label i18n="@@fd.prop.widget">Як показати</mat-label>
                    <mat-select data-testid="fd-prop-widget" [value]="widgetOf(p) ?? ''" (selectionChange)="setUi(key, 'widget', $event.value || undefined)">
                      <mat-option value="" i18n="@@fd.widget.list">Випадний список</mat-option>
                      @for (w of widgetsFor(p); track w) {
                        <mat-option [value]="w">{{ widgetLabel(w) }}</mat-option>
                      }
                    </mat-select>
                  </mat-form-field>
                }
                <mat-checkbox data-testid="fd-prop-required" [checked]="isRequired(key)" (change)="toggleRequired(key, $event.checked)" i18n="@@fd.prop.required">Обов'язкове</mat-checkbox>

                @if (!tableMode()) {
                @if (kind === 'text' || kind === 'textarea' || kind === 'address') {
                    <div class="fd__pair">
                      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                        <mat-label i18n="@@fd.prop.minLength">Мін. символів</mat-label>
                        <input matInput type="number" min="0" data-testid="fd-prop-minLength" [value]="str(p['minLength'])" (input)="setNumber(key, 'minLength', $event)" />
                      </mat-form-field>
                      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                        <mat-label i18n="@@fd.prop.maxLength">Макс. символів</mat-label>
                        <input matInput type="number" min="1" data-testid="fd-prop-maxLength" [value]="str(p['maxLength'])" (input)="setNumber(key, 'maxLength', $event)" />
                      </mat-form-field>
                    </div>
                  }
                  @if (kind === 'text') {
                    <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                      <mat-label i18n="@@fd.prop.pattern">Шаблон (регулярний вираз)</mat-label>
                      <input matInput data-testid="fd-prop-pattern" [value]="str(p['pattern'])" (input)="setProp(key, 'pattern', text($event))" />
                    </mat-form-field>
                  }
                  @if (kind === 'number') {
                    <div class="fd__pair">
                      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                        <mat-label i18n="@@fd.prop.minimum">Мінімум</mat-label>
                        <input matInput type="number" data-testid="fd-prop-minimum" [value]="str(p['minimum'])" (input)="setNumber(key, 'minimum', $event)" />
                      </mat-form-field>
                      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                        <mat-label i18n="@@fd.prop.maximum">Максимум</mat-label>
                        <input matInput type="number" data-testid="fd-prop-maximum" [value]="str(p['maximum'])" (input)="setNumber(key, 'maximum', $event)" />
                      </mat-form-field>
                    </div>
                    <mat-checkbox data-testid="fd-prop-integer" [checked]="p['type'] === 'integer'" (change)="setProp(key, 'type', $event.checked ? 'integer' : 'number')" i18n="@@fd.prop.integer">Лише цілі числа</mat-checkbox>
                  }
                  @if (kind === 'select' || kind === 'multiselect') {
                    <h4 class="fd__subheading" i18n="@@fd.options">Варіанти</h4>
                    <div class="fd-options" cdkDropList (cdkDropListDropped)="optionDropped(key, $event)" data-testid="fd-options">
                      @for (o of options(); track $index; let i = $index) {
                        <div class="fd-option" cdkDrag>
                          <span class="material-symbols-outlined fd__handle" cdkDragHandle aria-hidden="true">drag_indicator</span>
                          <input class="fd-option__input" [value]="o.label" [attr.data-testid]="'fd-option-label-' + i" i18n-aria-label="@@fd.option.label" aria-label="Підпис варіанта" (input)="setOption(key, i, 'label', text($event))" />
                          <input class="fd-option__input fd-option__value" [value]="o.value" [attr.data-testid]="'fd-option-value-' + i" i18n-aria-label="@@fd.option.value" aria-label="Значення в даних" (input)="setOption(key, i, 'value', text($event))" />
                          <button mat-icon-button type="button" bpSize="sm" [attr.data-testid]="'fd-option-remove-' + i" i18n-aria-label="@@fd.option.remove" aria-label="Прибрати варіант" (click)="removeOption(key, i)">
                            <span class="material-symbols-outlined" aria-hidden="true">close</span>
                          </button>
                        </div>
                      }
                    </div>
                    <button mat-stroked-button type="button" bpVariant="tonal" data-testid="fd-option-add" (click)="addOption(key)" i18n="@@fd.option.add">Додати варіант</button>
                  }
                  @if (kind === 'boolean') {
                    <mat-checkbox data-testid="fd-prop-default" [checked]="p['default'] === true" (change)="setProp(key, 'default', $event.checked || undefined)" i18n="@@fd.prop.defaultOn">Позначено спочатку</mat-checkbox>
                  } @else if (kind === 'select') {
                    <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                      <mat-label i18n="@@fd.prop.default">Значення за замовчуванням</mat-label>
                      <mat-select data-testid="fd-prop-default" [value]="str(p['default'])" (selectionChange)="setProp(key, 'default', $event.value || undefined)">
                        <bp-select-search />
                        <mat-option value="">—</mat-option>
                        @for (o of options(); track $index) {
                          <mat-option [value]="o.value">{{ o.label }}</mat-option>
                        }
                      </mat-select>
                    </mat-form-field>
                  } @else if (kind !== 'multiselect' && kind !== 'file') {
                    <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                      <mat-label i18n="@@fd.prop.default">Значення за замовчуванням</mat-label>
                      <input matInput data-testid="fd-prop-default" [value]="str(p['default'])" (input)="setDefault(key, kind, text($event))" />
                    </mat-form-field>
                  }
                }
                @if (selectedRow(); as r) {
                  <h4 class="fd__subheading" i18n="@@fd.row">Рядок</h4>
                  @if (r.size > 1) {
                    <div class="fd__presets" role="group" i18n-aria-label="@@fd.row.widths" aria-label="Ширини полів у рядку">
                      @for (preset of r.presets; track $index) {
                        <button
                          mat-stroked-button
                          type="button"
                          bpSize="sm"
                          [bpVariant]="sameWidths(preset, r.widths) ? 'tonal' : undefined"
                          [attr.aria-pressed]="sameWidths(preset, r.widths)"
                          [attr.data-testid]="'fd-row-preset-' + preset.join('-')"
                          (click)="applyPreset(preset)"
                        >
                          {{ presetLabel(preset) }}
                        </button>
                      }
                    </div>
                    <p class="fd__note" i18n="@@fd.row.widthsNote">Ширини — частки з 12. Потягніть межу між полями на канвасі або Alt+←/→ на картці.</p>
                    <button mat-stroked-button type="button" data-testid="fd-split" (click)="split(key)" i18n="@@fd.row.split">Окремим рядком</button>
                  } @else if (canJoin()) {
                    <button mat-stroked-button type="button" data-testid="fd-join" (click)="join(key)" i18n="@@fd.row.join">В один рядок із попереднім</button>
                  } @else {
                    <p class="fd__note" i18n="@@fd.row.alone">Перетягніть поле на інше поле, щоб поставити їх в один рядок (до 4 полів).</p>
                  }
                }
                <bp-visible-if-editor testId="fd-if" [rule]="cellRule(key)" [sources]="fieldSources()" (ruleChange)="setFieldRule(key, $event)" />
                <div class="fd__actions">
                  @if (!tableMode()) {
                    <button mat-stroked-button type="button" data-testid="fd-duplicate" (click)="duplicate(key)" i18n="@@fd.duplicate">Дублювати</button>
                    <button mat-button type="button" bpVariant="danger" data-testid="fd-delete" (click)="remove(key)" i18n="@@fd.delete">Видалити поле</button>
                  } @else {
                    <button mat-button type="button" bpVariant="danger" data-testid="fd-delete" (click)="remove(key)" i18n="@@fd.removeColumn">Прибрати з форми</button>
                  }
                </div>
              }
              @case ('section') {
                @let si = selectedSection()!;
                @let s = state().layout.sections[si];
                <h3 id="fd-props-title" class="fd__heading" i18n="@@fd.sectionProps">Секція</h3>
                <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                  <mat-label i18n="@@fd.section.title">Заголовок</mat-label>
                  <input matInput data-testid="fd-section-prop-title" [value]="s.title ?? ''" (input)="setSection(si, 'title', text($event))" />
                </mat-form-field>
                <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
                  <mat-label i18n="@@fd.section.description">Опис</mat-label>
                  <textarea matInput rows="2" data-testid="fd-section-prop-description" [value]="s.description ?? ''" (input)="setSection(si, 'description', text($event))"></textarea>
                </mat-form-field>
                <mat-checkbox data-testid="fd-section-prop-collapsible" [checked]="!!s.collapsible" (change)="setSection(si, 'collapsible', $event.checked)" i18n="@@fd.section.collapsible">Можна згорнути</mat-checkbox>
                @if (s.collapsible) {
                  <mat-checkbox data-testid="fd-section-prop-collapsed" [checked]="!!s.collapsed" (change)="setSection(si, 'collapsed', $event.checked)" i18n="@@fd.section.collapsed">Згорнута спочатку</mat-checkbox>
                }
                <bp-visible-if-editor testId="fd-section-if" [rule]="s.visibleIf" [sources]="sectionSources()" (ruleChange)="setSectionRule(si, $event)" />
                <div class="fd__actions">
                  <button mat-button type="button" bpVariant="danger" data-testid="fd-section-delete" [disabled]="state().layout.sections.length < 2" (click)="deleteSection(si)" i18n="@@fd.section.delete">Видалити секцію</button>
                </div>
                @if (state().layout.sections.length > 1) {
                  <p class="fd__note" i18n="@@fd.section.deleteNote">Поля секції перейдуть у сусідню.</p>
                }
              }
              @default {
                <h3 id="fd-props-title" class="fd__heading" i18n="@@fd.props">Властивості</h3>
                <p class="fd__note" i18n="@@fd.props.hint">Клацніть поле або заголовок секції, щоб налаштувати.</p>
              }
            }
          </aside>
        </div>
      } @else if (mode() === 'preview') {
        <bp-form-preview [schema]="currentSchema()" [title]="preview()?.title ?? ''" [description]="preview()?.description ?? ''" [submitLabel]="preview()?.submitLabel ?? ''" />
      } @else {
        <mat-form-field appearance="outline" class="w-full fd__json" subscriptSizing="dynamic">
          <mat-label i18n="@@fd.json">JSON Schema форми</mat-label>
          <textarea matInput rows="22" spellcheck="false" data-testid="fd-json" [value]="jsonText()" (input)="jsonEdited(text($event))"></textarea>
          @if (jsonError(); as err) {
            <mat-hint class="fd__error" data-testid="fd-json-error">{{ err }}</mat-hint>
          }
        </mat-form-field>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .fd__bar {
      display: flex;
      align-items: center;
      gap: 4px;
      margin-bottom: 12px;
    }
    .fd__spacer {
      flex: 1;
    }
    .fd__grid {
      display: grid;
      grid-template-columns: 200px minmax(0, 1fr) 300px;
      gap: 16px;
      align-items: start;
    }
    @media (max-width: 1023.98px) {
      .fd__grid {
        grid-template-columns: minmax(0, 1fr);
      }
    }
    .fd__heading {
      margin: 0 0 8px;
      font-size: 14px;
      font-weight: 600;
      color: var(--bp-text);
    }
    .fd__subheading {
      margin: 12px 0 6px;
      font-size: 13px;
      font-weight: 600;
      color: var(--bp-text-muted);
    }
    .fd__kinds {
      display: flex;
      flex-direction: column;
      gap: 4px;
      margin-bottom: 12px;
    }
    .fd__kind {
      display: flex;
      align-items: center;
      gap: 8px;
      height: 36px;
      padding: 0 10px;
      border: 1px solid var(--bp-line);
      border-radius: 8px;
      background: var(--bp-white, #fff);
      color: var(--bp-text);
      font: inherit;
      font-size: 14px;
      text-align: left;
      cursor: grab;
    }
    .fd__kind:hover {
      background: var(--bp-bg-2);
    }
    .fd__kind:focus-visible,
    .fd-card:focus-visible {
      outline: 2px solid var(--bp-primary);
      outline-offset: 2px;
    }
    .fd__kind .material-symbols-outlined {
      font-size: 20px;
      color: var(--bp-primary);
    }
    .fd__canvas {
      display: flex;
      flex-direction: column;
      gap: 12px;
      min-height: 200px;
      padding: 16px;
      border-radius: 12px;
      background: var(--bp-bg);
    }
    .fd-section {
      padding: 8px 12px 12px;
      border: 1px dashed var(--bp-line);
      border-radius: 12px;
      background: var(--bp-white, #fff);
    }
    .fd-section--titled {
      border-style: solid;
    }
    .fd-section--selected {
      border-color: var(--bp-primary);
      box-shadow: 0 0 0 3px var(--bp-tonal);
    }
    .fd-section__head {
      display: flex;
      align-items: center;
      gap: 6px;
      min-height: 28px;
      cursor: pointer;
    }
    .fd-section__title {
      flex: 1;
      padding: 2px 4px;
      border: 0;
      border-radius: 6px;
      background: none;
      text-align: left;
      font: inherit;
      cursor: pointer;
      font-size: 15px;
      font-weight: 600;
      color: var(--bp-text);
    }
    .fd-section__flag {
      font-size: 18px;
      color: var(--bp-text-muted);
    }
    .fd-section__description {
      margin: 0 0 6px 30px;
      font-size: 13px;
      color: var(--bp-text-muted);
    }
    .fd-rows {
      display: flex;
      flex-direction: column;
      padding-top: 2px;
    }
    .fd-rows__empty {
      margin: 0;
      padding: 12px;
      border: 1px dashed var(--bp-accent);
      border-radius: 8px;
      font-size: 13px;
      color: var(--bp-text-muted);
      text-align: center;
    }
    /* Проміжок між рядками — місце скидання «окремим рядком». */
    .fd-gap {
      min-height: 10px;
      border-radius: 4px;
    }
    .fd-gap--empty {
      min-height: 48px;
    }
    .fd-gap.cdk-drop-list-receiving {
      background: color-mix(in srgb, var(--bp-accent) 8%, transparent);
    }
    .fd-row {
      display: flex;
      gap: 12px;
      border-radius: 10px;
    }
    .fd-row.cdk-drop-list-dragging {
      outline: 1px dashed var(--bp-accent);
      outline-offset: 3px;
    }
    .fd-drop-marker {
      flex: none;
      align-self: stretch;
      min-width: 4px;
      min-height: 4px;
      border-radius: 2px;
      background: var(--bp-accent);
    }
    .fd-row .fd-drop-marker {
      width: 4px;
    }
    .fd-gap .fd-drop-marker {
      height: 4px;
      margin: 3px 0;
    }
    .fd-card {
      position: relative;
      min-width: 0;
      display: flex;
      align-items: center;
      gap: 6px;
      min-height: 52px;
      padding: 6px 10px 6px 4px;
      border: 1px solid var(--bp-line);
      border-radius: 8px;
      background: var(--bp-bg);
      cursor: pointer;
    }
    .fd-card--selected {
      border-color: var(--bp-primary);
      box-shadow: 0 0 0 3px var(--bp-tonal);
      background: var(--bp-white, #fff);
    }
    .fd-card__body {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }
    .fd-card__label {
      overflow: hidden;
      font-size: 14px;
      font-weight: 500;
      color: var(--bp-text);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .fd-card__required {
      color: var(--bp-danger);
    }
    .fd-card__meta {
      overflow: hidden;
      font-size: 12px;
      color: var(--bp-text-muted);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    /* Межа між полями в рядку: тягнуть, щоб змінити ширини з кроком 1/12. */
    .fd-resize {
      position: absolute;
      top: 8px;
      right: -10px;
      bottom: 8px;
      z-index: 1;
      width: 8px;
      border-radius: 4px;
      cursor: col-resize;
      touch-action: none;
    }
    .fd-resize:hover,
    .fd-resize:active {
      background: var(--bp-accent);
    }
    .fd__presets {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .fd__sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
      white-space: nowrap;
    }
    .fd__handle {
      flex: none;
      font-size: 20px;
      color: var(--bp-text-muted);
      cursor: grab;
    }
    .fd__props {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 12px;
      border: 1px solid var(--bp-line);
      border-radius: 12px;
      background: var(--bp-white, #fff);
    }
    .fd__pair {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 8px;
    }
    .fd__actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 4px;
    }
    .fd__notice {
      margin: 0 0 8px;
      padding: 8px 12px;
      border-radius: 8px;
      font-size: 13px;
      background: var(--bp-tonal);
      border-left: 3px solid var(--bp-warning);
      color: var(--bp-text);
    }
    .fd__note {
      margin: 0;
      font-size: 13px;
      color: var(--bp-text-muted);
    }
    .fd__error {
      color: var(--bp-danger);
    }
    .fd-options {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .fd-option {
      display: flex;
      align-items: center;
      gap: 4px;
      background: var(--bp-white, #fff);
    }
    .fd-option__input {
      flex: 1;
      min-width: 0;
      height: 32px;
      padding: 0 8px;
      border: 1px solid var(--bp-line);
      border-radius: 8px;
      font: inherit;
      font-size: 13px;
    }
    .fd-option__input:focus-visible {
      outline: 2px solid var(--bp-primary);
      outline-offset: 0;
    }
    .fd-option__value {
      flex: 0.7;
      color: var(--bp-text-muted);
    }
    .fd__json textarea {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 13px;
    }
    .cdk-drag-preview {
      box-shadow: 0 4px 16px rgb(12 43 48 / 0.18);
    }
    .cdk-drag-placeholder:not(.fd-drop-marker) {
      opacity: 0.4;
      outline: 2px dashed var(--bp-accent);
    }
    .cdk-drag-animating,
    .fd-row.cdk-drop-list-dragging .fd-card:not(.cdk-drag-placeholder),
    .fd__canvas.cdk-drop-list-dragging .fd-section:not(.cdk-drag-placeholder) {
      transition: transform 200ms cubic-bezier(0, 0, 0.2, 1);
    }
  `,
})
export class FormDesignerComponent {
  /** Схема форми ззовні; зміна ззовні (не наш власний вихід) перечитує конструктор і очищає історію. */
  readonly schema = input<JsonSchema | null>(null);
  readonly schemaChange = output<JsonSchema>();
  /** Є — з'являється вкладка «Перегляд» з цими заголовком, описом і кнопкою (панель вузла). */
  readonly preview = input<DesignerPreviewMeta | null>(null);
  /** Є — режим таблиці (F11-05): поля форми — колонки таблиці; тип і ключ поля задає колонка, палітра — колонки без поля, JSON-режиму немає. */
  readonly columns = input<readonly TableColumn[] | null>(null);

  protected readonly kinds = FIELD_KINDS;
  protected readonly paletteId = PALETTE_ID;
  protected readonly sectionPlaceholder = $localize`:@@fd.section.untitled:Без заголовка`;
  private readonly modeDesign: BpTab = { value: 'design', label: $localize`:@@fd.mode.design:Конструктор`, testId: 'fd-mode-design' };
  private readonly modePreview: BpTab = { value: 'preview', label: $localize`:@@fd.mode.preview:Перегляд`, testId: 'fd-mode-preview' };
  private readonly modeJson: BpTab = { value: 'json', label: 'JSON', testId: 'fd-mode-json' };
  protected readonly modes = computed<BpTab[]>(() => (this.preview() ? [this.modeDesign, this.modePreview, this.modeJson] : [this.modeDesign, this.modeJson]));
  protected readonly currentSchema = computed(() => toSchema(this.state(), { keepOrder: !this.tableMode() }));
  protected readonly tableMode = computed(() => this.columns() !== null);
  protected readonly freeColumns = computed(() => columnsNotOnForm(this.state(), this.columns() ?? []));
  protected readonly paletteHeading = $localize`:@@fd.palette:Додати поле`;
  protected readonly columnsHeading = $localize`:@@fd.columns:Колонки таблиці`;

  protected readonly state = signal<DesignerState>(fromSchema(null));
  protected readonly selection = signal<Selection>(null);
  protected readonly mode = signal<Mode>('design');
  protected readonly keyDraft = signal<string | null>(null);
  protected readonly jsonText = signal('');
  protected readonly jsonError = signal<string | null>(null);
  private readonly historyVersion = signal(0);
  private readonly history = new DesignerHistory<DesignerState>();
  /** Остання видана схема (JSON): не перечитуємо власний вихід, що повернувся через `schema`. */
  private lastEmitted: string | null = null;
  /** Поточна «серія» правок того самого поля: один крок історії на весь набір тексту. */
  private coalesce: string | null = null;

  protected readonly canUndo = computed(() => (this.historyVersion(), this.history.canUndo));
  protected readonly canRedo = computed(() => (this.historyVersion(), this.history.canRedo));
  /** Усі списки скидання полів: рядки (поруч з іншими полями) і проміжки між рядками (окремим рядком). */
  protected readonly dropIds = computed(() =>
    this.state().layout.sections.flatMap((section, si) => [
      ...section.rows.map((_, ri) => this.rowId(si, ri)),
      ...Array.from({ length: section.rows.length + 1 }, (_, ri) => this.gapId(si, ri)),
    ]),
  );
  /** Рядок вибраного поля, якщо в ньому кілька полів: для пресетів ширин. */
  protected readonly selectedRow = computed(() => {
    const key = this.selectedKey();
    const place = key ? placeOf(this.state(), key) : null;
    if (!place) return null;
    const cells = this.state().layout.sections[place.section].rows[place.row];
    return { ...place, size: cells.length, widths: rowWidths(cells), presets: widthPresets(cells.length) };
  });
  protected readonly canJoin = computed(() => {
    const key = this.selectedKey();
    return !!key && canJoinPrevious(this.state(), key);
  });
  protected readonly selectedKey = computed(() => {
    const s = this.selection();
    return s?.kind === 'field' && s.key in this.state().properties ? s.key : null;
  });
  protected readonly notice = signal<string | null>(null);
  protected readonly fieldSources = computed(() => {
    const key = this.selectedKey();
    return key ? this.sourcesOf(ruleSources(this.state(), { field: key })) : [];
  });
  protected readonly sectionSources = computed(() => {
    const index = this.selectedSection();
    return index === null || index === undefined ? [] : this.sourcesOf(ruleSources(this.state(), { section: index }));
  });
  protected readonly selectedSection = computed(() => {
    const s = this.selection();
    return s?.kind === 'section' && s.index < this.state().layout.sections.length ? s.index : null;
  });
  protected readonly selectedKind = computed(() => {
    const key = this.selectedKey();
    return key ? kindOf(this.state().properties[key]) : null;
  });
  protected readonly options = computed(() => {
    const key = this.selectedKey();
    return key ? optionsOf(this.state().properties[key]) : [];
  });
  protected readonly keyDraftError = computed(() => {
    const key = this.selectedKey();
    const draft = this.keyDraft();
    return key && draft !== null ? keyError(this.state(), key, draft) : null;
  });

  constructor() {
    effect(() => {
      const schema = this.schema();
      untracked(() => {
        const json = JSON.stringify(schema ?? null);
        if (json === this.lastEmitted) return;
        this.state.set(fromSchema(schema));
        this.history.clear();
        this.historyVersion.update((v) => v + 1);
        this.selection.set(null);
        this.coalesce = null;
      });
    });
  }

  protected readonly noEnter = () => false;

  protected rowId(section: number, row: number): string {
    return `fd-row-${section}-${row}`;
  }

  protected gapId(section: number, row: number): string {
    return `fd-gap-${section}-${row}`;
  }

  /** У рядок можна скинути поле, якщо в ньому менше 4 полів або поле вже з цього рядка. */
  protected readonly canEnterRow = (drag: CdkDrag<string>, drop: CdkDropList<DropSpot>): boolean => {
    const cells = this.state().layout.sections[drop.data.section]?.rows[drop.data.row] ?? [];
    return cells.length < MAX_ROW_CELLS || cells.some((c) => c.field === drag.data);
  };

  protected presetLabel(widths: readonly number[]): string {
    return widths.join(' / ');
  }

  protected sameWidths(a: readonly number[], b: readonly number[]): boolean {
    return a.length === b.length && a.every((w, i) => w === b[i]);
  }

  protected rowWidthsOf = rowWidths;

  protected text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  protected str(value: unknown): string {
    return value === undefined || value === null ? '' : String(value);
  }

  protected ui(property: JsonSchema): Record<string, unknown> {
    const ui = property[UI_KEY];
    return typeof ui === 'object' && ui !== null && !Array.isArray(ui) ? (ui as Record<string, unknown>) : {};
  }

  protected titleOf(property: JsonSchema | undefined, key: string): string {
    return typeof property?.['title'] === 'string' ? property['title'] : key;
  }

  protected kindLabel(property: JsonSchema | undefined): string {
    const kind = kindOf(property ?? {});
    return FIELD_KINDS.find((k) => k.kind === kind)?.label ?? '';
  }

  protected hasPlaceholder(kind: FieldKind): boolean {
    return ['text', 'textarea', 'number', 'email', 'phone', 'address'].includes(kind);
  }

  protected isRequired(key: string): boolean {
    return this.state().required.includes(key);
  }

  protected isFieldSelected(key: string): boolean {
    const s = this.selection();
    return s?.kind === 'field' && s.key === key;
  }

  protected isSectionSelected(index: number): boolean {
    const s = this.selection();
    return s?.kind === 'section' && s.index === index;
  }

  // ---------- Зміни ----------

  /** Новий стан: крок історії (крім продовження тієї самої серії правок) і вихід. */
  private update(next: DesignerState, series: string | null = null): void {
    const current = this.state();
    if (next === current) return;
    this.notice.set(null);
    if (series === null || series !== this.coalesce) {
      this.history.push(current);
      this.historyVersion.update((v) => v + 1);
    }
    this.coalesce = series;
    this.state.set(next);
    this.emit();
  }

  private emit(): void {
    const schema = toSchema(this.state(), { keepOrder: !this.tableMode() });
    this.lastEmitted = JSON.stringify(schema);
    this.schemaChange.emit(schema);
  }

  protected undo(): void {
    const previous = this.history.undo(this.state());
    if (!previous) return;
    this.restore(previous);
  }

  protected redo(): void {
    const next = this.history.redo(this.state());
    if (!next) return;
    this.restore(next);
  }

  private restore(state: DesignerState): void {
    this.coalesce = null;
    this.historyVersion.update((v) => v + 1);
    this.state.set(state);
    this.keyDraft.set(null);
    const key = this.selectedKey();
    if (this.selection()?.kind === 'field' && !key) this.selection.set(null);
    this.emit();
  }

  protected add(kind: FieldKind): void {
    const target = this.selectedSection() ?? (this.selectedKey() ? placeOf(this.state(), this.selectedKey()!)?.section : undefined);
    const { state, key } = addField(this.state(), kind, target ?? this.state().layout.sections.length - 1);
    this.update(state);
    this.selectField(key);
  }

  protected addColumnClick(column: TableColumn): void {
    const target = this.selectedSection() ?? (this.selectedKey() ? placeOf(this.state(), this.selectedKey()!)?.section : undefined);
    this.update(addColumn(this.state(), column, target ?? this.state().layout.sections.length - 1));
    this.selectField(column.name);
  }

  protected addAll(): void {
    const target = this.selectedSection() ?? (this.selectedKey() ? placeOf(this.state(), this.selectedKey()!)?.section : undefined);
    this.update(addAllColumns(this.state(), this.columns() ?? [], target ?? this.state().layout.sections.length - 1));
  }

  protected columnIcon(column: TableColumn): string {
    return FIELD_KINDS.find((k) => k.kind === kindOf(propertyForColumn(column)))?.icon ?? 'short_text';
  }

  protected columnTypeLabel(type: string): string {
    return type;
  }

  protected columnTypeOf(name: string): string {
    return this.columns()?.find((c) => c.name === name)?.type ?? '';
  }

  protected newSection(): void {
    this.update(addSection(this.state()));
    this.selection.set({ kind: 'section', index: this.state().layout.sections.length - 1 });
  }

  protected remove(key: string): void {
    const order = this.state().layout.sections.flatMap((s) => s.rows.flatMap((r) => r.map((c) => c.field)));
    const index = order.indexOf(key);
    const users = rulesUsing(this.state(), key);
    const names = [
      ...users.fields.map((f) => `«${this.labelOf(f)}»`),
      ...users.sections.map((i) => `«${this.state().layout.sections[i].title || this.sectionPlaceholder}»`),
    ];
    const label = this.labelOf(key);
    this.update(removeField(this.state(), key));
    // Умови інших полів і секцій на видалене поле прибрано (F11-06) — кажемо, де саме.
    if (names.length) this.notice.set($localize`:@@fd.if.removed:Умову «Показувати, якщо» від поля «${label}:label:» прибрано: ${names.join(', ')}:names:.`);
    const next = order[index + 1] ?? order[index - 1];
    if (next && next !== key) this.selectField(next, undefined, true);
    else this.selection.set(null);
  }

  protected duplicate(key: string): void {
    const { state, key: copy } = duplicateField(this.state(), key);
    this.update(state);
    this.selectField(copy, undefined, true);
  }

  protected cellRule(key: string): VisibleIf | undefined {
    const place = placeOf(this.state(), key);
    return place ? this.state().layout.sections[place.section].rows[place.row][place.cell].visibleIf : undefined;
  }

  protected setFieldRule(key: string, rule: VisibleIf | undefined): void {
    this.update(setFieldVisibleIf(this.state(), key, rule));
  }

  protected setSectionRule(index: number, rule: VisibleIf | undefined): void {
    this.update(setSectionProps(this.state(), index, { visibleIf: rule }));
  }

  private labelOf(key: string): string {
    const title = this.state().properties[key]?.['title'];
    return typeof title === 'string' && title ? title : key;
  }

  private sourcesOf(keys: string[]): RuleSource[] {
    return keys.map((key) => {
      const property = this.state().properties[key] ?? {};
      return { key, label: this.labelOf(key), kind: kindOf(property), options: propertyOptions(property) };
    });
  }

  protected deleteSection(index: number): void {
    this.update(removeSection(this.state(), index));
    this.selection.set(null);
  }

  protected setProp(key: string, name: string, value: unknown): void {
    this.update(setFieldProps(this.state(), key, { [name]: value }), `${key}:${name}`);
  }

  protected setUi(key: string, name: string, value: unknown): void {
    this.update(setFieldUi(this.state(), key, { [name]: value }), `${key}:ui.${name}`);
  }

  protected readonly widgetsFor = widgetsFor;
  protected readonly widgetOf = widgetOf;

  protected widgetLabel(widget: 'radio' | 'chips'): string {
    return widget === 'radio' ? $localize`:@@fd.widget.radio:Кнопки (до 6 варіантів)` : $localize`:@@fd.widget.chips:Чипи`;
  }

  protected setRows(key: string, event: Event): void {
    const n = Math.round(Number(this.text(event)));
    this.setUi(key, 'rows', Number.isFinite(n) && n >= 1 && n <= 20 ? n : undefined);
  }

  protected setNumber(key: string, name: string, event: Event): void {
    const raw = this.text(event).trim();
    const value = raw === '' ? undefined : Number(raw);
    if (value !== undefined && !Number.isFinite(value)) return;
    this.setProp(key, name, value);
  }

  protected setDefault(key: string, kind: FieldKind, raw: string): void {
    const value = raw === '' ? undefined : kind === 'number' && Number.isFinite(Number(raw)) ? Number(raw) : raw;
    this.setProp(key, 'default', value);
  }

  protected toggleRequired(key: string, required: boolean): void {
    this.update(setRequired(this.state(), key, required));
  }

  protected commitKey(key: string): void {
    const draft = this.keyDraft();
    if (draft === null || draft === key || keyError(this.state(), key, draft)) return;
    this.update(renameKey(this.state(), key, draft));
    this.keyDraft.set(null);
    this.selection.set({ kind: 'field', key: draft });
  }

  protected setSection(index: number, name: 'title' | 'description' | 'collapsible' | 'collapsed', value: string | boolean): void {
    this.update(setSectionProps(this.state(), index, { [name]: value }), typeof value === 'string' ? `section${index}:${name}` : null);
  }

  // ---------- Варіанти списку ----------

  private writeOptions(key: string, options: OptionRow[], series: string | null = null): void {
    this.update(setOptions(this.state(), key, options), series);
  }

  protected setOption(key: string, index: number, name: 'value' | 'label', value: string): void {
    const options = this.options().map((o, i) => (i === index ? { ...o, [name]: value } : o));
    this.writeOptions(key, options, `${key}:option${index}.${name}`);
  }

  protected addOption(key: string): void {
    const options = this.options();
    const n = options.length + 1;
    let value = `option_${n}`;
    for (let i = n; options.some((o) => o.value === value); i++) value = `option_${i + 1}`;
    this.writeOptions(key, [...options, { value, label: $localize`:@@fd.optionN:Варіант ${n}:n:` }]);
  }

  protected removeOption(key: string, index: number): void {
    this.writeOptions(key, this.options().filter((_, i) => i !== index));
  }

  protected optionDropped(key: string, event: CdkDragDrop<unknown, unknown>): void {
    if (event.previousIndex === event.currentIndex) return;
    const options = [...this.options()];
    const [moved] = options.splice(event.previousIndex, 1);
    options.splice(event.currentIndex, 0, moved);
    this.writeOptions(key, options);
  }

  // ---------- Вибір і клавіатура ----------

  protected selectField(key: string, event?: Event, focus = false): void {
    event?.stopPropagation();
    if (!this.isFieldSelected(key)) this.keyDraft.set(null);
    this.selection.set({ kind: 'field', key });
    this.coalesce = null;
    if (focus) queueMicrotask(() => this.focusCard(key));
  }

  protected selectSection(index: number, event: Event): void {
    event.stopPropagation();
    this.selection.set({ kind: 'section', index });
    this.coalesce = null;
  }

  private focusCard(key: string): void {
    document.querySelector<HTMLElement>(`[data-testid="fd-card-${CSS.escape(key)}"]`)?.focus();
  }

  protected cardKey(event: KeyboardEvent, key: string): void {
    if (event.altKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
      event.preventDefault();
      this.update(nudgeField(this.state(), key, event.key === 'ArrowUp' ? -1 : 1));
      this.selectField(key, undefined, true);
    } else if (event.altKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
      event.preventDefault();
      this.update(resizeField(this.state(), key, event.key === 'ArrowLeft' ? -1 : 1));
      this.selectField(key, undefined, true);
    } else if (event.key === 'Delete') {
      event.preventDefault();
      this.remove(key);
    } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'd') {
      event.preventDefault();
      if (!this.tableMode()) this.duplicate(key);
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.selectField(key);
    }
  }

  /** Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y поза текстовими полями (у полях — рідне скасування набору). */
  protected hostKey(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('input, textarea, [contenteditable="true"]')) return;
    if (!(event.ctrlKey || event.metaKey)) return;
    const k = event.key.toLowerCase();
    if (k === 'z' && !event.shiftKey) {
      event.preventDefault();
      this.undo();
    } else if ((k === 'z' && event.shiftKey) || k === 'y') {
      event.preventDefault();
      this.redo();
    }
  }

  // ---------- Перетягування ----------

  /**
   * Позиція в рядку для поля, що прийшло ззовні: CDK ставить його на місце картки під вказівником (тобто перед нею),
   * а людина, яка скинула на праву половину картки, чекає «після». Порівнюємо точку скидання із серединою картки.
   */
  private entryIndex(event: CdkDragDrop<DropSpot>, section: number, row: number): number {
    const cells = this.state().layout.sections[section]?.rows[row] ?? [];
    const hovered = cells[event.currentIndex];
    if (!hovered) return event.currentIndex;
    const card = document.querySelector<HTMLElement>(`[data-testid="fd-card-${CSS.escape(hovered.field)}"]`);
    if (!card) return event.currentIndex;
    const rect = card.getBoundingClientRect();
    return event.dropPoint.x > rect.left + rect.width / 2 ? event.currentIndex + 1 : event.currentIndex;
  }

  /** Скидання в рядок: поле стає поруч з іншими (у своєму рядку — змінює порядок). */
  protected rowDropped(event: CdkDragDrop<DropSpot>): void {
    const { section, row } = event.container.data;
    if (event.previousContainer.id === PALETTE_ID) {
      const item = event.item.data as PaletteItem;
      if ('column' in item) {
        const column = this.columns()?.find((c) => c.name === item.column);
        if (!column) return;
        this.update(addColumnToRow(this.state(), column, section, row, this.entryIndex(event, section, row)));
        this.selectField(column.name);
        return;
      }
      const { state, key } = addFieldToRow(this.state(), item.kind, section, row, this.entryIndex(event, section, row));
      this.update(state);
      this.selectField(key);
      return;
    }
    const key = event.item.data as string;
    if (event.previousContainer === event.container) {
      if (event.previousIndex === event.currentIndex) return;
      // CDK дає кінцеву позицію; placeInRow чекає позицію в рядку до вилучення поля.
      const index = event.currentIndex > event.previousIndex ? event.currentIndex + 1 : event.currentIndex;
      this.update(placeInRow(this.state(), key, section, row, index));
    } else {
      this.update(placeInRow(this.state(), key, section, row, this.entryIndex(event, section, row)));
    }
    this.selectField(key);
  }

  /** Скидання в проміжок між рядками: поле стає окремим рядком на цьому місці. */
  protected gapDropped(event: CdkDragDrop<DropSpot>): void {
    const { section, row } = event.container.data;
    if (event.previousContainer.id === PALETTE_ID) {
      const item = event.item.data as PaletteItem;
      if ('column' in item) {
        const column = this.columns()?.find((c) => c.name === item.column);
        if (!column) return;
        this.update(addColumn(this.state(), column, section, row));
        this.selectField(column.name);
        return;
      }
      const { state, key } = addField(this.state(), item.kind, section, row);
      this.update(state);
      this.selectField(key);
      return;
    }
    const key = event.item.data as string;
    const from = placeOf(this.state(), key);
    if (!from) return;
    // Якщо поле було саме в рядку вище за місце скидання, після вилучення рядки зсуваються на один.
    const alone = this.state().layout.sections[from.section].rows[from.row].length === 1;
    const index = alone && from.section === section && from.row < row ? row - 1 : row;
    this.update(moveField(this.state(), key, section, index));
    this.selectField(key);
  }

  // ---------- Рядки й ширини ----------

  protected applyPreset(widths: readonly number[]): void {
    const row = this.selectedRow();
    if (!row) return;
    this.update(setRowWidths(this.state(), row.section, row.row, widths));
  }

  protected split(key: string): void {
    this.update(splitFromRow(this.state(), key));
    this.selectField(key, undefined, true);
  }

  protected join(key: string): void {
    this.update(joinPrevious(this.state(), key));
    this.selectField(key, undefined, true);
  }

  /** Перетягування межі між клітинками: крок — 1/12 ширини рядка; один крок історії на весь рух. */
  protected startResize(event: PointerEvent, section: number, row: number, boundary: number): void {
    event.preventDefault();
    event.stopPropagation();
    const rowEl = (event.target as HTMLElement).closest('.fd-row');
    const unit = (rowEl?.getBoundingClientRect().width ?? 0) / GRID_UNITS;
    if (!unit) return;
    const start = this.state();
    const startX = event.clientX;
    const series = `resize:${section}:${row}:${boundary}:${Date.now()}`;
    const move = (e: PointerEvent) => {
      const delta = Math.round((e.clientX - startX) / unit);
      this.update(resizeBoundary(start, section, row, boundary, delta), series);
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
      this.coalesce = null;
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
  }

  protected sectionDropped(event: CdkDragDrop<string>): void {
    if (event.previousContainer !== event.container || event.previousIndex === event.currentIndex) return;
    this.update(moveSection(this.state(), event.previousIndex, event.currentIndex));
    this.selection.set({ kind: 'section', index: event.currentIndex });
  }

  // ---------- JSON ----------

  protected setMode(mode: Mode): void {
    if (mode === this.mode()) return;
    if (mode === 'json') {
      this.jsonText.set(JSON.stringify(toSchema(this.state(), { keepOrder: !this.tableMode() }), null, 2));
      this.jsonError.set(null);
    }
    this.coalesce = null;
    this.mode.set(mode);
  }

  protected jsonEdited(text: string): void {
    this.jsonText.set(text);
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch (e) {
      this.jsonError.set($localize`:@@fd.json.parse:Некоректний JSON: ${(e as Error).message}:message:`);
      return;
    }
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      this.jsonError.set($localize`:@@fd.json.object:Схема форми має бути об'єктом { "type": "object", "properties": { … } }.`);
      return;
    }
    this.jsonError.set(null);
    this.update(fromSchema(parsed as JsonSchema), 'json');
  }
}
