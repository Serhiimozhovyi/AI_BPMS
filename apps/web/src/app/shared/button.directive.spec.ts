import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BpButtonDirective } from './button.directive';

@Component({
  imports: [BpButtonDirective],
  template: `
    <button id="plain" type="button">Звичайна</button>
    <button id="tonal" type="button" bpVariant="tonal">Тональна</button>
    <button id="danger" type="button" bpVariant="danger" bpSize="sm">Видалити</button>
    <button id="close" type="button" bpVariant="close" aria-label="Закрити">×</button>
  `,
})
class HostComponent {}

describe('BpButtonDirective', () => {
  function btn(id: string): HTMLElement {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('#' + id)!;
  }

  it('без атрибутів директива не чіпає кнопку', () => {
    expect(btn('plain').className).toBe('');
  });

  it('bpVariant="tonal" додає клас tonal', () => {
    const el = btn('tonal');
    expect(el.classList.contains('bp-btn-tonal')).toBe(true);
    expect(el.classList.contains('bp-btn-danger')).toBe(false);
    expect(el.classList.contains('bp-btn-sm')).toBe(false);
  });

  it('bpVariant="danger" і bpSize="sm" додають класи danger та sm', () => {
    const el = btn('danger');
    expect(el.classList.contains('bp-btn-danger')).toBe(true);
    expect(el.classList.contains('bp-btn-sm')).toBe(true);
  });

  it('bpVariant="close" додає клас закриття', () => {
    expect(btn('close').classList.contains('bp-btn-close')).toBe(true);
  });
});
