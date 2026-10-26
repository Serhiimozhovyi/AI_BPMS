# Вбудовування редактора: `<openbp-canvas>`

Редактор сценарію OpenBP можна вбудувати на сторінку іншого застосунку як веб-компонент `<openbp-canvas>`.
Усередині ті самі канвас, палітра, панель вузла і тестовий запуск, що й у SPA, але без роутера, шапки й
навігації. Компонент несе власний Angular, тож хост може бути на іншій версії Angular (наприклад, 16 у
«Кіт 2») або взагалі без Angular. Розмітка й стилі живуть у Shadow DOM: стилі хоста не ламають канвас, а
стилі канвасу не потрапляють на сторінку.

## Збірка

```bash
cd apps/web
npm ci
npm run build:element      # → dist/element/openbp-canvas.js (один файл, CSS усередині)
```

Розмір: близько 1 МБ, у gzip близько 275 КБ. Скрипт друкує точні значення після збірки.

Образ `web` (docker/web.Dockerfile) уже містить файл: він доступний за адресою `<OpenBP>/element/openbp-canvas.js`
з `Cache-Control: no-cache`. Скрипт підключається звичайним `<script>`, тож його можна віддавати й з будь-якого
іншого статичного хостингу.

## Атрибути і властивості

| Атрибут | Властивість | Опис |
|---|---|---|
| `workflow-id` | `workflowId` | Id сценарію. Зміна перезавантажує редактор, а незбережені зміни попереднього сценарію відправляються. |
| `api-base` | `apiBase` | Корінь Api, напр. `https://openbp.example.com`. Порожньо — той самий origin, що й сторінка. |
| `token` | `token` | API-ключ OpenBP або OIDC access token для `Authorization: Bearer`. Без нього — cookie сесії. |
| `readonly` | `readonly` | Лише перегляд: без палітри, редагування, запусків і збереження; вузли можна виділяти й дивитися. |
| `locale` | `locale` | Мова інтерфейсу. Зараз є лише `uk`, інші значення показують українську. |
| `view` | `view` | `business` разом з `execution-id` — етапи виконання замість редактора (F7-05). Інше значення або без `execution-id` — редактор сценарію. |
| `execution-id` | `executionId` | Виконання для `view="business"`. Елемент малює етапи колонками: стан, строк, виконавця, порушення правил, невиконані умови наступного переходу й дії, доступні користувачу з `token`/cookie: рішення задачі (поля й кнопка на кожне рішення), відхилення правила з причиною, «Продовжити» для очікування вебхука. Причину елемент просить, коли сервер відповідає 422 `STAGE_RULE_REASON_REQUIRED`. Усе — з контракту `GET /api/v1/executions/{id}/stages` і `POST …/stages/{stageId}/actions/{actionId}`, тих самих, що `get_execution_stages`/`act_on_stage` у `/mcp`. Виконання за об'єктом своєї системи хост знаходить через `GET /api/v1/executions?correlationKey=`. |

`api-base` і `token` спільні для всіх `<openbp-canvas>` на сторінці: діє останнє встановлене значення.

## Події

Події — звичайні `CustomEvent`, а дані лежать у `event.detail`:

| Подія | `detail` | Коли |
|---|---|---|
| `openbp-saved` | `{ workflowId, version }` | Автозбереження записало нову версію сценарію. |
| `openbp-run` | `{ executionId }` | Почався тестовий запуск («Виконати» або «Виконати вузол»). |
| `openbp-action` | `{ executionId, stageId, actionId, kind, decision }` | Дію етапу виконано (`view="business"`): `kind` — `task`, `override` або `resume`, `decision` — ключ рішення задачі. |
| `openbp-error` | `{ code, message }` | Не вдалося завантажити, зберегти чи запустити, а у `view="business"` — завантажити етапи чи виконати дію (`STAGE_RULE_REASON_REQUIRED`, `TASK_CLOSED`…). `code` — код Api (`NOT_FOUND`, `FORBIDDEN`, `WORKFLOW_INVALID`…), `NETWORK_ERROR` (Api недоступний) або `RUN_FAILED`. |

## Чистий HTML

