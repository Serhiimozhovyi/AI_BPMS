import { Directive, HostBinding, Input } from '@angular/core';

export type BpButtonVariant = 'tonal' | 'danger' | 'close';
export type BpButtonSize = 'md' | 'sm';

/**
 * Варіанти кнопок AC-Kit поверх `mat-*-button` (F9-02, стилі — `theme/_buttons.scss`):
 * `bpVariant="tonal"` — тональна, `"danger"` — небезпечна дія (видалення), `"close"` — кнопка закриття (×);
 * `bpSize="sm"` — висота 32 px замість 40 px. Без обгортки-компонента: лише класи на хост-кнопці.
 */
// Атрибут `bpVariant` — частина дизайн-системи (як у документації), тож префікс `bp`, а не `app`.
// eslint-disable-next-line @angular-eslint/directive-selector
@Directive({ selector: 'button[bpVariant], a[bpVariant], button[bpSize], a[bpSize]' })
export class BpButtonDirective {
  @Input() bpVariant?: BpButtonVariant;
  @Input() bpSize: BpButtonSize = 'md';

  @HostBinding('class.bp-btn-tonal') get tonal(): boolean {
    return this.bpVariant === 'tonal';
  }

  @HostBinding('class.bp-btn-danger') get danger(): boolean {
    return this.bpVariant === 'danger';
  }

  @HostBinding('class.bp-btn-close') get close(): boolean {
    return this.bpVariant === 'close';
  }

  @HostBinding('class.bp-btn-sm') get small(): boolean {
    return this.bpSize === 'sm';
  }
}
