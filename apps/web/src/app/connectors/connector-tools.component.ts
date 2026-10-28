import { JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, linkedSignal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { argumentsSkeleton, parseArguments } from './connector-input';
import { ConnectorsApiService, McpToolInfo, ToolCallResult, apiErrorMessage } from './connectors-api.service';

/** Стан тестового виклику одного tool'а. */
interface CallState {
  args: string;
  calling: boolean;
  result: ToolCallResult | null;
  error: string | null;
}

/** Перелік tools конектора зі схемами і тестовим викликом (`POST /connectors/{id}/tools/{tool}/call`). */
@Component({
  selector: 'app-connector-tools',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [JsonPipe, MatExpansionModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    @if (tools().length === 0) {
      <p class="opacity-70" data-testid="no-tools" i18n="@@connectors.tools.empty">
        Дії ще не отримано. Натисніть «Перевірити з'єднання».
      </p>
    } @else {
      <mat-accordion multi>
        @for (tool of tools(); track tool.name) {
          <mat-expansion-panel [attr.data-tool]="tool.name" (opened)="ensureState(tool)">
            <mat-expansion-panel-header>
              <mat-panel-title class="font-mono">{{ tool.name }}</mat-panel-title>
              <mat-panel-description>{{ tool.title || tool.description }}</mat-panel-description>
            </mat-expansion-panel-header>

            @if (tool.description) {
              <p class="mb-2">{{ tool.description }}</p>
            }
            <h4 class="font-semibold" i18n="@@connectors.tools.inputSchema">Схема аргументів</h4>
            <pre class="mb-2 overflow-auto text-xs" data-testid="input-schema">{{ tool.inputSchema | json }}</pre>
            @if (tool.outputSchema) {
              <h4 class="font-semibold" i18n="@@connectors.tools.outputSchema">Схема результату</h4>
              <pre class="mb-2 overflow-auto text-xs">{{ tool.outputSchema | json }}</pre>
            }

            @let s = state()[tool.name];
            @if (s) {
              <mat-form-field appearance="outline" class="w-full">
                <mat-label i18n="@@connectors.tools.arguments">Аргументи (JSON)</mat-label>
                <textarea
                  matInput
                  rows="5"
                  class="font-mono"
                  data-testid="call-args"
                  [value]="s.args"
                  (input)="setArgs(tool.name, $any($event.target).value)"
                ></textarea>
              </mat-form-field>
              <button
                mat-stroked-button
                type="button"
                data-testid="call-button"
                [disabled]="s.calling"
                (click)="call(tool.name)"
                i18n="@@connectors.tools.call"
              >
                Тестовий виклик
              </button>

              @if (s.error) {
                <p role="alert" class="mt-2 text-red-700" data-testid="call-error">{{ s.error }}</p>
              }
              @if (s.result; as r) {
                <div class="mt-2" data-testid="call-result">
                  @if (r.isError) {
                    <p class="text-red-700" i18n="@@connectors.tools.result.isError">Сервер повернув помилку</p>
                  } @else {
                    <p class="text-green-700" i18n="@@connectors.tools.result.ok">Виклик успішний</p>
                  }
                  <p class="text-xs opacity-70" i18n="@@connectors.tools.result.elapsed">Тривалість: {{ r.elapsed }}</p>
                  @if (r.structuredContent) {
                    <pre class="overflow-auto text-xs">{{ r.structuredContent | json }}</pre>
                  }
                  @for (block of r.content; track $index) {
                    <pre class="overflow-auto text-xs">{{ block['type'] === 'text' ? block['text'] : (block | json) }}</pre>
                  }
                </div>
              }
            }
          </mat-expansion-panel>
        }
      </mat-accordion>
    }
  `,
})
export class ConnectorToolsComponent {
  private readonly api = inject(ConnectorsApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly connectorId = input.required<string>();
  readonly tools = input.required<McpToolInfo[]>();

  /** Стан тестових викликів за іменем tool'а; скидається при зміні конектора. */
  readonly state = linkedSignal<string, Record<string, CallState>>({
    source: this.connectorId,
    computation: () => ({}),
  });

  ensureState(tool: McpToolInfo): void {
    if (this.state()[tool.name]) return;
    this.patch(tool.name, {
      args: JSON.stringify(argumentsSkeleton(tool.inputSchema), null, 2),
      calling: false,
      result: null,
      error: null,
    });
  }

  setArgs(name: string, args: string): void {
    this.patch(name, { args });
  }

  call(name: string): void {
    const current = this.state()[name];
    if (!current || current.calling) return;
    const args = parseArguments(current.args);
    if (args === null) {
      this.patch(name, {
        error: $localize`:@@connectors.tools.arguments.invalid:Аргументи мають бути JSON-об'єктом.`,
        result: null,
      });
      return;
    }
    this.patch(name, { calling: true, error: null, result: null });
    this.api
      .callTool(this.connectorId(), name, args)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.patch(name, { calling: false, result }),
        error: (e: unknown) => this.patch(name, { calling: false, error: apiErrorMessage(e) }),
      });
  }

  private patch(name: string, change: Partial<CallState>): void {
    this.state.update((all) => ({
      ...all,
      [name]: { ...(all[name] ?? { args: '{}', calling: false, result: null, error: null }), ...change },
    }));
  }
}
