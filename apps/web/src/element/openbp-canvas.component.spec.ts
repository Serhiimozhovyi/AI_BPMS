import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { StageActedEvent } from '../app/executions/stage-board.component';
import { elementApiInterceptor } from './element-services';
import { OpenbpCanvasElement } from './openbp-canvas.component';

describe('<openbp-canvas view="business" execution-id> (F7-05)', () => {
  it('малює етапи виконання з контракту на api-base з токеном і віддає openbp-action', async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([elementApiInterceptor])), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(OpenbpCanvasElement);
    fixture.componentRef.setInput('apiBase', 'https://openbp.example.com/');
    fixture.componentRef.setInput('token', 'obp_key');
    fixture.componentRef.setInput('view', 'business');
    fixture.componentRef.setInput('executionId', 'e1');
    const actions: StageActedEvent[] = [];
    fixture.componentInstance.action.subscribe((e) => actions.push(e));
    fixture.detectChanges();
    await fixture.whenStable();
    const http = TestBed.inject(HttpTestingController);

    const get = http.expectOne('https://openbp.example.com/api/v1/executions/e1/stages');
    expect(get.request.headers.get('Authorization')).toBe('Bearer obp_key');
    get.flush({
      execution: { id: 'e1', workflowId: 'w1', versionId: 'v1', mode: 'manual', state: 'waiting', startedAt: null, finishedAt: null, error: null },
      stages: [
        {
          id: 'st_pay',
          name: 'Оплата',
          position: 0,
          status: 'active',
          overdue: false,
          enteredAt: null,
          dueAt: null,
          exitedAt: null,
          owner: null,
          violations: [],
          unmet: [],
          actions: [{ id: 'resume:n_wait', kind: 'resume', title: 'Продовжити «Оплата»', reasonRequired: false, nodeId: 'n_wait' }],
        },
      ],
    });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const root = (fixture.nativeElement as HTMLElement).shadowRoot ?? (fixture.nativeElement as HTMLElement);
    expect(root.querySelector('app-workflow-editor')).toBeNull();
    expect(root.querySelector('[data-stage="st_pay"]')?.textContent).toContain('Оплата');
    (root.querySelector('[data-testid="stage-resume"]') as HTMLButtonElement).click();
    http.expectOne('https://openbp.example.com/api/v1/executions/e1/stages/st_pay/actions/resume%3An_wait').flush({ execution: {}, stages: [] });

    expect(actions).toEqual([{ executionId: 'e1', stageId: 'st_pay', actionId: 'resume:n_wait', kind: 'resume', decision: null }]);
    http.verify();
  });
});
