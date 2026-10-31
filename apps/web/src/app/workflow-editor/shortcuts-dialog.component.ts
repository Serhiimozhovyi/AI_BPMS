import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule } from '@angular/material/dialog';
import { keyLabel, shortcutGroups } from './shortcuts';

/** Довідка «Гарячі клавіші» (F3-90): клавіші редактора, згруповані за діями. */
@Component({
  selector: 'app-shortcuts-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title i18n="@@shortcuts.title">Гарячі клавіші</h2>
    <mat-dialog-content>
      <div class="groups" data-testid="shortcuts">
        @for (group of groups; track group.id) {
          <section [attr.data-group]="group.id">
            <h3 class="group__title">{{ group.title }}</h3>
            <dl class="group__list">
              @for (s of group.shortcuts; track s.action) {
                <div class="row">
                  <dt>
                    @for (key of s.keys; track key; let last = $last) {
                      <kbd>{{ label(key) }}</kbd>
                      @if (!last) {
                        <span class="or" i18n="@@shortcuts.or">або</span>
                      }
                    }
                  </dt>
                  <dd>{{ s.action }}</dd>
                </div>
              }
            </dl>
          </section>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close data-testid="shortcuts-close" i18n="@@shortcuts.close">Закрити</button>
    </mat-dialog-actions>
  `,
  styles: `
    .groups {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: 16px 32px;
    }
    .group__title {
      margin: 0 0 8px;
      color: var(--bp-text);
      font: 700 12px/16px var(--bp-font-family);
      text-transform: uppercase;
    }
    .group__list {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 0;
    }
    .row {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: 12px;
    }
    dd {
      margin: 0;
      flex: 1;
      font-size: 13px;
    }
    dt {
      display: flex;
      flex: 0 0 auto;
      align-items: center;
      gap: 4px;
      min-width: 108px;
    }
    kbd {
      padding: 1px 6px;
      border: 1px solid var(--bp-line);
      border-radius: 4px;
      background: var(--bp-bg-2);
      font: var(--bp-font-p4);
      white-space: nowrap;
    }
    .or {
      color: var(--bp-text-muted, #57696b);
      font-size: 11px;
    }
  `,
})
export class ShortcutsDialogComponent {
  protected readonly groups = shortcutGroups();
  private readonly mac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform);

  protected label(key: string): string {
    return keyLabel(key, this.mac);
  }
}
