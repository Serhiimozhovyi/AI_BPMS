import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NotFoundPage } from './not-found.page';

describe('NotFoundPage', () => {
  it('показує повідомлення й посилання на «Огляд»', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(NotFoundPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h1')?.textContent).toContain('Сторінку не знайдено');
    expect(el.querySelector('[data-testid="not-found-home"]')?.getAttribute('href')).toBe('/');
  });
});
