import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { StageStep, StageStepsComponent } from './stage-steps.component';

describe('StageStepsComponent', () => {
  function render(steps: StageStep[]): HTMLElement {
    const f = TestBed.createComponent(StageStepsComponent);
    f.componentRef.setInput('steps', steps);
    f.detectChanges();
    return f.nativeElement as HTMLElement;
  }

  const steps: StageStep[] = [
    { id: 'a', caption: 'Етап 1', name: 'Заявка', status: 'done', stateLabel: 'Пройдено' },
    {
      id: 'b',
      caption: 'Етап 2',
      name: 'Погодження',
      status: 'active',
      overdue: true,
      stateLabel: 'Прострочено',
      dueLabel: 'до 31.10',
    },
    { id: 'c', name: 'Оплата', status: 'pending' },
  ];

  it('малює крок на кожен етап із станом і атрибутами', () => {
    const el = render(steps);
    const items = el.querySelectorAll('li');
    expect(items).toHaveLength(3);
    expect(items[0].dataset['status']).toBe('done');
    expect(items[1].dataset['overdue']).toBe('true');
    expect(items[1].classList.contains('stage--overdue')).toBe(true);
    expect(items[1].querySelector('[data-testid="stage-due"]')?.textContent).toContain('до 31.10');
    expect(items[2].querySelector('[data-testid="stage-state"]')).toBeNull();
  });

  it('позначає активний крок aria-current', () => {
    const el = render(steps);
    const items = el.querySelectorAll('li');
    expect(items[1].getAttribute('aria-current')).toBe('step');
    expect(items[0].getAttribute('aria-current')).toBeNull();
  });
});
