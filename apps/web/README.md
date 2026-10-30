# apps/web — Angular SPA AI_BPMS

Angular 22 (standalone, Signals, zoneless), Angular Material + Tailwind 4, ESLint (angular-eslint), Vitest.
Мова інтерфейсу за замовчуванням — українська: `@angular/localize`, `sourceLocale: uk`, тексти з i18n-ключами (`i18n="@@ключ"`, `$localize`).

Потрібен Node.js ≥ 24.15 (вимога Angular CLI 22).

```bash
npm ci
npm run lint
npm test          # одноразовий прогін Vitest; npm run test:watch — у режимі спостереження
npm run build
npm start         # dev-сервер на http://localhost:4200
npm run e2e       # Playwright (e2e/*.e2e.ts); Api підміняється в тестах, браузер — npx playwright install chromium
npm run e2e:live  # Playwright (e2e-live/*.live.ts) проти справжніх Api + Worker і моків 1С/Telegram:
                  # спершу `dotnet build` і Postgres (`scripts/dev.sh up`); БД — ConnectionStrings__Default
```

Маршрути (`docs/architecture.md` §7): `/workflows`, `/workflows/:id`, `/executions`, `/connectors`, `/credentials`, `/settings` — поки заглушки.
