# Дизайн-система AI_BPMS

Компоненти й розміри беремо з Figma **AC-Kit** (Кіт CRM): https://www.figma.com/design/LiFumJJDQgtYQXektIe12P/AC-Kit, сторінка «🎨UI Kit» `1710:5369`. **Figma лише читаємо** — нічого там не створюємо й не змінюємо. Палітра власна — «Глибока бірюза» (вибір власника 2026-10-30), порівняння: https://claude.ai/artifact/QwjgMr4eRtRghEUk4FKmkT

## Токени

Джерело — `apps/web/src/theme/_openbp-theme.scss` (CSS-змінні `--bp-*`), для Tailwind — `apps/web/src/tailwind.css` (`bg-bp-primary`, `text-bp-text-muted` …).

| Токен | Значення | Роль |
|---|---|---|
| `--bp-primary` | `#0D736D` | кнопки, посилання, активна вкладка (5,7:1 на білому) |
| `--bp-primary-hover` | `#095A56` | наведення |
| `--bp-primary-pressed` | `#074744` | натискання |
| `--bp-accent` | `#27C7BE` | підкреслення меню, логотип, акценти **на темному**; не для тексту на білому (2,1:1) |
| `--bp-shell` | `#0C2B30` | верхнє меню |
| `--bp-shell-2` | `#12404A` | вторинний темний |
| `--bp-tonal` | `#DDF3F1` | тональні кнопки, вибраний сегмент, кільце фокуса (текст primary на ньому 4,9:1) |
| `--bp-bg` / `--bp-bg-2` | `#F2F6F6` / `#E9F4F3` | фон сторінки / вибраний рядок, неактивні етапи |
| `--bp-surface-2` | `#F8FBFB` | другий рівень поверхні |
| `--bp-line` / `-2` / `-3` | `#C3CFCE` / `#DBE6E5` / `#B4C4C3` | рамки, лінії таблиць, декор |
| `--bp-text` | `#0C2B30` | основний текст |
| `--bp-text-muted` | `#57696B` | підписи, заголовки колонок (5,8:1) |

Статусні кольори (зелений, помаранчевий, червоний, жовтий, синій і їхні 15%-варіанти) лишаються з AC-Kit як `--ac-green`, `--ac-red`, … Текст на 15%-фоні має бути затемненим відтінком (≥ 4,5:1).

## Перехідні назви

`--ac-*` (і Tailwind `ac-*`) — аліаси на `--bp-*` до задачі F9-11, яка їх прибирає. У новому коді використовуйте `--bp-*`.

## Правила

- Усі текстові пари проходять WCAG AA (axe-перевірка `a11y.e2e.ts` лишається зеленою).
- Жорстких hex-кольорів у компонентах немає: тільки токени (у `var(--x, fallback)` fallback дорівнює значенню токена).
- Тіні — `rgb(12 43 48 / …)`.
- Стилі кожного компонента — окремим partial у `apps/web/src/theme/` (`_buttons.scss`, `_tabs.scss` …).

## Кнопки (F9-02)

Figma «Atoms / Buttons» `1710:6222`. Стилі — `apps/web/src/theme/_buttons.scss` поверх Material, окремого компонента-обгортки немає.

| Вигляд | Розмітка | Коли |
|---|---|---|
| filled | `mat-flat-button` | одна головна дія на екрані |
| tonal | `mat-flat-button bpVariant="tonal"` | другорядні дії поруч з головною |
| outlined | `mat-stroked-button` | рівноцінні другорядні дії |
| text | `mat-button` | третьорядні, «Скасувати» |
| danger | `bpVariant="danger"` | видалення й підтвердження видалення |
| іконкова | `mat-icon-button` (`bpVariant="close"` — ×) | дії без підпису; потрібен `aria-label` |

Малий розмір (32 px замість 40 px) — `bpSize="sm"`. Директива `BpButtonDirective` (`shared/button.directive.ts`) додає лише класи `bp-btn-*`; її треба додати в `imports` компонента. Стани: hover, pressed, disabled, focus-visible (кільце `--bp-tonal`). Приклад усіх варіантів — `/dev/kit` (лише dev-збірка).

## Вкладки (`bp-tabs`, F9-03)

Figma «Atoms / Tabs» `3251:37133`. Компонент `apps/web/src/app/shared/tabs/tabs.component.ts`, стилі — `apps/web/src/theme/_tabs.scss`.

| Вид | `variant` | Де | Активна |
|---|---|---|---|
| Сегментні «пігулки» | `pills` | фільтри й перемикачі виду (період в «Огляді», вид і виходи даних вузла) | фон `--bp-tonal`, текст `--bp-primary` |
| Підкреслені | `underline` (типовий) | розділи сторінки (проєкт, застосунок, «Підключення», панель вузла) | текст і підкреслення 2px `--bp-primary` |
| Зі значком | `icon` | вкладки картки («Підключити ШІ-агента») | фон `--bp-tonal`, текст `--bp-primary` |

