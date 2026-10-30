import { Clipboard } from '@angular/cdk/clipboard';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { AuthService, UserInfo } from '../../auth/auth.service';
import { ApiKeyInfo } from '../../settings/api-keys-api.service';
import { SettingsPage } from './settings.page';

const claude: ApiKeyInfo = { id: 'k1', name: 'Claude', createdBy: 'u1', lastUsedAt: '2026-10-23T10:00:00Z' };
const cursor: ApiKeyInfo = { id: 'k2', name: 'Cursor', createdBy: 'u1', lastUsedAt: null };

describe('SettingsPage', () => {
  async function setup(items: ApiKeyInfo[] = [claude, cursor], role?: UserInfo['role'], section: string | null = null) {
    const copy = vi.fn().mockReturnValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: Clipboard, useValue: { copy } },
        { provide: ActivatedRoute, useValue: { queryParamMap: new BehaviorSubject(convertToParamMap(section ? { section } : {})) } },
      ],
    });
    if (role) TestBed.inject(AuthService).user.set({ id: 'u1', email: 'u@openbp.test', role });
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne({ method: 'GET', url: '/api/v1/api-keys' }).flush(items);
    await render(fixture);
    return { fixture, http, copy, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function typeName(host: HTMLElement, value: string) {
    const input = host.querySelector<HTMLInputElement>('[data-testid="key-name"]')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('показує свої ключі без самих ключів', async () => {
    const { host } = await setup();
    const rows = host.querySelectorAll('[data-key]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Claude');
    expect(rows[1].textContent).toContain('ще не використовувався');
    expect(host.querySelector('[data-testid="created-key"]')).toBeNull();
  });

  it('порожній список — підказка', async () => {
    const { host } = await setup([]);
    expect(host.querySelector('[data-testid="empty"]')).not.toBeNull();
  });

  it('створення показує ключ один раз, копіює його і підставляє у фрагмент MCP', async () => {
    const { fixture, http, copy, host } = await setup([]);
    typeName(host, '  Claude Desktop ');
    host.querySelector<HTMLButtonElement>('[data-testid="create-button"]')!.click();
    const req = http.expectOne({ method: 'POST', url: '/api/v1/api-keys' });
    expect(req.request.body).toEqual({ name: 'Claude Desktop' });
    req.flush({ id: 'k3', name: 'Claude Desktop', key: 'obp_once' });
    http.expectOne({ method: 'GET', url: '/api/v1/api-keys' }).flush([{ id: 'k3', name: 'Claude Desktop', createdBy: 'u1', lastUsedAt: null }]);
    await render(fixture);

    expect(host.querySelector('[data-testid="created-key-value"]')?.textContent).toBe('obp_once');
    expect(host.querySelectorAll('[data-key]')).toHaveLength(1);

    host.querySelector<HTMLButtonElement>('[data-testid="copy-key"]')!.click();
    await render(fixture);
    expect(copy).toHaveBeenCalledWith('obp_once');
    expect(host.querySelector('[data-testid="copied"]')).not.toBeNull();

    // «Готово» — ключ більше не показується ніде на сторінці.
    host.querySelector<HTMLButtonElement>('[data-testid="hide-key"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-testid="created-key"]')).toBeNull();
    expect(host.textContent).not.toContain('obp_once');
  });

  it('порожня назва — запит не надсилається', async () => {
    const { http, host } = await setup([]);
    typeName(host, '   ');
    host.querySelector<HTMLButtonElement>('[data-testid="create-button"]')!.click();
    http.expectNone({ method: 'POST', url: '/api/v1/api-keys' });
  });

  it('відкликання прибирає ключ зі списку', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-key="k1"] [data-testid="revoke"]')!.click();
    http.expectOne({ method: 'DELETE', url: '/api/v1/api-keys/k1' }).flush(null, { status: 204, statusText: 'No Content' });
    await render(fixture);
    const rows = host.querySelectorAll('[data-key]');
    expect(rows).toHaveLength(1);
    expect(rows[0].getAttribute('data-key')).toBe('k2');
  });

  it('помилка відкликання показується, ключ лишається', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-key="k1"] [data-testid="revoke"]')!.click();
    http.expectOne('/api/v1/api-keys/k1').flush(null, { status: 403, statusText: 'Forbidden' });
    await render(fixture);
    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('прав');
    expect(host.querySelectorAll('[data-key]')).toHaveLength(2);
  });

  it('блок «Підключити ШІ-агента» — URL /mcp і заглушка ключа', async () => {
    const { fixture, copy, host } = await setup([], undefined, 'agent');
    expect(host.textContent).toContain('Підключити ШІ-агента');
    expect(host.querySelector('[data-testid="mcp-url"]')?.textContent).toBe(`${location.origin}/mcp`);
    // Перша вкладка — Claude Desktop через mcp-remote.
    const desktop = JSON.parse(host.querySelector('[data-testid="mcp-config-desktop"]')!.textContent!);
    expect(desktop.mcpServers.openbp.args).toContain(`${location.origin}/mcp`);
    expect(desktop.mcpServers.openbp.env.OPENBP_AUTH).toBe('Bearer <ваш API-ключ>');
    host.querySelector<HTMLButtonElement>('[data-testid="copy-config-desktop"]')!.click();
    expect(copy).toHaveBeenCalledWith(expect.stringContaining('"mcp-remote"'));

    const labels = [...host.querySelectorAll<HTMLElement>('[role="tab"]')];
    expect(labels.map((t) => t.querySelector('.bp-tab__label')?.textContent?.trim())).toEqual(['Claude Desktop', 'Claude Code', 'Інше']);
    labels[1].click();
    await render(fixture);
    expect(host.querySelector('[data-testid="mcp-config-code"]')?.textContent).toContain('claude mcp add --transport http openbp');
    labels[2].click();
    await render(fixture);
    const other = JSON.parse(host.querySelector('[data-testid="mcp-config-other"]')!.textContent!);
    expect(other.mcpServers.openbp.headers.Authorization).toBe('Bearer <ваш API-ключ>');

    // Щойно створений ключ підставляється в усі фрагменти.
    fixture.componentInstance.created.set({ id: 'k9', name: 'Новий', key: 'obp_once' });
    await render(fixture);
    expect(host.querySelector('[data-testid="mcp-config-other"]')?.textContent).toContain('Bearer obp_once');
  });

  it('без ?section= відкриваються API-ключі, у навігації лише розділи для ролі', async () => {
    const { host } = await setup([], 'editor');
    expect(host.querySelector('[data-testid="keys-table"], [data-testid="empty"]')).not.toBeNull();
    const ids = [...host.querySelectorAll('[data-section]')].map((a) => a.getAttribute('data-section'));
    expect(ids).toEqual(['api-keys', 'agent', 'variables']);
    expect(host.querySelector('[data-section="api-keys"]')?.getAttribute('aria-current')).toBe('page');
  });

  it('?section=variables показує лише розділ змінних', async () => {
    const { fixture, http, host } = await setup([], 'editor', 'variables');
    http.expectOne('/api/v1/variables').flush([]);
    await render(fixture);
    expect(host.querySelector('[data-testid="vars-empty"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="create-button"]')).toBeNull();
    expect(host.querySelector('[data-section="variables"]')?.getAttribute('aria-current')).toBe('page');
    expect(host.querySelector('select')).toBeNull();
  });

  it('чужий для ролі розділ (audit для editor) відкриває перший', async () => {
    const { host } = await setup([], 'editor', 'audit');
    expect(host.querySelector('[data-testid="audit-section"]')).toBeNull();
    expect(host.querySelector('[data-testid="create-button"]')).not.toBeNull();
  });

  it('журнал аудиту, сповіщення й помічника бачить лише admin', async () => {
    const admin = await setup([], 'admin', 'audit');
    expect(admin.host.querySelector('[data-testid="audit-section"]')).not.toBeNull();
    expect(admin.http.match((r) => r.url === '/api/v1/audit')).toHaveLength(1);
    const ids = [...admin.host.querySelectorAll('[data-section]')].map((a) => a.getAttribute('data-section'));
    expect(ids).toEqual(['api-keys', 'agent', 'variables', 'assistant', 'notifications', 'audit']);
    admin.http.match(() => true);
  });

  it('сповіщення: розділ завантажує канали', async () => {
    const admin = await setup([], 'admin', 'notifications');
    expect(admin.host.querySelector('[data-testid="notifications-section"]')).not.toBeNull();
    expect(admin.http.match('/api/v1/notification-channels')).toHaveLength(1);
  });
});
