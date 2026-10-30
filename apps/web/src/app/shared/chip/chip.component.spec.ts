import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ChipComponent, ChipPriority, ChipTone, priorityTone } from './chip.component';

@Component({
  imports: [ChipComponent],
  template: `<bp-chip
    [tone]="tone()"
    [priority]="priority()"
    [removable]="removable()"
    (removed)="count.set(count() + 1)"
    >Текст</bp-chip
  >`,
})
class HostComponent {
  readonly tone = signal<ChipTone>('green');
  readonly priority = signal<ChipPriority | null>(null);
  readonly removable = signal(false);
  readonly count = signal(0);
}

describe('ChipComponent', () => {
  function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('ставить клас кольору за tone і показує текст', () => {
    const f = setup();
    const chip = f.nativeElement.querySelector('bp-chip') as HTMLElement;
    expect(chip.dataset['tone']).toBe('green');
    expect(chip.classList.contains('bp-chip--green')).toBe(true);
    expect(chip.textContent).toContain('Текст');
    expect(chip.querySelector('.bp-chip__dot')).toBeNull();
  });

  it('пріоритет задає колір і крапку', () => {
    const f = setup();
    f.componentInstance.priority.set('critical');
    f.detectChanges();
    const chip = f.nativeElement.querySelector('bp-chip') as HTMLElement;
    expect(chip.dataset['tone']).toBe('red');
    expect(chip.querySelector('.bp-chip__dot')).not.toBeNull();
    expect(priorityTone('low')).toBe('gray');
  });

  it('мітка із закриттям видає removed', () => {
    const f = setup();
    f.componentInstance.removable.set(true);
    f.detectChanges();
    (f.nativeElement.querySelector('[data-testid="chip-remove"]') as HTMLButtonElement).click();
    expect(f.componentInstance.count()).toBe(1);
  });

  it('без removable кнопки закриття немає', () => {
    const f = setup();
    expect(f.nativeElement.querySelector('[data-testid="chip-remove"]')).toBeNull();
  });
});
