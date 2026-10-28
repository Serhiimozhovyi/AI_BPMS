import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { AuthService } from '../auth/auth.service';
import { ConnectorCatalogEntry, ConnectorDetail } from '../connectors/connectors-api.service';
import { NewConnectionDialogComponent, NewConnectionDialogData } from './new-connection-dialog.component';

const entry = (id: string, name: string): ConnectorCatalogEntry => ({
  manifest: {
    id,
    name,
    description: '',
    transport: 'stdio',
    stdio: { command: 'dotnet', arguments: ['x.dll'] },
    credential: { type: 'telegram.bot', map: { token: 'TELEGRAM_BOT_TOKEN' } },
    triggers: [],
  },
  source: 'builtin',
  installed: [],
});

describe('NewConnectionDialogComponent (F3-95)', () => {
  function setup(data: NewConnectionDialogData) {
    const closed: (ConnectorDetail | undefined)[] = [];
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: { close: (v?: ConnectorDetail) => closed.push(v) } },
      ],
    });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@openbp.test', role: 'admin' } as never);
    const fixture = TestBed.createComponent(NewConnectionDialogComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/credentials/types').flush([
      { type: 'telegram.bot', displayName: 'Telegram: бот', description: '', fields: [{ name: 'token', title: 'Токен бота', kind: 'secret', required: true }], schema: {} },
    ]);
    http.expectOne('/api/v1/connectors/catalog').flush([entry('telegram', 'Telegram'), entry('other', 'Інший')]);
    http.match('/api/v1/credentials/options');
    fixture.detectChanges();
    return { fixture, http, host: fixture.nativeElement as HTMLElement, closed };
  }

  afterEach(() => {
    TestBed.inject(HttpTestingController).match(() => true);
  });

  it('вузол із записом каталогу: одразу крок «Підключення» лише для цього запису', () => {
    const { host } = setup({ catalogId: 'telegram' });
    expect(host.querySelector('h2')?.textContent).toContain('Нове підключення');
    expect(host.querySelector('[data-testid="catalog-connect"]')).not.toBeNull();
    expect(host.querySelectorAll('[data-catalog]')).toHaveLength(1);
    expect(host.querySelector('[data-testid="catalog-connect"]')?.textContent).toContain('Telegram');
  });

  it('вузол без запису каталогу: увесь каталог, крок «Підключення» відкриває користувач', () => {
    const { host } = setup({ catalogId: null });
    expect(host.querySelector('[data-testid="catalog-connect"]')).toBeNull();
    expect(host.querySelectorAll('[data-catalog]')).toHaveLength(2);
  });

  it('підключення встановлено — діалог закривається з ним', () => {
    const { fixture, http, host, closed } = setup({ catalogId: 'telegram' });
    const input = host.querySelector<HTMLInputElement>('[data-testid="field-token"]')!;
    input.value = '123:abc';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="credential-save"]')!.click();
    http.expectOne({ method: 'POST', url: '/api/v1/credentials' }).flush({ id: 'cred1', name: 'Telegram', type: 'telegram.bot', fields: ['token'], usedBy: { connectors: [], workflows: [] } });
    const detail: ConnectorDetail = {
      summary: { id: 'bot9', name: 'Telegram', transport: 'stdio', status: 'ok', toolCount: 1, toolsCachedAt: null, catalogId: 'telegram' },
      config: {},
      credentialId: 'cred1',
      tools: [],
    };
    http.expectOne({ method: 'POST', url: '/api/v1/connectors/catalog/telegram/install' }).flush(detail);
    expect(closed).toEqual([detail]);
  });

  it('помилка встановлення лишається в діалозі людським текстом', () => {
    const { fixture, http, host, closed } = setup({ catalogId: 'telegram' });
    const input = host.querySelector<HTMLInputElement>('[data-testid="field-token"]')!;
    input.value = '123:abc';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="credential-save"]')!.click();
    http.expectOne({ method: 'POST', url: '/api/v1/credentials' }).flush({ id: 'cred1', name: 'Telegram', type: 'telegram.bot', fields: ['token'], usedBy: { connectors: [], workflows: [] } });
    http.expectOne({ method: 'POST', url: '/api/v1/connectors/catalog/telegram/install' }).flush({ code: 'CONFLICT', detail: 'Підключення з такою назвою вже є.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(closed).toEqual([]);
    expect(host.querySelector('[data-testid="connect-error"]')?.textContent).toContain('вже є');
  });
});
