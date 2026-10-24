import { ChangeDetectionStrategy, Component, ElementRef, ViewEncapsulation, booleanAttribute, effect, inject, input, output } from '@angular/core';
import {
  EditorErrorEvent,
  EditorRunEvent,
  EditorSavedEvent,
  WorkflowEditorComponent,
} from '../app/workflow-editor/workflow-editor.component';
import { ElementConfig } from './element-services';

/** Мови інтерфейсу, для яких є переклад; інші значення `locale` — українська. */
const LOCALES = ['uk'];

/**
 * `<openbp-canvas>` (F4-13): редактор сценарію SPA (канвас, палітра, панель вузла, тестовий запуск) без роутера й
 * shell, у Shadow DOM — стилі хоста не діють на канвас і навпаки. Атрибути: `api-base`, `workflow-id`, `readonly`,
 * `locale`, `token`; події: `openbp-saved`, `openbp-run`, `openbp-error` (дані — у `event.detail`). Див. docs/embedding.md.
 */
@Component({
  // Тег на сторінці — `openbp-canvas` (customElements.define у main.ts); selector лише для збірки.
  selector: 'app-openbp-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.ShadowDom,
  imports: [WorkflowEditorComponent],
  template: `
    <div class="openbp" [attr.lang]="lang()">
      @if (workflowId()) {
        <app-workflow-editor
          [workflowId]="workflowId()"
          [readonly]="readonly()"
          [embedded]="true"
          (saved)="saved.emit($event)"
          (executionStarted)="run.emit($event)"
          (failed)="failed.emit($event)"
        />
      } @else {
        <p class="openbp__empty" role="status" i18n="@@element.noWorkflow">Не вказано workflow-id.</p>
      }
    </div>
  `,
  styleUrls: ['./openbp-canvas.scss', '../tailwind.css'],
})
export class OpenbpCanvasElement {
  private readonly config = inject(ElementConfig);

  /** Корінь Api (`https://openbp.example.com`); порожньо — той самий origin. Атрибут `api-base`. */
  readonly apiBase = input('');

  /** Сценарій. Атрибут `workflow-id`. */
  readonly workflowId = input('');

  /** Лише перегляд. Атрибут `readonly` (наявність — увімкнено). */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Мова інтерфейсу (`uk`). */
  readonly locale = input('uk');

  /** API-ключ або OIDC access token (`Authorization: Bearer`); без нього — cookie сесії. */
  readonly token = input('');

  /** `openbp-saved` — `{ workflowId, version }`. */
  // Назва DOM-події елемента — частина публічного API (docs/embedding.md).
  // eslint-disable-next-line @angular-eslint/no-output-rename
  readonly saved = output<EditorSavedEvent>({ alias: 'openbp-saved' });

  /** `openbp-run` — `{ executionId }`. */
  // Назва DOM-події елемента — частина публічного API (docs/embedding.md).
  // eslint-disable-next-line @angular-eslint/no-output-rename
  readonly run = output<EditorRunEvent>({ alias: 'openbp-run' });

  /** `openbp-error` — `{ code, message }`. */
  // Назва DOM-події елемента — частина публічного API (docs/embedding.md).
  // eslint-disable-next-line @angular-eslint/no-output-rename
  readonly failed = output<EditorErrorEvent>({ alias: 'openbp-error' });

  protected lang(): string {
    return LOCALES.includes(this.locale()) ? this.locale() : 'uk';
  }

  constructor() {
    this.config.overlayHost = (inject(ElementRef).nativeElement as HTMLElement).shadowRoot;
    effect(() => {
      this.config.apiBase.set(this.apiBase());
      this.config.token.set(this.token());
    });
  }
}
