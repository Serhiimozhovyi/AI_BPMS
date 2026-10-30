# Шаблон проєкту «Заявки до підтримки»

Найменший приклад шаблону проєкту (`docs/projects.md` §5): роль застосунку, таблиця `tickets` з двома рядками,
три сторінки застосунку (список із дією «Закрити», форма «Нова заявка», показники) і сценарій «Закрити заявку»,
який дія сторінки запускає з `{ row, rowKey, user }`.

```bash
# Новий проєкт у запущеному AI_BPMS
openbp import-project examples/projects/helpdesk/bundle.json --url http://localhost:8088 --api-key $OPENBP_API_KEY

# Своє — навпаки: зберегти проєкт у такий файл (рядки лише названих таблиць, без чутливих колонок)
openbp export-project <id проєкту> --rows tickets --out my-project.json
```

У UI: «Проєкти» → «Імпортувати проєкт» і «Огляд» проєкту → «Експортувати проєкт». Сценарії імпортуються
неактивними; змінних інстансу, конекторів і облікових даних цьому шаблону не потрібно.

Кожен `examples/projects/*/bundle.json` імпортується в порожній інстанс і проганяється з pinData у CI
(`ProjectTemplateCatalogTests`, `ProjectTemplatePinTests`).
