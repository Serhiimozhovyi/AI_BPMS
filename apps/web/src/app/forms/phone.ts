/**
 * Поле-телефон форми (F11-07, `format: "phone"`): маска під час введення і нормалізація до цифр — так само, як колонка
 * `format: "phone"` сторінок (F8-15) зберігає «+38 (067) 123-45-67» цифрами. Сервер нормалізує тим самим правилом
 * (`FormSchema.NormalizePhones`).
 */

/** Значення `format` поля-телефону. */
export const PHONE_FORMAT = 'phone';
/** Найменше й найбільше цифр у номері (E.164 — до 15). */
export const PHONE_MIN_DIGITS = 10;
export const PHONE_MAX_DIGITS = 15;

/** Лише цифри номера. */
export function normalizePhone(value: string): string {
  return value.replace(/\D/g, '').slice(0, PHONE_MAX_DIGITS);
}

/** Чи номер має допустиму кількість цифр. */
export function isPhone(value: string): boolean {
  const digits = value.replace(/\D/g, '');
  return digits.length >= PHONE_MIN_DIGITS && digits.length <= PHONE_MAX_DIGITS;
}

/**
 * Маска під час введення: український номер (`380…` або `0…`) — `+380 67 123 45 67`, інший — `+` і цифри. Порожнє —
 * порожнє. Маска не губить цифр: `normalizePhone(maskPhone(x)) === normalizePhone(x)` (для `0…` — з префіксом `38`).
 */
export function maskPhone(value: string): string {
  let digits = normalizePhone(value);
  if (!digits) return '';
  if (digits.startsWith('0')) digits = `38${digits}`.slice(0, PHONE_MAX_DIGITS);
  if (!digits.startsWith('380')) return `+${digits}`;
  const groups = [digits.slice(0, 3), digits.slice(3, 5), digits.slice(5, 8), digits.slice(8, 10), digits.slice(10, 12)].filter(Boolean);
  const rest = digits.slice(12);
  return `+${groups.join(' ')}${rest}`;
}
