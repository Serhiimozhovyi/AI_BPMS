import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConnectorToolsComponent } from './connector-tools.component';
import { McpToolInfo } from './connectors-api.service';

const tools: McpToolInfo[] = [
  {
    name: 'create_order',
    title: 'Створити замовлення',
    description: 'Створює замовлення в 1С',
    inputSchema: { type: 'object', properties: { customer: { type: 'string' } }, required: ['customer'] },
  },
];

describe('ConnectorToolsComponent', () => {
  function setup(list: McpToolInfo[] = tools) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(ConnectorToolsComponent);
    fixture.componentRef.setInput('connectorId', 'c1');
    fixture.componentRef.setInput('tools', list);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: ReturnType<typeof setup>['fixture']) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('без tools — підказка оновити', () => {
    const { host } = setup([]);
    expect(host.querySelector('[data-testid="no-tools"]')?.textContent).toContain("Перевірити з'єднання");
  });

  it('показує tools зі схемою аргументів', () => {
    const { host } = setup();
    const panel = host.querySelector('[data-tool="create_order"]')!;
    expect(panel.textContent).toContain('create_order');
    expect(panel.textContent).toContain('Створити замовлення');
    expect(panel.querySelector('[data-testid="input-schema"]')?.textContent).toContain('"customer"');
  });

  it('тестовий виклик із заготовкою аргументів показує результат', async () => {
    const { fixture, http, host } = setup();
    fixture.componentInstance.ensureState(tools[0]);
    await render(fixture);
    const args = host.querySelector<HTMLTextAreaElement>('[data-testid="call-args"]')!;
    expect(JSON.parse(args.value)).toEqual({ customer: '' });

    args.value = '{"customer":"ТОВ Ромашка"}';
    args.dispatchEvent(new Event('input'));
    host.querySelector<HTMLButtonElement>('[data-testid="call-button"]')!.click();

    const req = http.expectOne({ method: 'POST', url: '/api/v1/connectors/c1/tools/create_order/call' });
    expect(req.request.body).toEqual({ arguments: { customer: 'ТОВ Ромашка' } });
    req.flush({ isError: false, structuredContent: { orderId: 42 }, content: [{ type: 'text', text: 'готово' }], elapsed: '00:00:00.2500000' });
    await render(fixture);

    const result = host.querySelector('[data-testid="call-result"]')!;
    expect(result.textContent).toContain('Виклик успішний');
    expect(result.textContent).toContain('"orderId": 42');
    expect(result.textContent).toContain('готово');
  });

  it('невалідний JSON аргументів — помилка без запиту', async () => {
    const { fixture, http, host } = setup();
    fixture.componentInstance.ensureState(tools[0]);
    fixture.componentInstance.setArgs('create_order', '{bad');
    fixture.componentInstance.call('create_order');
    await render(fixture);
    http.expectNone(() => true);
    expect(host.querySelector('[data-testid="call-error"]')?.textContent).toContain('JSON');
  });

  it('недоступний сервер (502) — повідомлення про помилку', async () => {
    const { fixture, http, host } = setup();
    fixture.componentInstance.ensureState(tools[0]);
    fixture.componentInstance.call('create_order');
    http
      .expectOne('/api/v1/connectors/c1/tools/create_order/call')
      .flush({ detail: 'MCP-сервер не відповідає', code: 'CONNECTOR_UNAVAILABLE' }, { status: 502, statusText: 'Bad Gateway' });
    await render(fixture);
    expect(host.querySelector('[data-testid="call-error"]')?.textContent).toContain('MCP-сервер не відповідає');
  });

  it('isError від сервера позначається як помилка', async () => {
    const { fixture, http, host } = setup();
    fixture.componentInstance.ensureState(tools[0]);
    fixture.componentInstance.call('create_order');
    http.expectOne('/api/v1/connectors/c1/tools/create_order/call').flush({ isError: true, structuredContent: null, content: [], elapsed: '00:00:00' });
    await render(fixture);
    expect(host.querySelector('[data-testid="call-result"]')?.textContent).toContain('Сервер повернув помилку');
  });
});
