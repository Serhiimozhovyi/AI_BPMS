import { TestBed } from '@angular/core/testing';
import { EmptyStateComponent } from './empty-state.component';

describe('EmptyStateComponent', () => {
  function setup(actionLabel: string | null) {
    const fixture = TestBed.createComponent(EmptyStateComponent);
    fixture.componentRef.setInput('title', 'Порожньо');
    fixture.componentRef.setInput('description', 'Пояснення.');
    fixture.componentRef.setInput('actionLabel', actionLabel);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('показує заголовок, пояснення й основну дію', () => {
    const { fixture, host } = setup('Створити');
    let emitted = 0;
    fixture.componentInstance.action.subscribe(() => emitted++);
    expect(host.querySelector('[data-testid="empty-title"]')?.textContent).toContain('Порожньо');
    expect(host.textContent).toContain('Пояснення.');
    (host.querySelector('[data-testid="empty-action"]') as HTMLButtonElement).click();
    expect(emitted).toBe(1);
  });

  it('без підпису дії кнопки немає', () => {
    const { host } = setup(null);
    expect(host.querySelector('[data-testid="empty-action"]')).toBeNull();
  });
});
