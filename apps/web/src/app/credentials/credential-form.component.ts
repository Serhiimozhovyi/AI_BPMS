import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Observable } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { ProjectContextService } from '../projects/project-context.service';
import {
  CredentialFormValue,
  PairValue,
  buildData,
  emptyPair,
  formFields,
  initialValue,
  isFilled,
} from './credential-input';
import { CredentialSummary, CredentialTypeInfo, CredentialsApiService } from './credentials-api.service';

/**
 * Форма credential'а за типом (F4-02): створення (вибір типу → поля) і редагування (перейменування, заміна полів).
 * Значення секретів не показуються ніколи: при редагуванні заповнене поле позначене «заповнено», порожнє поле
 * лишає збережене значення. Використовується на сторінці `/credentials` і в діалозі конектора (F4-03).
 */
@Component({
  selector: 'app-credential-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <!-- Не <form>: компонент вкладається у форму конектора (F4-03), а вкладені форми HTML не дозволяє. -->
    <div class="flex flex-col gap-2" data-testid="credential-form">
      <mat-form-field appearance="outline">
        <mat-label i18n="@@credentials.form.name">Назва</mat-label>
        <input matInput name="name" data-testid="credential-name" [value]="name()" (input)="name.set(text($event))" />
      </mat-form-field>

      @if (!credential() && fixedProjectId() === undefined && targets().length > 1) {
        <mat-form-field appearance="outline">
          <mat-label i18n="@@credentials.form.project">Проєкт</mat-label>
          <mat-select data-testid="credential-project" [value]="projectId()" (selectionChange)="projectId.set($event.value)">
            @for (t of targets(); track t.id) {
              <mat-option [value]="t.id" [attr.data-project]="t.id ?? 'shared'">{{ t.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }

      @if (!credential() && availableTypes().length > 1) {
        <mat-form-field appearance="outline">
          <mat-label i18n="@@credentials.form.type">Тип</mat-label>
          <mat-select data-testid="credential-type" [value]="typeName()" (selectionChange)="selectType($event.value)">
            @for (t of availableTypes(); track t.type) {
              <mat-option [value]="t.type" [attr.data-type]="t.type">{{ t.displayName }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }

      @if (type(); as t) {
        <p class="text-sm opacity-70" data-testid="credential-type-description">{{ t.description }}</p>
        @if (credential()) {
          <p class="text-sm opacity-70" i18n="@@credentials.form.keepHint">
            Збережені значення не показуються. Залиште поле порожнім, щоб не змінювати його.
          </p>
        }
        @for (f of fields(); track f.name) {
          @if (f.kind === 'secretMap') {
            <fieldset class="rounded border border-ac-line p-3" [attr.data-map]="f.name">
              @if (f.title) {
                <legend class="px-1 text-sm font-medium">{{ f.title }}</legend>
              }
              @for (p of value().maps[f.name]; track $index; let i = $index) {
                <div class="flex items-start gap-2" [attr.data-pair]="i" [class.opacity-50]="p.removed">
                  <mat-form-field appearance="outline" class="flex-1">
                    <mat-label i18n="@@credentials.form.pairKey">Назва</mat-label>
                    <input
                      matInput
                      data-testid="pair-key"
                      [value]="p.key"
                      [readonly]="p.existing"
                      (input)="setPair(f.name, i, { key: text($event) })"
                    />
                  </mat-form-field>
                  <mat-form-field appearance="outline" class="flex-1">
                    <mat-label i18n="@@credentials.form.pairValue">Значення</mat-label>
                    <input
                      matInput
                      type="password"
                      autocomplete="new-password"
                      data-testid="pair-value"
                      [value]="p.value"
                      [disabled]="p.removed"
                      [placeholder]="p.existing ? filledLabel : ''"
                      (input)="setPair(f.name, i, { value: text($event) })"
                    />
                    @if (p.existing) {
                      <mat-hint i18n="@@credentials.form.filled">заповнено</mat-hint>
                    }
                  </mat-form-field>
                  <button mat-button type="button" data-testid="pair-remove" (click)="togglePair(f.name, i)">
                    @if (p.removed) {
                      <span i18n="@@credentials.form.restore">Повернути</span>
                    } @else {
                      <span i18n="@@credentials.form.remove">Прибрати</span>
                    }
                  </button>
                </div>
              }
              <button mat-stroked-button type="button" data-testid="pair-add" (click)="addPair(f.name)" i18n="@@credentials.form.addPair">
                Додати пару
              </button>
            </fieldset>
          } @else {
            <mat-form-field appearance="outline" [attr.data-field]="f.name">
              <mat-label>{{ f.title }}</mat-label>
              <input
                matInput
                [attr.data-testid]="'field-' + f.name"
                [type]="f.kind === 'secret' ? 'password' : 'text'"
                [autocomplete]="f.kind === 'secret' ? 'new-password' : 'off'"
                [value]="value().fields[f.name]"
                [placeholder]="filled(f.name) ? filledLabel : ''"
                (input)="setField(f.name, text($event))"
              />
              @if (filled(f.name)) {
                <mat-hint i18n="@@credentials.form.filled">заповнено</mat-hint>
              }
            </mat-form-field>
          }
        }
      }

      @for (e of errors(); track e) {
        <p role="alert" class="text-sm text-ac-red" data-testid="form-error">{{ e }}</p>
      }

      <div class="flex gap-2">
        <button mat-flat-button type="button" data-testid="credential-save" (click)="save()" [disabled]="saving() || !type()" i18n="@@credentials.form.save">
          Зберегти
        </button>
        <button mat-button type="button" data-testid="credential-cancel" (click)="cancelled.emit()" i18n="@@credentials.form.cancel">
          Скасувати
        </button>
      </div>
    </div>
  `,
})
export class CredentialFormComponent implements OnInit {
  private readonly api = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly context = inject(ProjectContextService);
  private readonly auth = inject(AuthService);

  /** Каталог типів (сторінка завантажує його один раз). */
  readonly types = input.required<CredentialTypeInfo[]>();
  /** Credential для редагування; `null` — створення. */
  readonly credential = input<CredentialSummary | null>(null);
  /** Дозволені типи для створення (напр. лише `mcp.http` у формі конектора); `null` — усі. */
  readonly allowedTypes = input<string[] | null>(null);
  /** Проєкт, заданий батьківською формою (конектор, F6-14): вибору немає; `undefined` — вибирає користувач. */
  readonly fixedProjectId = input<string | null | undefined>(undefined);

  readonly saved = output<CredentialSummary>();
  readonly cancelled = output<void>();

  readonly filledLabel = $localize`:@@credentials.form.filledPlaceholder:•••••• (заповнено)`;

  readonly name = signal('');
  readonly typeName = signal<string | null>(null);
  readonly value = signal<CredentialFormValue>({ fields: {}, maps: {} });
  readonly errors = signal<string[]>([]);
  readonly saving = signal(false);
  /** Проєкт нового credential'а (F6-14); `null` — спільний. */
  readonly projectId = signal<string | null>(null);
  readonly targets = computed(() => this.context.targets(this.auth.user()?.role === 'admin'));

  readonly availableTypes = computed(() => {
    const allowed = this.allowedTypes();
    return allowed ? this.types().filter((t) => allowed.includes(t.type)) : this.types();
  });
  readonly type = computed(() => this.types().find((t) => t.type === this.typeName()) ?? null);
  readonly fields = computed(() => {
    const t = this.type();
    return t ? formFields(t) : [];
  });

  ngOnInit(): void {
    const fixed = this.fixedProjectId();
    this.projectId.set(fixed !== undefined ? fixed : this.context.initialTarget(this.auth.user()?.role === 'admin'));
    const existing = this.credential();
    if (existing) {
      this.name.set(existing.name);
      this.selectType(existing.type);
    } else if (this.availableTypes().length === 1 || (this.allowedTypes() && this.availableTypes().length > 0)) {
      this.selectType(this.availableTypes()[0].type);
    }
  }

  selectType(typeName: string): void {
    this.typeName.set(typeName);
    // Невідомий тип (рядок, створений до F4-01) редагується як «довільні пари».
    const t = this.type() ?? this.types().find((x) => x.fields.length === 0);
    if (t && !this.type()) this.typeName.set(t.type);
    this.value.set(t ? initialValue(t, this.credential()) : { fields: {}, maps: {} });
    this.errors.set([]);
  }

  filled(field: string): boolean {
    return isFilled(this.credential(), field);
  }

  text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  setField(name: string, v: string): void {
    this.value.update((s) => ({ ...s, fields: { ...s.fields, [name]: v } }));
  }

  setPair(map: string, index: number, change: Partial<PairValue>): void {
    this.value.update((s) => ({
      ...s,
      maps: { ...s.maps, [map]: s.maps[map].map((p, i) => (i === index ? { ...p, ...change } : p)) },
    }));
  }

  togglePair(map: string, index: number): void {
    const pair = this.value().maps[map][index];
    if (pair.existing) {
      this.setPair(map, index, { removed: !pair.removed, value: '' });
    } else {
      this.value.update((s) => ({ ...s, maps: { ...s.maps, [map]: s.maps[map].filter((_, i) => i !== index) } }));
    }
  }

  addPair(map: string): void {
    this.value.update((s) => ({ ...s, maps: { ...s.maps, [map]: [...s.maps[map], emptyPair()] } }));
  }

  save(): void {
    const t = this.type();
    if (!t) return;
    const name = this.name().trim();
    const existing = this.credential();
    const { data, errors } = buildData(t, this.value(), existing);
    if (!name) errors.unshift($localize`:@@credentials.form.nameRequired:Вкажіть назву.`);
    this.errors.set(errors);
    if (errors.length > 0) return;

    let request: Observable<CredentialSummary>;
    if (existing) {
      const changes: { name?: string; data?: typeof data } = {};
      if (name !== existing.name) changes.name = name;
      if (data) changes.data = data;
      if (!changes.name && !changes.data) {
        this.saved.emit(existing);
        return;
      }
      request = this.api.update(existing.id, changes as { name?: string; data?: NonNullable<typeof data> });
    } else {
      request = this.api.create(name, t.type, data ?? {}, this.projectId());
    }

    this.saving.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (c) => {
        this.saving.set(false);
        this.saved.emit(c);
      },
      error: (e: unknown) => {
        this.saving.set(false);
        this.errors.set(fieldErrors(e) ?? [apiErrorMessage(e)]);
      },
    });
  }
}

/** Помилки полів із `400 CREDENTIAL_INVALID` (`errors: [{ field, message }]`). */
function fieldErrors(e: unknown): string[] | null {
  const body = (e as { error?: { errors?: { field: string; message: string }[] } } | null)?.error;
  return Array.isArray(body?.errors) && body.errors.length > 0 ? body.errors.map((x) => `${x.field}: ${x.message}`) : null;
}
