import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { ConnectorsPage } from '../connectors/connectors.page';
import { CredentialsPage } from '../credentials/credentials.page';

export type ConnectionsTab = 'connected' | 'catalog' | 'credentials';

const TABS: readonly ConnectionsTab[] = ['connected', 'catalog', 'credentials'];

/**
 * `/connections` (F3-99): підключення до систем в одному місці. «Підключено» — встановлені конектори, «Каталог» —
 * додати підключення (облікові дані вводяться там же, F3-94), «Облікові дані» — доступи й де їх використано.
 * Вкладка і підсвічений запис каталогу живуть в адресі (`?tab=…&catalog=…`), тож посилання працюють.
 */
@Component({
  selector: 'app-connections-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonToggleModule, ConnectorsPage, CredentialsPage],
  template: `
    <section class="p-6 pb-0">
      <div class="flex items-center gap-4">
        <h1 class="mat-font-headline-sm" i18n="@@connections.title">Підключення</h1>
        <mat-button-toggle-group [value]="tab()" (change)="show($event.value)" data-testid="connections-tabs" aria-label="Розділ підключень" i18n-aria-label="@@connections.tabs">
          <mat-button-toggle value="connected" data-testid="tab-connected" i18n="@@connections.tab.connected">Підключено</mat-button-toggle>
          <mat-button-toggle value="catalog" data-testid="tab-catalog" i18n="@@connections.tab.catalog">Каталог</mat-button-toggle>
          <mat-button-toggle value="credentials" data-testid="tab-credentials" i18n="@@connections.tab.credentials">Облікові дані</mat-button-toggle>
        </mat-button-toggle-group>
      </div>
    </section>
    @if (tab() === 'credentials') {
      <app-credentials-page [embedded]="true" />
    } @else {
      <app-connectors-page
        [embedded]="true"
        [tab]="tab() === 'catalog' ? 'catalog' : 'installed'"
        [highlightId]="highlight()"
        (viewChange)="show($event === 'catalog' ? 'catalog' : 'connected')"
      />
    }
  `,
})
export class ConnectionsPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly query = toSignal(this.route.queryParamMap.pipe(map((q) => ({ tab: q.get('tab'), catalog: q.get('catalog') }))), {
    initialValue: { tab: null as string | null, catalog: null as string | null },
  });

  readonly tab = computed<ConnectionsTab>(() => {
    const tab = this.query().tab;
    return TABS.find((t) => t === tab) ?? 'connected';
  });
  readonly highlight = computed(() => this.query().catalog || null);

  show(tab: ConnectionsTab): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab, catalog: tab === 'catalog' ? this.query().catalog : null },
      replaceUrl: true,
    });
  }
}
