import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';

/** `**` — невідома адреса (F3-84): замість мовчазного перекидання на «Огляд». */
@Component({
  selector: 'app-not-found-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, RouterLink],
  template: `
    <section class="p-6" data-testid="not-found">
      <h1 class="mat-font-headline-sm" i18n="@@notfound.title">Сторінку не знайдено</h1>
      <p class="my-4" i18n="@@notfound.text">Такої адреси в AI_BPMS немає. Можливо, посилання застаріло або містить помилку.</p>
      <a mat-flat-button routerLink="/" data-testid="not-found-home" i18n="@@notfound.home">До «Огляду»</a>
    </section>
  `,
})
export class NotFoundPage {}
