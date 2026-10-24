import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { Api, Material } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';

interface MaterialForm {
    id: number | null;
    title: string;
    body: string;
    link: string;
    status: Material['status'];
}

const empty = (): MaterialForm => ({ id: null, title: '', body: '', link: '', status: 'draft' });

/** Матеріали бота: читання для всіх, редагування — координатор. */
@Component({
    selector: 'app-materials-page',
    imports: [FormsModule, MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './editor.scss',
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@materials.title">Матеріали</h1>
            @if (error()) {
                <p class="error" role="alert">{{ error() }}</p>
            }
            <table class="grid">
                <thead>
                    <tr>
                        <th i18n="@@materials.name">Назва</th>
                        <th i18n="@@materials.status">Стан</th>
                        <th i18n="@@materials.views">Перегляди</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (m of materials(); track m.id) {
                        <tr>
                            <td>
                                {{ m.title }}
                                @if (m.link) {
                                    <a
                                        [href]="m.link"
                                        target="_blank"
                                        rel="noopener"
                                        i18n="@@materials.link"
                                        >посилання</a
                                    >
                                }
                            </td>
                            <td>
                                <span class="badge">{{ statusLabel[m.status] }}</span>
                            </td>
                            <td>{{ m.views }}</td>
                            <td>
                                @if (session.isCoordinator()) {
                                    <button
                                        mat-button
                                        type="button"
                                        (click)="edit(m)"
                                        i18n="@@materials.edit"
                                    >
                                        Змінити
                                    </button>
                                }
                            </td>
                        </tr>
                    } @empty {
                        <tr>
                            <td colspan="4" class="muted" i18n="@@materials.empty">
                                Матеріалів немає.
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
            @if (session.isCoordinator()) {
                <h2 class="page-subtitle">{{ form.id ? editTitle : newTitle }}</h2>
                <form class="editor" (ngSubmit)="save()">
                    <label
                        ><span i18n="@@materials.name">Назва</span
                        ><input class="border" name="title" [(ngModel)]="form.title" required
                    /></label>
                    <label
                        ><span i18n="@@materials.linkField">Посилання</span
                        ><input class="border" type="url" name="link" [(ngModel)]="form.link"
                    /></label>
                    <label
                        ><span i18n="@@materials.status">Стан</span>
                        <select class="border" name="status" [(ngModel)]="form.status">
                            <option value="draft">{{ statusLabel.draft }}</option>
                            <option value="published">{{ statusLabel.published }}</option>
                            <option value="archived">{{ statusLabel.archived }}</option>
                        </select>
                    </label>
                    <label class="wide"
                        ><span i18n="@@materials.body">Текст</span
                        ><textarea
                            class="border"
                            rows="6"
                            name="body"
                            [(ngModel)]="form.body"
                        ></textarea>
                    </label>
                    <div class="form-row wide">
                        <button mat-flat-button type="submit" i18n="@@materials.save">
                            Зберегти
                        </button>
                        @if (form.id) {
                            <button
                                mat-button
                                type="button"
                                (click)="form = newForm()"
                                i18n="@@materials.cancelEdit"
                            >
                                Скасувати
                            </button>
                        }
                    </div>
                </form>
            }
        </section>
    `,
})
export class MaterialsPage implements OnInit {
    protected readonly session = inject(Session);
    private readonly api = inject(Api);

    protected readonly statusLabel = {
        draft: $localize`:@@status.draft:чернетка`,
        published: $localize`:@@status.published:опубліковано`,
        archived: $localize`:@@status.archived:архів`,
    };
    protected readonly newTitle = $localize`:@@materials.new:Новий матеріал`;
    protected readonly editTitle = $localize`:@@materials.editTitle:Редагування матеріалу`;
    protected readonly materials = signal<Material[]>([]);
    protected readonly error = signal<string | null>(null);
    protected form = empty();
    protected readonly newForm = empty;

    ngOnInit(): void {
        this.reload();
    }

    protected edit(m: Material): void {
        this.form = {
            id: m.id,
            title: m.title,
            body: m.body ?? '',
            link: m.link ?? '',
            status: m.status,
        };
    }

    protected save(): void {
        const f = this.form;
        this.api
            .saveMaterial(f.id, {
                title: f.title,
                body: f.body || null,
                link: f.link || null,
                status: f.status,
            })
            .subscribe({
                next: () => {
                    this.form = empty();
                    this.reload();
                },
                error: (e: unknown) => this.error.set(errorText(e)),
            });
    }

    private reload(): void {
        this.api.materials().subscribe((list) => this.materials.set(list));
    }
}