```html
<script src="https://openbp.example.com/element/openbp-canvas.js" defer></script>

<openbp-canvas
  api-base="https://openbp.example.com"
  workflow-id="0199a0c4-…"
  style="display:block; height: 70vh"
></openbp-canvas>

<script>
  const canvas = document.querySelector('openbp-canvas');
  canvas.token = 'obp_…'; // API-ключ або OIDC access token; не вписуйте ключ у розмітку
  canvas.addEventListener('openbp-saved', (e) => console.log('версія', e.detail.version));
  canvas.addEventListener('openbp-error', (e) => alert(e.detail.message));
</script>
```

Висоту задає хост: елемент займає 100% висоти свого блоку, мінімум 480 px.

## Angular 16 («Кіт 2»)

1. Підключіть скрипт глобально: `angular.json` → `scripts` або `<script>` в `index.html`.
2. Дозвольте невідомі теги в модулі чи standalone-компоненті, де використовується редактор:

```ts
import { CUSTOM_ELEMENTS_SCHEMA, NgModule } from '@angular/core';

@NgModule({
  declarations: [WorkflowEditorHostComponent],
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
})
export class WorkflowsModule {}
```

```html
<openbp-canvas
  [attr.api-base]="openbpUrl"
  [attr.workflow-id]="workflowId"
  [attr.readonly]="canEdit ? null : ''"
  [token]="accessToken"
  (openbp-saved)="onSaved($any($event).detail)"
  (openbp-error)="onError($any($event).detail)"
></openbp-canvas>
```

`token` краще передавати властивістю (`[token]`), а не атрибутом: так його не видно в DOM. Якщо OpenBP і хост
входять через той самий Keycloak, передавайте access token хоста: OpenBP приймає його як `Bearer`, коли провайдер
налаштовано з `AcceptBearer` і клієнт хоста (напр. `KitWeb`) є в `BearerAudiences` ([admin/oidc.md](admin/oidc.md)).
Токен живе кілька хвилин, тож після оновлення токена в хості передайте новий: `[token]` робить це сам.

## Автентифікація і CORS

- **Інший домен (рекомендовано):** `token` з API-ключем або OIDC access token. Origin сторінки хоста треба
  дозволити в Api змінною `OPENBP_CORS_ORIGINS` (через кому, без `/` у кінці):

  ```bash
  OPENBP_CORS_ORIGINS=https://kit.example.com,https://kit-test.example.com
  ```

  Потік подій виконання (SSE) з токеном іде через `fetch` із заголовком `Authorization`, бо браузерний
  `EventSource` заголовків не має.
- **Cookie сесії:** працює, коли редактор і OpenBP на тому самому сайті, наприклад `kit.example.com` і
  `openbp.example.com`. Cookie `openbp.auth` має `SameSite=Lax`, тож на сторінки іншого сайту браузер її не
  надішле. Для іншого origin того самого сайту теж потрібен `OPENBP_CORS_ORIGINS`.
- Ролі ті самі, що в UI: viewer бачить, editor редагує й запускає. Для перегляду без змін на будь-якій ролі
  використовуйте `readonly`.

## Що відрізняється від SPA

- Немає посилань на інші сторінки: «← Сценарії», «Журнал виконань».
- Код вузла `core.code` редагується звичайним текстовим полем без Monaco. Так елемент лишається одним файлом, а
  Monaco не тягне глобальні стилі й воркери в Shadow DOM.
- Шрифти Ubuntu і Material Symbols хост підключає сам (`fonts.googleapis.com`, як в `apps/web/src/index.html`).
  Без них діє запасний `sans-serif`.
- Діалоги й випадні списки відкриваються всередині Shadow DOM елемента, а не в `document.body`.

## Демо і перевірка

- `apps/web/element-demo/index.html` — сторінка-хост з навмисно «агресивними» стилями. Параметри адреси:
  `?workflow-id=…&api-base=…&token=…&readonly`, для етапів виконання — `?view=business&execution-id=…&api-base=…&token=…`.
- `npm run e2e:element` збирає елемент і проганяє Playwright на демо з підміненим Api. Перевіряються:
  - збереження з подією `openbp-saved`;
  - токен у запитах і SSE;
  - `readonly`;
  - ізоляція стилів в обидва боки;
  - діалог у Shadow DOM;
  - `openbp-error`.