- Режим посилань: у вкладки є `link` (+ `queryParams`), активну визначає адреса. Режим значення: `[(value)]`.
- ARIA `tablist`/`tab`, `aria-selected`, одна вкладка в порядку Tab; ←/→ (↑/↓), Home/End. У режимі значення стрілки одразу вибирають вкладку, у режимі посилань — лише фокусують.
- Коли вкладки не вміщаються — горизонтальна прокрутка й кнопки ‹ ›.
- Модифікатори: `bp-tabs--stretch` (рівні частки на всю ширину), `bp-tabs--sm` (дрібні пігулки).
- Кожна вкладка має `data-tab="<value>"`, `testId` — для тестів.

## Поля, списки, перемикачі (F9-05)

Стилі — `apps/web/src/theme/_fields.scss` (`fields.overrides()` — токени Material, `fields.global()` — правила без токенів), без правки шаблонів. Figma: «FIELDS» `2605:13160`, «DROP WINDOW» `2322:8183`, «Atoms / Selectors» `1710:6019`, «Calendars» `3811:88118`.

- Поле `appearance="outline"`: висота 40, радіус 8, рамка `--bp-line`; hover — `--bp-primary`; focus — рамка primary + кільце `--bp-tonal`; error — червона рамка й кільце; disabled — фон `--bp-bg`.
- Додаткові стани — класи на `mat-form-field`: `bp-field-success` (зелена рамка), `bp-field-loading` (смуга завантаження), `bp-search-secondary` (вторинний пошук на `--bp-bg-2`).
- Панель `mat-select`/автодоповнення: радіус 8, рамка line, тінь AC-Kit; пункт 40 px, вибраний — `--bp-tonal`; вкладений рівень — клас `bp-option-nested`, порожній стан — `bp-option-empty`.
- Checkbox, radio, switch — primary / `--bp-line-3`; календар і діапазон дат — вибрана дата primary, діапазон `--bp-tonal`.

## Таблиці й панель дій (F9-07)

Figma «Atoms / Table» `2815:14396`, екран «Реєстр продажів» `3499:77865`. Стилі — `apps/web/src/theme/_tables.scss`, діють на всі `main table.w-full`.

- Шапка `--bp-surface-2`, текст `--bp-text-muted` 12 px; рядки в рамці `--bp-line`; наведення й вибраний рядок — `--bp-bg-2` (вибраний — клас `bp-row-selected` або `aria-selected="true"`, ще смуга `--bp-primary` зліва).
- Колонка галочок — клас `bp-col-check` на `th`/`td`. Вертикальні лінії — клас `bp-table--grid` на таблиці. Липка шапка — таблиця в контейнері `bp-table-wrap` (прокручується).
- Картковий вигляд: на телефоні (< 768 px) для `table[appCardTable]` (F3-85) і за перемикачем — клас `bp-cards` на такій таблиці.
- Панель над таблицею — компонент `bp-table-toolbar` (`shared/table-toolbar/`): перемикач таблиця/картки (`[(view)]`), пошук (`[(search)]`, `searchPlaceholder`, `searchTestId`), кнопки дій у проєкції контенту, праворуч іконки фільтра, вивантаження й налаштувань (`showFilter`/`showExport`/`showSettings` → події `filter`/`exportData`/`settings`). Застосована у «Сценаріях»; решта списків отримує стилі таблиці, панель підключається за потреби.

## Модальні вікна, сповіщення, підказки, порожні стани (F9-08)

Figma «Atoms / Modal» `2607:14939` (Snackbars `7247:33465`), Tooltip `4101:72215`, кнопка закриття `4134:183358`. Стилі — `apps/web/src/theme/_overlays.scss`.

- Діалог: білий непрозорий контейнер, радіус 12, тінь, підкладка `rgb(12 43 48 / 0.5)`; заголовок 18/24 bold; `mat-dialog-actions` — праворуч, відступи 16/24, розділювач `--bp-line-2`. Кнопка закриття в куті — клас `bp-dialog-close` на `mat-icon-button` (`bpVariant="close"`).
- Сповіщення `NotifyService`: контейнер `--bp-shell`, білий текст, смуга стану зліва — `--bp-accent` для успіху й `--ac-red` для помилки; кнопка дії відповідно.
- Підказка `matTooltip`: фон `--bp-shell`, текст 12/16, радіус 6.
- `app-empty-state`: рамка `--bp-line`, іконка й дія primary, пояснення `--bp-text-muted`.
