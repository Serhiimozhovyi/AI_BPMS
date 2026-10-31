import { getDebugNode } from '@angular/core';
import { MatSelect } from '@angular/material/select';

/** Тестовий хелпер (F9-14): екземпляр `MatSelect` за його елементом `<mat-select>`. */
export function matSelectOf(element: Element): MatSelect {
  const select = getDebugNode(element)?.injector.get(MatSelect, null);
  if (!select) {
    throw new Error('Елемент не є <mat-select>');
  }
  return select;
}

/** Поточне значення `<mat-select>` (замінник `HTMLSelectElement.value` у специфікаціях). */
export function selectValue(element: Element): unknown {
  return matSelectOf(element).value;
}

/** Значення всіх пунктів списку в порядку показу. */
export function selectOptionValues(element: Element): unknown[] {
  return matSelectOf(element).options.map((option) => option.value);
}

/**
 * Вибирає пункт зі значенням `value` так, як це зробив би користувач (подія `selectionChange`), — замінник
 * `select.value = …; select.dispatchEvent(new Event('change'))` для `<mat-select>`.
 */
export function chooseOption(element: Element, value: unknown): void {
  const select = matSelectOf(element);
  const option = select.options.find((o) => o.value === value);
  if (!option) {
    const known = select.options.map((o) => String(o.value)).join(', ');
    throw new Error(`Пункту «${String(value)}» немає у списку (є: ${known})`);
  }
  option._selectViaInteraction();
}

/** Усі `<mat-select>` усередині елемента (замінник `querySelectorAll('select')`). */
export function matSelectsIn(root: ParentNode): Element[] {
  return Array.from(root.querySelectorAll('mat-select'));
}
