import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { ConnectorsPage } from '../connectors/connectors.page';
import { CredentialsPage } from '../credentials/credentials.page';
import { BpTab, BpTabsComponent } from '../../shared/tabs/tabs.component';

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
  imports: [BpTabsComponent, ConnectorsPage, CredentialsPage],
  template: `
    <section class="p-6 pb-0">
      <h1 class="mat-font-headline-sm" i18n="@@connections.title">Підключення</h1>
      <bp-tabs class="mt-3" [tabs]="tabs" [value]="tab()" (valueChange)="show($any($event))" label="Розділ підключень" i18n-label="@@connections.tabs" testId="connections-tabs" />
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

  readonly tabs: BpTab[] = [
    { value: 'connected', testId: 'tab-connected', label: $localize`:@@connections.tab.connected:Підключено` },
    { value: 'catalog', testId: 'tab-catalog', label: $localize`:@@connections.tab.catalog:Каталог` },
    { value: 'credentials', testId: 'tab-credentials', label: $localize`:@@connections.tab.credentials:Облікові дані` },
  ];

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
