import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { EmptyStartComponent } from './empty-start.component';

describe('EmptyStartComponent', () => {
  function create() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
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
});
