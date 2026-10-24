import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { Api } from '../core/api';
import { errorText } from '../core/errors';

interface SettingRow {
    key: string;
    label: string;
    text: boolean;
    value: string;
    saved?: boolean;
    error?: string;
}

const LABELS: Record<string, string> = {
    TEXT_WELCOME: $localize`:@@texts.welcome:Привітання й запит номера`,
    TEXT_MENU: $localize`:@@texts.menu:Головне меню`,
    TEXT_HELP: $localize`:@@texts.help:«Мені потрібна допомога»`,
    TEXT_SOS: $localize`:@@texts.sos:SOS`,
    TEXT_BST_INFO: $localize`:@@texts.bstInfo:Про сеанси БСТ`,
    MIN_HOURS_BEFORE: $localize`:@@texts.minHours:Не пізніше ніж за, год (запис і скасування)`,
    HORIZON_DAYS: $localize`:@@texts.horizon:Горизонт запису, днів`,
    MAX_ACTIVE_CONSULTS: $localize`:@@texts.maxActive:Максимум активних консультацій на людину`,
    SLOT_DURATION_MIN: $localize`:@@texts.slotDuration:Тривалість слота за замовчуванням, хв`,
    BST_CAPACITY_DEFAULT: $localize`:@@texts.bstCapacity:Місткість БСТ за замовчуванням`,
    PSY_PEER_VIEW_DAYS: $localize`:@@texts.peerDays:Скільки днів психологи бачать записи колег`,
};

/** Тексти бота й числові налаштування (`settings`); бот бере їх без перезапуску. */
@Component({
    selector: 'app-texts-page',
    imports: [FormsModule, MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './editor.scss',
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@texts.title">Тексти бота й налаштування</h1>
            <div class="editor">
                @for (row of rows(); track row.key) {
                    <div class="field" [class.wide]="row.text">
                        <label [for]="'setting-' + row.key"
                            >{{ row.label }} <code class="muted">{{ row.key }}</code></label
                        >
                        @if (row.text) {
                            <textarea
                                class="border"
                                rows="4"
                                [id]="'setting-' + row.key"
                                [name]="row.key"
                                [attr.data-testid]="'setting-' + row.key"
                                [(ngModel)]="row.value"
                            ></textarea>
                        } @else {
                            <input
                                class="border"
                                type="number"
                                min="0"
                                [id]="'setting-' + row.key"
                                [name]="row.key"
                                [(ngModel)]="row.value"
                            />
                        }
                        <span class="form-row">
                            <button
                                mat-stroked-button
                                type="button"
                                (click)="save(row)"
                                i18n="@@texts.save"
                            >
                                Зберегти
                            </button>
                            @if (row.saved) {
                                <span class="muted" i18n="@@texts.saved">Збережено</span>
                            }
                            @if (row.error) {
                                <span class="error">{{ row.error }}</span>
                            }
                        </span>
                    </div>
                }
            </div>
        </section>
    `,
})
export class TextsPage implements OnInit {
    private readonly api = inject(Api);
    protected readonly rows = signal<SettingRow[]>([]);

    ngOnInit(): void {
        this.api
            .settings()
            .subscribe((all) =>
                this.rows.set(
                    Object.keys(LABELS).map((key) => ({
                        key,
                        label: LABELS[key],
                        text: key.startsWith('TEXT_'),
                        value: all[key] ?? '',
                    })),
                ),
            );
    }

    protected save(row: SettingRow): void {
        this.api.saveSetting(row.key, String(row.value)).subscribe({
            next: () => this.update(row, { saved: true, error: undefined }),
            error: (e: unknown) => this.update(row, { saved: false, error: errorText(e) }),
        });
    }

    private update(row: SettingRow, patch: Partial<SettingRow>): void {
        this.rows.update((rows) =>
            rows.map((r) => (r.key === row.key ? { ...r, ...patch, value: row.value } : r)),
        );
    }
}
