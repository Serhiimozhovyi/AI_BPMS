import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { EmptyStartComponent } from './empty-start.component';

describe('EmptyStartComponent', () => {
  function create() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(EmptyStartComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, http: TestBed.inject(HttpTestingController) };
  }

  const click = (host: HTMLElement, testId: string) => (host.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('«Додати тригер» віддає точку під кнопкою', () => {
    const { fixture, host } = create();
    const anchors: unknown[] = [];
    fixture.componentInstance.addTrigger.subscribe((a) => anchors.push(a));

    click(host, 'start-trigger');

    expect(anchors).toEqual([{ x: expect.any(Number), y: expect.any(Number) }]);
  });

  it('«Почати з прикладу»: список шаблонів, вибір, «Назад»; збій — повідомлення і повтор', () => {
    const { fixture, host, http } = create();
    const picked: string[] = [];
    fixture.componentInstance.pick.subscribe((t) => picked.push(t.id));

    click(host, 'start-template');
    http.expectOne('/api/v1/workflows/templates').flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="templates-error"]')?.textContent).toContain('Не вдалося');

    click(host, 'start-template');
    http.expectOne('/api/v1/workflows/templates').flush([
      { id: 'a', name: 'А', description: 'перший', definition: emptyWorkflowDefinition('А') },
      { id: 'b', name: 'Б', description: 'другий', definition: emptyWorkflowDefinition('Б') },
    ]);
    fixture.detectChanges();
    expect(Array.from(host.querySelectorAll('[data-testid="template"]')).map((t) => t.getAttribute('data-template'))).toEqual(['a', 'b']);
    (host.querySelector('[data-template="b"]') as HTMLButtonElement).click();
    expect(picked).toEqual(['b']);

    click(host, 'templates-back');
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="start-trigger"]')).not.toBeNull();
  });

  it('шаблон без потрібних конекторів показує їх з посиланням на каталог (F6-16)', () => {
    const { fixture, host, http } = create();

    click(host, 'start-template');
    http.expectOne('/api/v1/workflows/templates').flush([
      { id: 'a', name: 'А', description: 'без конекторів', definition: emptyWorkflowDefinition('А') },
      { id: 'tg', name: 'Telegram → 1С', description: 'заявка', definition: emptyWorkflowDefinition('Т'), connectors: ['telegram', 'onec'] },
    ]);
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({
      items: [{ id: 'c1', name: '1С', transport: 'stdio', status: 'ok', toolCount: 2, toolsCachedAt: null, catalogId: 'onec' }],
      page: 1,
      pageSize: 200,
      total: 1,
    });
    http.expectOne('/api/v1/connectors/catalog').flush([
      { manifest: { id: 'telegram', name: 'Telegram Bot API' }, source: 'builtin', installed: [] },
      { manifest: { id: 'onec', name: '1С (OData)' }, source: 'builtin', installed: [] },
    ]);
    fixture.detectChanges();

    const hints = host.querySelectorAll('[data-testid="template-missing"]');
    expect(hints).toHaveLength(1);
    expect(hints[0].getAttribute('data-template-missing')).toBe('tg');
    const link = hints[0].querySelector('[data-install]') as HTMLAnchorElement;
    expect(link.getAttribute('data-install')).toBe('telegram');
    expect(link.textContent).toContain('Telegram Bot API');
    expect(link.getAttribute('href')).toBe('/connections?tab=catalog&catalog=telegram');
    expect(hints[0].querySelector('[data-install="onec"]')).toBeNull();
  });
});
