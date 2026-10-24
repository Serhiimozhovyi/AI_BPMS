import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PublishBarComponent, publishStateOf } from './publish-bar.component';
import { WorkflowDefinition, WorkflowDetail, emptyWorkflowDefinition } from './workflows-api.service';

const node = (id: string, name: string, type = 'core.set') => ({ id, name, type, typeVersion: 1, position: [0, 0] as [number, number], parameters: {} });

function definition(names: string[]): WorkflowDefinition {
  return { ...emptyWorkflowDefinition('Сценарій'), nodes: [node('t', 'Старт', 'core.manualTrigger'), ...names.map((n, i) => node(`n${i}`, n))] };
}

function detail(version: number, publishedVersion: number | null, def = definition([])): WorkflowDetail {
  return {
    id: 'wf',
    name: 'Сценарій',
    active: true,
    version,
    versionId: `v${version}`,
    definition: def,
    updatedAt: '2026-10-24T10:00:00Z',
    publishedVersion,
    hasUnpublishedChanges: publishedVersion !== version,
  };
}

function setup(workflow: WorkflowDetail, draft: WorkflowDefinition) {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const fixture = TestBed.createComponent(PublishBarComponent);
  fixture.componentRef.setInput('workflow', workflow);
  fixture.componentRef.setInput('draft', draft);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const q = (id: string) => host.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  return { fixture, host, q, http: TestBed.inject(HttpTestingController) };
}

describe('publishStateOf', () => {
  it('розрізняє опубліковану, змінену і ще не опубліковану чернетку', () => {
    expect(publishStateOf({ version: 3, publishedVersion: 3 })).toBe('published');
    expect(publishStateOf({ version: 4, publishedVersion: 3 })).toBe('changed');
    expect(publishStateOf({ version: 1, publishedVersion: null })).toBe('never');
  });
});

describe('PublishBarComponent', () => {
  it('опублікована чернетка — лише стан, без кнопок і без запиту diff', () => {
    const { q, http } = setup(detail(2, 2), definition([]));
    expect(q('publish-state')?.getAttribute('data-state')).toBe('published');
    expect(q('publish-state')?.textContent).toContain('Опубліковано · версія 2');
    expect(q('publish')).toBeNull();
    expect(q('discard-draft')).toBeNull();
    http.verify();
  });

  it('є неопубліковані зміни: diff до опублікованої версії, «Опублікувати» віддає відповідь Api', () => {
    const draft = definition(['Задати поля', 'Код']);
    const { fixture, q, http } = setup(detail(5, 3, draft), draft);
    http.expectOne('/api/v1/workflows/wf/versions/3').flush(definition(['Задати поля']));
    fixture.detectChanges();

    expect(q('publish-state')?.getAttribute('data-state')).toBe('changed');
    expect(q('publish-state')?.textContent).toContain('Є неопубліковані зміни');
    expect(q('publish-diff')?.textContent).toContain('додано: Код');
    expect(q('discard-draft')).not.toBeNull();

    let published: WorkflowDetail | undefined;
    fixture.componentInstance.published.subscribe((d) => (published = d));
    q('publish')!.click();
    http.expectOne({ method: 'POST', url: '/api/v1/workflows/wf/publish' }).flush(detail(5, 5, draft));
    expect(published?.publishedVersion).toBe(5);
    http.verify();
  });

  it('помилки валідації публікації показуються, кнопки вимкнені під час збереження', () => {
    const { fixture, q, http } = setup(detail(1, null), definition([]));
    expect(q('publish-state')?.getAttribute('data-state')).toBe('never');
    expect(q('discard-draft')).toBeNull();

    q('publish')!.click();
    http.expectOne('/api/v1/workflows/wf/publish').flush(
      { code: 'WORKFLOW_INVALID', title: 'Сценарій некоректний', errors: [{ code: 'TRIGGER_MISSING', message: 'Немає тригера.' }] },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    fixture.detectChanges();
    expect(q('publish-error')?.textContent).toContain('Немає тригера.');

    fixture.componentRef.setInput('saving', true);
    fixture.detectChanges();
    expect((q('publish') as HTMLButtonElement).disabled).toBe(true);
    http.verify();
  });

  it('«Скасувати зміни» віддає опубліковану версію', () => {
    const { fixture, q, http } = setup(detail(4, 2), definition(['Новий']));
    http.expectOne('/api/v1/workflows/wf/versions/2').flush(definition([]));
    let discarded: WorkflowDetail | undefined;
    fixture.componentInstance.discarded.subscribe((d) => (discarded = d));
    q('discard-draft')!.click();
    http.expectOne({ method: 'POST', url: '/api/v1/workflows/wf/discard-draft' }).flush(detail(2, 2));
    expect(discarded?.version).toBe(2);
    http.verify();
  });
});
