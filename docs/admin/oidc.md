# Вхід через OIDC: Keycloak і Microsoft Entra ID

OpenBP вміє пускати користувачів через корпоративний вхід (OpenID Connect) замість окремого пароля. Після входу
користувач отримує ту саму сесію, що й після пароля, з роллю viewer, editor або admin. Провайдерів може бути кілька:
на сторінці входу для кожного з'являється кнопка «Увійти через …».

## Як це працює

1. Користувач натискає «Увійти через Кіт 2» і логіниться на сторінці провайдера.
2. Провайдер повертає браузер на `https://<OpenBP>/signin-oidc/<key>`. OpenBP перевіряє відповідь (authorization code
   + PKCE) і шукає користувача:
   - за обліковим записом провайдера (`sub`), якщо користувач уже входив цим провайдером;
   - інакше за поштою, якщо провайдер її підтвердив (тоді обліковий запис прив'язується до наявного користувача);
   - інакше створює нового користувача без пароля, якщо для провайдера ввімкнено `AutoProvision`.
3. Якщо задано `RoleMap`, роль виставляється з ролей провайдера при кожному вході. Так зняття ролі в Keycloak чи Entra
   ID забирає права в OpenBP з наступним входом. Аварійного адміністратора `OPENBP_ADMIN_EMAIL` це не стосується.

Якщо вхід відхилено, користувач повертається на сторінку входу з поясненням:

| Код | Що сталося | Що робити |
|---|---|---|
| `OIDC_EMAIL_NOT_ALLOWED` | Домен пошти не входить у `AllowedEmailDomains`. | Додати домен або не пускати цього користувача. |
| `OIDC_EMAIL_NOT_VERIFIED` | Провайдер не підтвердив пошту, а користувача з цим `sub` ще немає. | Підтвердити пошту в провайдері; для Entra ID — `TrustEmail=true`. |
| `OIDC_USER_NOT_FOUND` | Користувача немає, а `AutoProvision` вимкнено. | Створити користувача з цією поштою або ввімкнути `AutoProvision`. |
| `OIDC_ACCOUNT_CONFLICT` | Користувача з цією поштою вже прив'язано до іншого облікового запису цього провайдера. | Розібратися, чий це обліковий запис; прив'язки лежать у таблиці `user_logins`. |
| `OIDC_FAILED` | Помилка протоколу: скасований вхід, прострочений запит, невірний секрет клієнта. | Подивитися журнал api: там провайдер і тип помилки. |

## Налаштування

Провайдери задаються змінними `OpenBp__Oidc__Providers__<key>__<Поле>`. У `docker-compose.prod.yml` їх читає сервіс `api`
з файлу `oidc.env` поруч із compose-файлом. Зразок лежить у [`oidc.env.example`](../../oidc.env.example):

```bash
cp oidc.env.example oidc.env
chmod 600 oidc.env          # тут секрет клієнта
nano oidc.env
docker compose -f docker-compose.prod.yml up -d api
```

`<key>` — латиниця в нижньому регістрі, цифри й дефіс, наприклад `kit` чи `entra`. Він стає частиною redirect URI, тож
після реєстрації клієнта його не змінюють.

| Поле | Обов'язкове | Опис |
|---|---|---|
| `DisplayName` | ні | Текст кнопки: «Увійти через `DisplayName`». Без нього — ключ. |
| `Authority` | так | Issuer. Keycloak: `https://<sso>/realms/<realm>`. Entra ID: `https://login.microsoftonline.com/<tenant-id>/v2.0`. |
| `ClientId` | так | Client id клієнта OpenBP у провайдера. |
| `ClientSecret` | так для confidential-клієнта | Секрет клієнта. У журнали й відповіді Api не потрапляє. |
| `Scopes__0`, `Scopes__1`… | ні | Scopes понад `openid`. Типово `profile email`. |
| `RoleClaim` | ні | Де лежать ролі, через кому. Keycloak: `realm_access.roles`, `resource_access.<client>.roles`. Entra ID: `roles`. |
| `RoleMap__admin__0`, `RoleMap__editor__0`… | ні | Які ролі провайдера дають роль OpenBP. Діє найвища зі збігів, без збігу — viewer. Без `RoleMap` роль задає адміністратор OpenBP, а нові користувачі отримують viewer. |
| `AutoProvision` | ні | `true` — створювати користувача при першому вході. Типово `false`. |
| `AllowedEmailDomains__0`… | ні | Дозволені домени пошти, наприклад `astarta.ua`. Перевіряються на кожному вході. Порожньо — будь-які. |
| `TrustEmail` | ні | Вважати пошту підтвердженою без claim `email_verified`. Для Entra ID одного тенанта. Для провайдерів, де користувач сам вписує пошту, не вмикати. |
| `AcceptBearer` | ні | Приймати access token провайдера як `Authorization: Bearer` (вбудований редактор, REST). Типово `true`. |
| `BearerAudiences__0`… | ні | Допустимі `aud` або `azp` bearer-токенів. Типово лише `ClientId`. Для токенів іншого клієнта (наприклад, `KitWeb`) додайте його сюди. |
| `RequireHttpsMetadata` | ні | `false` лише для локального стенда з провайдером на http. |

Інші змінні:

- `OPENBP_PUBLIC_URL` — публічна адреса OpenBP (`https://openbp.example.com`). З неї будується redirect URI, коли OpenBP
  стоїть за reverse proxy з HTTPS. Без неї береться адреса запиту, і за проксі вона буде `http://…`, а провайдер такий
  redirect URI відхилить.
- `OPENBP_PASSWORD_LOGIN=false` ховає форму пароля і вимикає вхід паролем для всіх, крім `OPENBP_ADMIN_EMAIL`. Аварійний
  адміністратор входить через `https://<OpenBP>/login?password`.

Помилка в налаштуваннях (немає `Authority` чи `ClientId`, недопустимий ключ) зупиняє старт api з поясненням у журналі:
`docker compose -f docker-compose.prod.yml logs api`.

Не знижуйте рівень журналу `Microsoft.AspNetCore` нижче `Warning` у продакшні: на рівні `Information` у журнал
потрапляють адреси запитів, а в адресі callback'а є одноразовий код входу.

## Keycloak «Кіт 2» (realm `astarta`)

У Кіті OpenBP нічого не змінює. Клієнта для OpenBP створює власник Keycloak:

1. **Clients → Create client**: Client type `OpenID Connect`, Client ID `openbp`.
2. **Capability config**: Client authentication **On** (confidential), Standard flow **On**, решта вимкнено.
3. **Login settings**:
   - Valid redirect URIs: `https://<OpenBP>/signin-oidc/kit`;
   - Web origins: `https://<OpenBP>`.
4. **Advanced → Proof Key for Code Exchange**: `S256`.
5. **Credentials**: скопіюйте Client secret в `OpenBp__Oidc__Providers__kit__ClientSecret`.
6. Ролі. Варіант A — ролі клієнта: **Clients → openbp → Roles** `openbp-admin`, `openbp-editor`, призначити користувачам
   чи групам; `RoleClaim=resource_access.openbp.roles`. Варіант B — ролі realm'у: `RoleClaim=realm_access.roles`.
7. Пошта має бути підтверджена (`Email verified`) — інакше без `AutoProvision` і наявного користувача вхід відхиляється
   з `OIDC_EMAIL_NOT_VERIFIED`.

Вбудований редактор `<openbp-canvas>` у Кіті може передавати access token Кіта (клієнт `KitWeb`) замість API-ключа
(див. [embedding.md](../embedding.md)). Для цього достатньо `BearerAudiences__1=KitWeb`: OpenBP перевіряє підпис токена
ключами realm'у і `azp`, а користувача шукає за тими самими правилами, що й при вході.

## Microsoft Entra ID

1. **Entra ID → App registrations → New registration**:
   - Supported account types: «Accounts in this organizational directory only»;
   - Redirect URI: Web, `https://<OpenBP>/signin-oidc/entra`.
2. **Certificates & secrets → New client secret** → `ClientSecret`. Секрет має строк дії: поставте нагадування.
3. **App roles → Create app role**: `OpenBP.Admin`, `OpenBP.Editor` (Allowed member types: Users/Groups). Призначте їх у
   **Enterprise applications → OpenBP → Users and groups**.
4. **Token configuration → Add optional claim → ID → `email`** (і за бажання `xms_edov`).
5. У `oidc.env`: `Authority=https://login.microsoftonline.com/<tenant-id>/v2.0` (саме id тенанта, не `common`),
   `ClientId=<Application (client) ID>`, `RoleClaim=roles`, `RoleMap…`, `TrustEmail=true`.

Entra ID не надсилає `email_verified`. `TrustEmail=true` безпечний для одного тенанта: пошту там задає адміністратор
тенанта. Якщо claim `email` порожній, OpenBP бере UPN (`upn` або `preferred_username`).

## Перевірка

- `curl https://<OpenBP>/api/v1/auth/providers` → `{"passwordLogin":…,"providers":[{"key":"kit",…}]}`.
- Відкрийте `https://<OpenBP>/login`, натисніть кнопку провайдера й увійдіть. У правому верхньому куті має бути ваша
  пошта, а роль — як у `RoleMap`.
- Вихід («Вийти») закриває сесію OpenBP. Сесія в самому провайдері лишається: наступний вхід може пройти без пароля.
