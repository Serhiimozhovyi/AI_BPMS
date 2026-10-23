# Конектор 1С (OData) для OpenBP

MCP-сервер над стандартним OData-інтерфейсом 1С:Підприємство 8.3 (`standard.odata`, OData v3), зроблений на шаблоні
Connector Kit (`tools/connector-template`). Два tools описано вручну для сценаріїв PRD №1–2, решту можна згенерувати з
`$metadata`:

| Tool | Що робить | Аргументи | Результат |
|---|---|---|---|
| `create_order` | шукає контрагента за назвою (`Description`), номенклатуру за артикулом і **записує** (не проводить) документ замовлення | `customer`, `lines: [{ sku, qty }]` | `{ number, ref_key, date, customer, line_count }` |
| `sales_report` | проведені документи реалізації за період (обидва дні включно) з назвами контрагентів | `from`, `to` (`YYYY-MM-DD`) | `{ "result": [{ number, date, customer, customer_key, amount }] }` |
| згенеровані | `list_`/`get_`/`create_`/`update_` для кожного відкритого entity set (`Connector:Source`) | `$filter`, `$select`, `$top`… | див. `docs/connectors.md` §5 |

`create_order` нічого не записує, якщо контрагента чи артикул не знайдено або знайдено кілька: тоді `isError` з
поясненням. Помилки 1С (невірний логін — 401, немає прав, об'єкт не опубліковано) теж повертаються як `isError` з
HTTP-статусом і текстом 1С. Пароля немає ні у відповідях, ні в текстах помилок.

## 1. Увімкнути OData в 1С

1. Опублікуйте базу на вебсервері (Apache або IIS): Конфігуратор → «Адміністрування» → «Публікація на веб-сервері»,
   галочка **«Публікувати стандартний інтерфейс OData»**. Адреса публікації буде виду
   `http://srv/trade/odata/standard.odata/`.
2. Відкрийте потрібні об'єкти для OData. У режимі «Підприємство» виконайте обробку з методом
   `УстановитьСоставСтандартногоИнтерфейсаOData` (або скористайтеся готовою обробкою «Налаштування стандартного
   інтерфейсу OData» з ІТС) і додайте:
   - довідник **Контрагенти** (`Catalog_Контрагенты`);
   - довідник **Номенклатура** (`Catalog_Номенклатура`) з реквізитом артикулу;
   - документ **Замовлення покупця** (`Document_ЗаказПокупателя`) з табличною частиною **Товари**;
   - документ **Реалізація товарів і послуг** (`Document_РеализацияТоваровУслуг`);
   - для генератора — інші об'єкти, які мають бачити агенти.
3. Створіть окремого користувача 1С для OpenBP з правами лише на ці об'єкти (читання, а для замовлення — додавання).
4. Перевірте в браузері: `http://srv/trade/odata/standard.odata/Catalog_Контрагенты?$format=json&$top=1` (із
   логіном і паролем цього користувача).

### Інша конфігурація (УТ 11, ERP, BAS)

Назви об'єктів і реквізитів у різних конфігураціях різні. Типові значення розраховані на «Замовлення покупця» і
«Реалізацію товарів і послуг» (УТ 10, Бухгалтерія). Інші задаються налаштуваннями `Connector:OneC:*` (змінні
середовища `Connector__OneC__…`):

| Налаштування | Типово | Приклад для УТ 11 |
|---|---|---|
| `CustomerCatalog` | `Catalog_Контрагенты` | `Catalog_Партнеры` |
| `ItemCatalog` | `Catalog_Номенклатура` | |
| `SkuField` | `Артикул` | |
| `OrderDocument` | `Document_ЗаказПокупателя` | `Document_ЗаказКлиента` |
| `OrderCustomerField` | `Контрагент_Key` | `Партнер_Key` |
| `OrderLinesField` | `Товары` | |
| `LineItemField` | `Номенклатура_Key` | |
| `LineQuantityField` | `Количество` | |
| `SalesDocument` | `Document_РеализацияТоваровУслуг` | |
| `SalesCustomerField` | `Контрагент_Key` | `Партнер_Key` |
| `SalesAmountField` | `СуммаДокумента` | |

Точні назви видно в `$metadata` публікації. Обов'язкові реквізити документа, яких конектор не заповнює (організація,
склад, договір), 1С бере зі значень за замовчуванням користувача; якщо їх немає, запис завершиться помилкою 1С з
назвою реквізиту.

## 2. Зібрати

Потрібен .NET SDK 10.

```bash
cd connectors/onec
dotnet test
dotnet publish src/OpenBP.Connectors.OneC -c Release -o out
```

## 3. Зареєструвати в OpenBP

«Конектори» → «Додати MCP-сервер» (роль admin):

- **stdio** (OpenBP запускає процес сам):
  - команда `dotnet`, аргументи — шлях до `out/OpenBP.Connectors.OneC.dll` і
    `--Connector:BaseUrl=http://srv/trade/odata/standard.odata/`;
  - credential конектора: `{ "env": { "ONEC_USERNAME": "openbp", "ONEC_PASSWORD": "…" } }`. OpenBP зберігає його
    зашифрованим і передає процесу як змінні середовища. Порожній пароль — просто не задавайте `ONEC_PASSWORD`.
- **http** (окремий сервіс, напр. контейнер поруч із вебсервером 1С):
  - `Connector__BaseUrl=http://srv/trade/odata/standard.odata/ ONEC_USERNAME=… ONEC_PASSWORD=… dotnet out/OpenBP.Connectors.OneC.dll --http http://0.0.0.0:8080`;
  - в OpenBP: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і tools `create_order`, `sales_report`.

### Згенеровані tools

Щоб агенти могли читати й інші об'єкти, задайте джерело й фільтр (без фільтра будуть tools на кожен опублікований
об'єкт — у великій базі їх сотні):

```bash
Connector__Source=odata:http://srv/trade/odata/standard.odata/$metadata
Connector__OData__Include__0=Catalog_*
Connector__OData__Include__1=Document_РеализацияТоваровУслуг
```

`$format=json` до запитів додається завжди (`Connector:OData:FormatParameter`), бо без нього 1С віддає Atom XML.

Для CLI (`openbp run`) запис є в `examples/servers.json` (`srv_onec_odata`): адреса й логін беруться зі змінних
середовища вашої оболонки. Мок `--mock 1c` (`srv_1c`) лишається для прикладів і CI.
