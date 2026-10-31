import { Locator, expect } from '@playwright/test';

/**
 * Вибір пункту у випадному списку (`mat-select`, F9-14): відкрити список і клацнути пункт за підписом —
 * так само, як користувач. Замінник `selectOption` для нативного `<select>`.
 */
export async function chooseOption(select: Locator, label: string | RegExp): Promise<void> {
  await select.click();
  const option = select.page().getByRole('option', { name: label, exact: typeof label === 'string' });
  await option.click();
  await expect(option).toHaveCount(0);
}
