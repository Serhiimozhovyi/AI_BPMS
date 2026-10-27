import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { EditorToolbarComponent } from './editor-toolbar.component';
import { WorkflowDetail } from '../workflows/workflows-api.service';

const workflow = {
  id: 'w1',
  name: 'Заявки',
  version: 3,
  publishedVersion: 3,
  groupId: null,
  definition: { formatVersion: 1, name: 'Заявки', nodes: [{ id: 'n1', type: 'core.manualTrigger', name: 'Старт' }], connections: [] },
} as unknown as WorkflowDetail;

describe('EditorToolbarComponent', () => {
  let fixture: ComponentFixture<EditorToolbarComponent>;
  let host: HTMLElement;

  async function render(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    fixture = TestBed.createComponent(EditorToolbarComponent);
    host = fixture.nativeElement;
    fixture.componentRef.setInput('workflow', workflow);
    fixture.componentRef.setInput('definition', workflow.definition);
    await render();
  });

  const q = <T extends HTMLElement>(id: string): T | null => host.querySelector<T>(`[data-testid="${id}"]`);

  it('стани автозбереження: незбережено, збереження, помилка, збережено', async () => {
    const texts: Record<string, string> = {};
    for (const status of ['pending', 'saving', 'error', 'saved'] as const) {
      fixture.componentRef.setInput('saveStatus', status);
      await render();
      const el = q(status === 'saved' ? 'saved' : 'dirty')!;
      texts[status] = el.textContent!.trim();
      if (status !== 'saved') expect(el.getAttribute('data-status')).toBe(status);
    }
    expect(texts['pending']).toContain('незбережені');
    expect(texts['saving']).toContain('Збереження');
    expect(texts['error']).toContain('Помилка збереження');
    expect(texts['saved']).toBe('Збережено');
    expect(q('saved')!.getAttribute('title')).toContain('3');
  });

  it('меню «⋯» ховає Помічник, Історію, Налаштування й відкривається по кліку', async () => {
    expect(q('assistant-toggle')).toBeNull();
    q('toolbar-more')!.click();
    await render();
    expect(q('assistant-toggle')).not.toBeNull();
    expect(q('history')).not.toBeNull();
    expect(q('settings')).not.toBeNull();
    expect(q('executions-link')).not.toBeNull();

    const actions: string[] = [];
    fixture.componentInstance.action.subscribe((a) => actions.push(a));
    q('history')!.click();
    await render();
    expect(actions).toEqual(['history']);
    expect(q('toolbar-menu')).toBeNull();
  });

  it('«Виконати» завжди в шапці й віддає дію run', async () => {
    const actions: string[] = [];
    fixture.componentInstance.action.subscribe((a) => actions.push(a));
    q('run')!.click();
    expect(actions).toEqual(['run']);
  });

  it('перейменування: клік → поле, Enter віддає нову назву, Esc скасовує', async () => {
    const names: string[] = [];
    fixture.componentInstance.renamed.subscribe((n) => names.push(n));

    q('workflow-name-edit')!.click();
    await render();
    let input = q<HTMLInputElement>('workflow-name-input')!;
    input.value = 'Нова назва';
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await render();
    expect(names).toEqual(['Нова назва']);
    expect(q('workflow-name-input')).toBeNull();

    q('workflow-name-edit')!.click();
    await render();
    input = q<HTMLInputElement>('workflow-name-input')!;
    input.value = 'Інша';
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await render();
    expect(names).toEqual(['Нова назва']);
    expect(q('workflow-name')!.textContent).toContain('Заявки');
  });

  it('лише перегляд: без перейменування, редагування й «Виконати»', async () => {
    fixture.componentRef.setInput('readonly', true);
    await render();
    expect(q('workflow-name-edit')).toBeNull();
    expect(q('undo')).toBeNull();
    expect(q('run')).toBeNull();
    expect(q('readonly')).not.toBeNull();
  });
});
