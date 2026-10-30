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

## Типографіка

Джерело — Figma «Typography - Desktop» `1710:6146`, шрифт Ubuntu. Токени у `apps/web/src/theme/_typography.scss` (shorthand `font`), класи `.bp-h1…` і `.bp-p4`, ті самі розміри для `h1…h6` і Tailwind `text-*`.

| Токен / клас | Вага · розмір/міжряддя | Де |
|---|---|---|
| `--bp-font-h1` | 700 · 28/34 | заголовок екрана-входу, великі числа |
| `--bp-font-h2` | 700 · 26/34 | `text-3xl` |
| `--bp-font-h3` | 500 · 24/29 | заголовок сторінки (`.mat-font-headline-sm`), `text-2xl` |
| `--bp-font-h4` | 500 · 20/28 | заголовок діалогу, `text-xl` |
| `--bp-font-h6` | 500 · 16/20 | заголовок блоку |
| `--bp-font-h7` | 500 · 14/18 | вкладки, пункти меню, підписи кнопок |
| `--bp-font-p1` | 400 · 18/22 | `text-lg` |
| `--bp-font-p2` | 400 · 16/20 | `text-base` |
| `--bp-font-p3` | 400 · 14/20 | основний текст, `text-sm` |
| `--bp-font-p4` | 400 · 12/16 | підписи, заголовки колонок, `text-xs` |

## Іконки

Один набір — Material Symbols Outlined, 20 px (`--bp-icon-size`), товщина 400, без заливки; 24 px (`.bp-icon--lg`) — лише в меню й круглих кнопках дій. SVG з Figma не тягнемо (асети заблоковані, файл лише для читання), тому іконки Кіт зіставлено з Material Symbols:

| Кіт CRM (Figma «Icons» `1710:5456`) | Material Symbols |
|---|---|
| Trash | `delete` |
| Filter | `filter_list` |
| save / upload / Import | `save` / `upload` / `download` |
| copy | `content_copy` |
| edit | `edit` |
| Calendar | `calendar_month` |
| Share | `share` |
| Search | `search` |
| Settings / Settings 2 / tool | `settings` / `tune` / `build` |
| `...` / vertical `...` | `more_horiz` / `more_vert` |
| reload / Refresh | `refresh` |
| Drag | `drag_indicator` |
| Menu: BP / Sell / Service / Directory | `account_tree` / `shopping_bag` / `support_agent` / `menu_book` |
| Bell on / bell off | `notifications` / `notifications_off` |
| Arrows up/down/left/right | `keyboard_arrow_up/down/left/right` |
| Double arrow | `keyboard_double_arrow_right/left` |
| Navigation: Arrow left / × / Check / + | `arrow_back` / `close` / `check` / `add` |
| Input: success / alert / Show / Hide / Loading / clear | `check_circle` / `error` / `visibility` / `visibility_off` / `progress_activity` / `cancel` |
| Priority: Low / Medium / High / Critical | `keyboard_arrow_down` / `drag_handle` / `keyboard_arrow_up` / `keyboard_double_arrow_up` |
| Task / Unchecked / ±circle | `task_alt` / `check_box_outline_blank` / `remove_circle` / `add_circle` |
| Star_save | `star` |
| Info / mail / phone-call / link / folder / File / Description | `info` / `mail` / `call` / `link` / `folder` / `description` / `article` |
| Chat / send / mic / smile | `chat` / `send` / `mic` / `sentiment_satisfied` |
| Magic / rocket / zap / Integration | `auto_awesome` / `rocket_launch` / `bolt` / `extension` |
| home / Company / Contacts / group | `home` / `apartment` / `contacts` / `group` |
| Line menu / maximize / minimize | `menu` / `open_in_full` / `close_fullscreen` |

## Поля, списки, перемикачі (F9-05)

Стилі — `apps/web/src/theme/_fields.scss` (`fields.overrides()` — токени Material, `fields.global()` — правила без токенів), без правки шаблонів. Figma: «FIELDS» `2605:13160`, «DROP WINDOW» `2322:8183`, «Atoms / Selectors» `1710:6019`, «Calendars» `3811:88118`.

- Поле `appearance="outline"`: висота 40, радіус 8, рамка `--bp-line`; hover — `--bp-primary`; focus — рамка primary + кільце `--bp-tonal`; error — червона рамка й кільце; disabled — фон `--bp-bg`.
- Додаткові стани — класи на `mat-form-field`: `bp-field-success` (зелена рамка), `bp-field-loading` (смуга завантаження), `bp-search-secondary` (вторинний пошук на `--bp-bg-2`).
- Панель `mat-select`/автодоповнення: радіус 8, рамка line, тінь AC-Kit; пункт 40 px, вибраний — `--bp-tonal`; вкладений рівень — клас `bp-option-nested`, порожній стан — `bp-option-empty`.
- Checkbox, radio, switch — primary / `--bp-line-3`; календар і діапазон дат — вибрана дата primary, діапазон `--bp-tonal`.
