import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { SubscribeSourceComponent } from './subscribe-source.component';

const node = (parameters: Record<string, unknown>): WorkflowNode => ({
  id: 'n_sub',
  name: 'Подія',
  type: 'mcp.subscribe',
  typeVersion: 1,
  position: [0, 0],
  parameters,
});

const page = { items: [{ id: 'srv1', name: 'Telegram' }, { id: 'srv2', name: 'Kit CRM' }], page: 1, pageSize: 200, total: 2 };

describe('SubscribeSourceComponent', () => {
  async function setup(parameters: Record<string, unknown>) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(SubscribeSourceComponent);
    fixture.componentRef.setInput('node', node(parameters));
    const emitted: WorkflowNode[] = [];
    fixture.componentInstance.nodeChange.subscribe((n) => {
      emitted.push(n);
      fixture.componentRef.setInput('node', n);
    });
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/connectors').flush(page);
    return { fixture, http, emitted, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('без конектора ресурсів не вантажить; вибір конектора скидає ресурс і вантажить список', async () => {
    const { fixture, http, emitted, host } = await setup({ resourceUri: 'old://x', keyField: 'id' });
    await render(fixture);

    fixture.componentInstance['connectorPicked']('srv1');
    expect(emitted.at(-1)?.parameters).toEqual({ keyField: 'id', serverId: 'srv1' });
    http.expectOne('/api/v1/connectors/srv1/resources').flush([
      { uri: 'telegram://updates', name: 'Оновлення', description: 'Нові повідомлення', isTemplate: false },
    ]);
    await render(fixture);
    expect(host.querySelector('[data-testid="subscribe-empty"]')).toBeNull();
    expect((host.querySelector('[data-testid="subscribe-resource"]') as HTMLInputElement).value).toBe('');
  });

  it('вибір і ручне введення ресурсу пишуть resourceUri, решту параметрів зберігають', async () => {
    const { fixture, http, emitted } = await setup({ serverId: 'srv1', keyField: 'update_id', itemsPath: 'result' });
    http.expectOne('/api/v1/connectors/srv1/resources').flush([{ uri: 'telegram://updates', name: 'Оновлення', isTemplate: false }]);
    await render(fixture);

    fixture.componentInstance['resourcePicked']({ option: { value: 'telegram://updates' } } as never);
    expect(emitted.at(-1)?.parameters).toEqual({ serverId: 'srv1', keyField: 'update_id', itemsPath: 'result', resourceUri: 'telegram://updates' });

    fixture.componentInstance['resourceTyped']('kit://changes/sales');
    expect(emitted.at(-1)?.parameters?.['resourceUri']).toBe('kit://changes/sales');
    fixture.componentInstance['resourceTyped']('');
    expect(emitted.at(-1)?.parameters).not.toHaveProperty('resourceUri');
  });

  it('конектор без ресурсів показує підказку, помилка — повідомлення; «Оновити» читає з refresh', async () => {
    const { fixture, http, host } = await setup({ serverId: 'srv2', resourceUri: 'x://y' });
    http.expectOne('/api/v1/connectors/srv2/resources').flush([]);
    await render(fixture);
    expect(host.querySelector('[data-testid="subscribe-empty"]')).not.toBeNull();

    (host.querySelector('[data-testid="subscribe-refresh"]') as HTMLButtonElement).click();
    const retry = http.expectOne((r) => r.url === '/api/v1/connectors/srv2/resources');
    expect(retry.request.params.get('refresh')).toBe('true');
    retry.flush({ detail: 'Конектор недоступний' }, { status: 502, statusText: 'Bad Gateway' });
    await render(fixture);
    expect(host.querySelector('[data-testid="subscribe-error"]')).not.toBeNull();
  });

  it('readonly: зміни не надсилаються', async () => {
    const { fixture, http, emitted } = await setup({ serverId: 'srv1' });
    http.expectOne('/api/v1/connectors/srv1/resources').flush([]);
    fixture.componentRef.setInput('readonly', true);
    fixture.componentInstance['connectorPicked']('srv2');
    fixture.componentInstance['resourceTyped']('a://b');
    expect(emitted).toEqual([]);
  });
});
