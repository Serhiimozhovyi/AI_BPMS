import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { BpTab, BpTabsComponent, BpTabsVariant } from './tabs.component';

@Component({ template: '' })
class BlankPage {}

@Component({
  imports: [BpTabsComponent],
  template: `<bp-tabs [tabs]="tabs()" [variant]="variant()" [(value)]="value" label="Фільтр" testId="tabs" />`,
})
class ValueHost {
  readonly tabs = signal<BpTab[]>([
    { value: 'all', label: 'Усі', testId: 't-all' },
    { value: 'active', label: 'Активні', icon: '✓', testId: 't-active' },
    { value: 'off', label: 'Вимкнені', disabled: true, testId: 't-off' },
    { value: 'draft', label: 'Чернетки', testId: 't-draft' },
  ]);
  readonly variant = signal<BpTabsVariant>('pills');
  readonly value = signal<string | null>('all');
}

@Component({
  imports: [BpTabsComponent],
  template: `<bp-tabs [tabs]="tabs" testId="tabs" />`,
})
class LinkHost {
  readonly tabs: BpTab[] = [
    { value: 'a', label: 'А', link: '/a' },
    { value: 'b', label: 'Б', link: '/b' },
    { value: 'c', label: 'В', link: '/c' },
  ];
}

const tab = (host: HTMLElement, value: string) => host.querySelector<HTMLElement>(`[data-tab="${value}"]`)!;
/** Клавіша на вкладці, що зараз у порядку Tab (tabindex=0). */
const key = (host: HTMLElement, k: string) => host.querySelector<HTMLElement>('[role="tab"][tabindex="0"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));

describe('BpTabsComponent', () => {
  describe('режим значення', () => {
    async function setup() {
      const fixture = TestBed.createComponent(ValueHost);
      fixture.detectChanges();
      await fixture.whenStable();
      return { fixture, host: fixture.nativeElement as HTMLElement };
    }

    it('tablist з aria-selected і одним tabindex=0; клік вибирає вкладку', async () => {
      const { fixture, host } = await setup();
      const list = host.querySelector('[role="tablist"]')!;
      expect(list.getAttribute('aria-label')).toBe('Фільтр');
      expect(list.getAttribute('data-testid')).toBe('tabs');
      expect([...host.querySelectorAll('[role="tab"]')].map((t) => t.getAttribute('aria-selected'))).toEqual(['true', 'false', 'false', 'false']);
      expect([...host.querySelectorAll('[role="tab"]')].map((t) => t.getAttribute('tabindex'))).toEqual(['0', '-1', '-1', '-1']);
      expect(host.querySelector('bp-tabs')!.classList).toContain('bp-tabs--pills');
      expect(tab(host, 'active').querySelector('.bp-tab__icon')?.getAttribute('aria-hidden')).toBe('true');

      tab(host, 'draft').click();
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('draft');
      expect(tab(host, 'draft').getAttribute('aria-selected')).toBe('true');
      expect(tab(host, 'draft').classList).toContain('bp-tab--on');
      expect(tab(host, 'all').getAttribute('aria-selected')).toBe('false');
    });

    it('стрілки, Home і End переводять фокус і вибір, оминаючи вимкнені', async () => {
      const { fixture, host } = await setup();
      key(host, 'ArrowRight');
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('active');
      expect(document.activeElement).toBe(tab(host, 'active'));

      key(host, 'ArrowRight');
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('draft');
      expect(tab(host, 'draft').getAttribute('tabindex')).toBe('0');

      key(host, 'ArrowRight');
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('all');

      key(host, 'ArrowLeft');
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('draft');

      key(host, 'Home');
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('all');

      key(host, 'End');
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('draft');
      expect(tab(host, 'off').hasAttribute('disabled')).toBe(true);
    });

    it('інші клавіші вибору не змінюють', async () => {
      const { fixture, host } = await setup();
      key(host, 'a');
      fixture.detectChanges();
      expect(fixture.componentInstance.value()).toBe('all');
    });

    it('при переповненні показує кнопки прокрутки', async () => {
      const { fixture, host } = await setup();
      expect(host.querySelector('[data-testid="tabs-scroll-end"]')).toBeNull();
      const list = host.querySelector<HTMLElement>('.bp-tabs__list')!;
      Object.defineProperty(list, 'scrollWidth', { configurable: true, value: 600 });
      Object.defineProperty(list, 'clientWidth', { configurable: true, value: 200 });
      list.dispatchEvent(new Event('scroll'));
      fixture.detectChanges();
      expect(host.querySelector('bp-tabs')!.classList).toContain('bp-tabs--overflow');
      expect(host.querySelector<HTMLButtonElement>('[data-testid="tabs-scroll-start"]')!.disabled).toBe(true);
      expect(host.querySelector<HTMLButtonElement>('[data-testid="tabs-scroll-end"]')!.disabled).toBe(false);
      const scrollBy = vi.fn();
      list.scrollBy = scrollBy;
      host.querySelector<HTMLButtonElement>('[data-testid="tabs-scroll-end"]')!.click();
      expect(scrollBy).toHaveBeenCalledWith(expect.objectContaining({ left: 160 }));

      Object.defineProperty(list, 'scrollLeft', { configurable: true, value: 400 });
      list.dispatchEvent(new Event('scroll'));
      fixture.detectChanges();
      expect(host.querySelector<HTMLButtonElement>('[data-testid="tabs-scroll-start"]')!.disabled).toBe(false);
      expect(host.querySelector<HTMLButtonElement>('[data-testid="tabs-scroll-end"]')!.disabled).toBe(true);
    });
  });

  describe('режим посилань', () => {
    async function setup(url: string) {
      TestBed.configureTestingModule({
        providers: [provideRouter([{ path: 'a', component: BlankPage }, { path: 'b', component: BlankPage }, { path: 'c', component: BlankPage }])],
      });
      const fixture = TestBed.createComponent(LinkHost);
      await TestBed.inject(Router).navigateByUrl(url);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      return { fixture, host: fixture.nativeElement as HTMLElement };
    }

    it('активна вкладка — за адресою, з tabindex=0', async () => {
      const { host } = await setup('/b');
      expect(tab(host, 'b').tagName).toBe('A');
      expect(tab(host, 'b').getAttribute('href')).toBe('/b');
      expect(tab(host, 'b').getAttribute('aria-selected')).toBe('true');
      expect(tab(host, 'a').getAttribute('aria-selected')).toBe('false');
      expect([...host.querySelectorAll('[role="tab"]')].map((t) => t.getAttribute('tabindex'))).toEqual(['-1', '0', '-1']);
    });

    it('стрілки лише переводять фокус, адреса не змінюється', async () => {
      const { fixture, host } = await setup('/b');
      key(host, 'ArrowRight');
      fixture.detectChanges();
      expect(document.activeElement).toBe(tab(host, 'c'));
      expect(tab(host, 'c').getAttribute('tabindex')).toBe('0');
      expect(TestBed.inject(Router).url).toBe('/b');
    });

    it('клік відкриває вкладку', async () => {
      const { fixture, host } = await setup('/a');
      tab(host, 'c').click();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(TestBed.inject(Router).url).toBe('/c');
      expect(tab(host, 'c').getAttribute('aria-selected')).toBe('true');
    });
  });
});
