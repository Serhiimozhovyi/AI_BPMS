using System.Text.Json.Nodes;
using OpenBP.Application.Errors;
using OpenBP.Application.Forms;
using OpenBP.Engine.Data;

namespace OpenBP.Application.Projects;

/// <summary>Типи сторінок застосунку (docs/projects.md §3).</summary>
public static class ProjectPageKinds
{
    /// <summary>Список рядків таблиці.</summary>
    public const string List = "list";

    /// <summary>Картка рядка з пов'язаними списками.</summary>
    public const string Detail = "detail";

    /// <summary>Форма створення чи зміни рядка.</summary>
    public const string Form = "form";

    /// <summary>Плитки з агрегатами і прості діаграми (F8-05).</summary>
    public const string Metrics = "metrics";

    /// <summary>Календар (тиждень, день, місяць) над таблицею з початком і тривалістю (F8-14).</summary>
    public const string Calendar = "calendar";

    /// <summary>Сторінка з блоків: плитки, списки, календар, кнопки, форма, текст (F8-14).</summary>
    public const string Dashboard = "dashboard";

    /// <summary>Підтримувані типи.</summary>
    public static readonly IReadOnlyList<string> All = [List, Detail, Form, Metrics, Calendar, Dashboard];
}

/// <summary>Формати показу колонок і полів (<see cref="PageColumn.Format"/>, docs/projects.md §3.6).</summary>
public static class PageFormats
{
    /// <summary>Текст як є.</summary>
    public const string Text = "text";

    /// <summary>Число.</summary>
    public const string Number = "number";

    /// <summary>Дата у поясі застосунку.</summary>
    public const string Date = "date";

    /// <summary>Дата й час у поясі застосунку.</summary>
    public const string Datetime = "datetime";

    /// <summary>Лише час (<c>datetime</c> у поясі застосунку або текст <c>ГГ:ХХ</c>).</summary>
    public const string Time = "time";

    /// <summary>День тижня назвою: число 1–7 (1 — понеділок) або дата.</summary>
    public const string Weekday = "weekday";

    /// <summary>Значення з підписом із <see cref="PageColumn.Labels"/>.</summary>
    public const string Enum = "enum";

    /// <summary>Кольорова мітка: підпис з <see cref="PageColumn.Labels"/>, колір з <see cref="PageColumn.Colors"/>.</summary>
    public const string Badge = "badge";

    /// <summary>Телефон: показ з маскою, у формі — нормалізація до цифр.</summary>
    public const string Phone = "phone";

    /// <summary>Місткість: <c>0</c> — «без ліміту» (підпис можна змінити у <see cref="PageColumn.Labels"/> ключем <c>0</c>).</summary>
    public const string Capacity = "capacity";

    /// <summary>Так/ні підписами.</summary>
    public const string Boolean = "boolean";

    /// <summary>Усі формати.</summary>
    public static readonly IReadOnlyList<string> All = [Text, Number, Date, Datetime, Time, Weekday, Enum, Badge, Phone, Capacity, Boolean];

    /// <summary>Кольори міток: назви токенів теми, не hex.</summary>
    public static readonly IReadOnlyList<string> Colors = ["gray", "green", "yellow", "red", "blue", "purple"];
}

/// <summary>Колонка списку чи поле картки.</summary>
/// <param name="Column">Колонка таблиці або ключ обчислюваної колонки (<see cref="PageDefinition.Computed"/>).</param>
/// <param name="Label">Підпис; <c>null</c> — назва колонки.</param>
/// <param name="Format">Формат показу (<see cref="PageFormats"/>); <c>null</c> — за типом колонки.</param>
/// <param name="Display">Для колонки <c>ref</c>: <c>&lt;таблиця&gt;.&lt;колонка&gt;</c>, значення якої показати замість ключа.</param>
public sealed record PageColumn(string Column, string? Label = null, string? Format = null, string? Display = null)
{
    /// <summary>Підписи значень (<c>enum</c>, <c>badge</c>, <c>boolean</c>, <c>capacity</c>): значення → текст.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Кольори значень для <c>badge</c>: значення → колір з <see cref="PageFormats.Colors"/>.</summary>
    public IReadOnlyDictionary<string, string>? Colors { get; init; }
}

/// <summary>
/// Фільтр, яким керує користувач (панель над списком чи календарем): вибір значення, чипи станів, період чи пошук.
/// Діє поверх <see cref="PageDefinition.Filter"/> і прав; передається у запит рядків як <c>filters[колонка]</c>.
/// </summary>
/// <param name="Column">Колонка таблиці.</param>
/// <param name="Kind"><see cref="PageUserFilterKinds"/>.</param>
public sealed record PageUserFilter(string Column, string Kind)
{
    /// <summary>Підпис; <c>null</c> — назва колонки.</summary>
    public string? Label { get; init; }

    /// <summary>Для колонки <c>ref</c>: <c>&lt;таблиця&gt;.&lt;колонка&gt;</c> — що показати у виборі.</summary>
    public string? Display { get; init; }

    /// <summary>Підписи значень (<c>enum</c>, <c>boolean</c>).</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// Значення за замовчуванням: рядок чи число для <c>select</c>, список для <c>chips</c>, назва періоду
    /// (<c>today</c>, <c>week</c>, <c>month</c>) для <c>period</c>; вираз з <c>$user</c> дозволений.
    /// </summary>
    public JsonNode? Default { get; init; }
}

/// <summary>Види фільтрів користувача.</summary>
public static class PageUserFilterKinds
{
    /// <summary>Вибір одного значення: <c>enum</c>, <c>boolean</c>, <c>ref</c> (рядки пов'язаної таблиці), <c>user</c>.</summary>
    public const string Select = "select";

    /// <summary>Чипи з кількома станами (<c>enum</c>, <c>boolean</c>).</summary>
    public const string Chips = "chips";

    /// <summary>Період для <c>date</c>/<c>datetime</c>: сьогодні, тиждень, місяць або від–до.</summary>
    public const string Period = "period";

    /// <summary>Пошук підрядком по колонці (текст).</summary>
    public const string Search = "search";

    /// <summary>Усі види.</summary>
    public static readonly IReadOnlyList<string> All = [Select, Chips, Period, Search];
}

/// <summary>
/// Обчислювана колонка списку: агрегат по рядках <see cref="Table"/>, де <see cref="By"/> дорівнює ключу рядка списку.
/// Показується як колонка з ключем <see cref="Key"/> (її можна згадати в <see cref="PageDefinition.Columns"/>).
/// </summary>
/// <param name="Key">Ключ: латиниця, цифри, _.</param>
/// <param name="Label">Підпис.</param>
/// <param name="Table">Таблиця пов'язаних рядків.</param>
/// <param name="By">Колонка <see cref="Table"/>, що посилається на рядок списку.</param>
public sealed record PageComputed(string Key, string Label, string Table, string By)
{
    /// <summary><c>count</c> (типово) або <c>sum</c>.</summary>
    public string? Aggregate { get; init; }

    /// <summary>Числова колонка для <c>sum</c>.</summary>
    public string? Column { get; init; }

    /// <summary>Умови на пов'язані рядки; значення можуть бути виразами з <c>$user</c>.</summary>
    public IReadOnlyList<PageFilter>? Where { get; init; }

    /// <summary>Колонка рядка списку з загальною кількістю: показ «3 / 8»; <c>0</c> у ній — «без ліміту».</summary>
    public string? Of { get; init; }
}

/// <summary>Показ списку картками: які колонки — заголовок і підзаголовок картки.</summary>
/// <param name="Title">Колонка заголовка.</param>
public sealed record PageCard(string Title)
{
    /// <summary>Колонка підзаголовка.</summary>
    public string? Subtitle { get; init; }
}

/// <summary>Режими відкриття сторінки з дії чи кнопки «Створити».</summary>
public static class PageOpenModes
{
    /// <summary>Перехід на сторінку (типово).</summary>
    public const string Navigate = "navigate";

    /// <summary>Бокова панель поверх поточної сторінки; після збереження список оновлюється.</summary>
    public const string Drawer = "drawer";

    /// <summary>Усі режими.</summary>
    public static readonly IReadOnlyList<string> All = [Navigate, Drawer];
}

/// <summary>Умова сторінки: як <see cref="DataFilter"/>, значення може бути виразом з <c>$user</c>.</summary>
/// <param name="Column">Колонка.</param>
/// <param name="Operator">Оператор (<see cref="DataFilterOperators"/>).</param>
/// <param name="Value">Значення або вираз <c>{{ $user.id }}</c>.</param>
public sealed record PageFilter(string Column, string Operator, JsonNode? Value = null);

/// <summary>Сортування.</summary>
/// <param name="Column">Колонка.</param>
/// <param name="Direction"><c>asc</c> або <c>desc</c>.</param>
public sealed record PageSort(string Column, string? Direction = "asc");

/// <summary>
/// Дія над рядком: рівно одне з <see cref="Workflow"/> (F8-05), <see cref="Page"/> (перехід) чи <see cref="Operation"/>
/// (<c>delete</c>, лише editor проєкту).
/// </summary>
/// <param name="Label">Підпис кнопки.</param>
public sealed record PageAction(string Label)
{
    /// <summary>
    /// Ключ (назва) або id сценарію проєкту: сервер запускає його з item <c>{ row, user }</c>, чекає результат до
    /// 30 с і віддає <c>message</c>/помилку (F8-05).
    /// </summary>
    public string? Workflow { get; init; }

    /// <summary>Ключ сторінки, яку відкрити з ключем рядка.</summary>
    public string? Page { get; init; }

    /// <summary>Як відкрити <see cref="Page"/>: <see cref="PageOpenModes"/>; <c>null</c> — перехід.</summary>
    public string? Mode { get; init; }

    /// <summary><c>delete</c> — видалити рядок.</summary>
    public string? Operation { get; init; }

    /// <summary>Питання підтвердження перед дією.</summary>
    public string? Confirm { get; init; }

    /// <summary>Вираз з <c>$row</c> і <c>$user</c>: дію показано, лише якщо він істинний.</summary>
    public string? When { get; init; }

    /// <summary>
    /// Ролі застосунку, яким дію показано; порожньо — усім, хто бачить сторінку. Editor проєкту й admin бачать усі дії.
    /// </summary>
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>Посилання на іншу сторінку застосунку.</summary>
/// <param name="Page">Ключ сторінки.</param>
/// <param name="Mode">Як відкрити: <see cref="PageOpenModes"/>; <c>null</c> — перехід.</param>
public sealed record PageLink(string Page, string? Mode = null);

/// <summary>Пов'язаний список на картці: рядки <see cref="Table"/>, де колонка <see cref="By"/> дорівнює ключу рядка картки.</summary>
/// <param name="Table">Таблиця.</param>
/// <param name="By">Колонка, що посилається на рядок картки.</param>
public sealed record PageRelated(string Table, string By)
{
    /// <summary>Підпис секції.</summary>
    public string? Label { get; init; }

    /// <summary>Колонки.</summary>
    public IReadOnlyList<PageColumn>? Columns { get; init; }

    /// <summary>Дії над рядками; у картці (<c>related</c>) — лише <c>page</c>, у підлеглих списках (<c>expand</c>) — будь-які.</summary>
    public IReadOnlyList<PageAction>? Actions { get; init; }

    /// <summary>Кнопка «Додати» підлеглого списку — форма з підставленим ключем батьківського рядка в <see cref="By"/>.</summary>
    public PageLink? Create { get; init; }

    /// <summary>Текст порожнього списку.</summary>
    public string? Empty { get; init; }

    /// <summary>Сортування.</summary>
    public PageSort? Sort { get; init; }
}

/// <summary>Поле картки (<c>detail</c>) чи форми (<c>form</c>).</summary>
/// <param name="Column">Колонка.</param>
public sealed record PageField(string Column)
{
    /// <summary>Підпис.</summary>
    public string? Label { get; init; }

    /// <summary>Підказка показу (картка).</summary>
    public string? Format { get; init; }

    /// <summary>Обов'язкове на цій формі (крім обов'язкових колонок таблиці).</summary>
    public bool Required { get; init; }

    /// <summary>Підказка під полем.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Фіксоване значення або вираз з <c>$user</c> (<c>{{ $user.id }}</c>): поле не показується, значення ставить сервер.
    /// </summary>
    public JsonNode? Value { get; init; }

    /// <summary>Для колонки <c>ref</c>: <c>&lt;таблиця&gt;.&lt;колонка&gt;</c> — що показати замість ключа (і підписи варіантів у формі).</summary>
    public string? Display { get; init; }

    /// <summary>Підписи значень (<c>enum</c>, <c>boolean</c>) — у картці й у варіантах вибору форми.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>
/// Форма «ключ — значення» (<c>form</c>): усі рядки таблиці налаштувань однією формою, поле на кожен рядок; збереження —
/// <c>upsert</c> змінених рядків за ключовою колонкою.
/// </summary>
/// <param name="Key">Ключова колонка таблиці (назва ключа).</param>
/// <param name="Value">Колонка значення.</param>
public sealed record PageKeyValue(string Key, string Value)
{
    /// <summary>Які ключі показати і в якому порядку; порожньо — усі рядки за ключем.</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>Підписи полів: ключ → текст.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Ключі, значення яких редагується багаторядковим полем.</summary>
    public IReadOnlyList<string>? Multiline { get; init; }
}

/// <summary>Календар (<c>calendar</c>): подія — рядок таблиці з початком і тривалістю чи кінцем.</summary>
/// <param name="Start">Колонка <c>datetime</c> початку.</param>
public sealed record PageCalendar(string Start)
{
    /// <summary>Числова колонка тривалості у хвилинах; або <see cref="End"/>.</summary>
    public string? Duration { get; init; }

    /// <summary>Колонка <c>datetime</c> кінця; або <see cref="Duration"/>. Без обох — подія на <see cref="DefaultMinutes"/> хвилин.</summary>
    public string? End { get; init; }

    /// <summary>Тривалість, коли немає ні <see cref="Duration"/>, ні <see cref="End"/> (типово 60).</summary>
    public int? DefaultMinutes { get; init; }

    /// <summary>Заголовок події: колонка (з <c>display</c> для <c>ref</c>).</summary>
    public PageColumn? Title { get; init; }

    /// <summary>Підзаголовок події.</summary>
    public PageColumn? Subtitle { get; init; }

    /// <summary>Колір події: колонка з форматом <c>badge</c> і <c>colors</c>.</summary>
    public PageColumn? Color { get; init; }

    /// <summary>Доступні вигляди з <see cref="PageCalendarViews"/>; порожньо — усі.</summary>
    public IReadOnlyList<string>? Views { get; init; }

    /// <summary>Вигляд за замовчуванням; <c>null</c> — перший з <see cref="Views"/>.</summary>
    public string? Default { get; init; }

    /// <summary>Клік по події: сторінка (картка чи форма) з ключем рядка і режимом відкриття.</summary>
    public PageLink? Open { get; init; }

    /// <summary>Перша година дня у вигляді тижня/дня (0–23, типово 7).</summary>
    public int? FromHour { get; init; }

    /// <summary>Остання година (1–24, типово 21).</summary>
    public int? ToHour { get; init; }
}

/// <summary>Вигляди календаря.</summary>
public static class PageCalendarViews
{
    /// <summary>Тиждень.</summary>
    public const string Week = "week";

    /// <summary>День.</summary>
    public const string Day = "day";

    /// <summary>Місяць.</summary>
    public const string Month = "month";

    /// <summary>Усі вигляди.</summary>
    public static readonly IReadOnlyList<string> All = [Week, Day, Month];
}

/// <summary>Блок сторінки <c>dashboard</c>: вбудована сторінка проєкту (<see cref="Page"/>) або власне визначення.</summary>
/// <param name="Kind"><see cref="PageBlockKinds"/>.</param>
public sealed record PageBlock(string Kind)
{
    /// <summary>Заголовок блоку.</summary>
    public string? Label { get; init; }

    /// <summary>Ключ сторінки проєкту того самого виду, яку показати блоком (<c>tiles</c> — сторінка <c>metrics</c>).</summary>
    public string? Page { get; init; }

    /// <summary>Власне визначення блоку замість <see cref="Page"/>: як у сторінки відповідного виду.</summary>
    public PageDefinition? Definition { get; init; }

    /// <summary>Скільки рядків показати у списку (1–50, типово 5); «Усі» веде на <see cref="Page"/>.</summary>
    public int? Limit { get; init; }

    /// <summary>Для <c>calendar</c>: <c>today</c> (типово) чи <c>week</c>.</summary>
    public string? Range { get; init; }

    /// <summary>Кнопки блоку <c>actions</c>: сценарій проєкту з item <c>{ user }</c> або перехід; без рядка.</summary>
    public IReadOnlyList<PageAction>? Actions { get; init; }

    /// <summary>Текст блоку <c>text</c>; вирази з <c>$user</c> дозволені.</summary>
    public string? Text { get; init; }

    /// <summary>Вираз з <c>$user</c>: блок показано, лише якщо він істинний.</summary>
    public string? When { get; init; }

    /// <summary>Ширина у колонках сітки з 12 (типово 12 — на всю ширину; 6 — половина).</summary>
    public int? Width { get; init; }
}

/// <summary>Види блоків <c>dashboard</c>.</summary>
public static class PageBlockKinds
{
    /// <summary>Плитки й діаграми (<c>metrics</c>).</summary>
    public const string Tiles = "tiles";

    /// <summary>Короткий список (<c>list</c>) з лімітом.</summary>
    public const string List = "list";

    /// <summary>Події календаря за сьогодні чи тиждень.</summary>
    public const string Calendar = "calendar";

    /// <summary>Кнопки дій без рядка.</summary>
    public const string Actions = "actions";

    /// <summary>Форма (<c>form</c>), зокрема «ключ — значення».</summary>
    public const string Form = "form";

    /// <summary>Текст (привітання, підказка).</summary>
    public const string Text = "text";

    /// <summary>Усі види.</summary>
    public static readonly IReadOnlyList<string> All = [Tiles, List, Calendar, Actions, Form, Text];

    /// <summary>Вид сторінки, визначенням якої описано блок; <c>null</c> — блок без визначення.</summary>
    public static string? PageKind(string kind) => kind switch
    {
        Tiles => ProjectPageKinds.Metrics,
        List => ProjectPageKinds.List,
        Calendar => ProjectPageKinds.Calendar,
        Form => ProjectPageKinds.Form,
        _ => null,
    };
}

/// <summary>Що робить форма: рівно одне з <see cref="Operation"/> чи <see cref="Workflow"/>.</summary>
public sealed record PageSubmit
{
    /// <summary><c>insert</c>, <c>upsert</c> або <c>update</c> (рядок з адреси форми).</summary>
    public string? Operation { get; init; }

    /// <summary>
    /// Сценарій, якому передати значення: item <c>{ values, user }</c> (F8-05). Відмова сценарію (<c>ok: false</c> з
    /// <c>message</c> і, за потреби, <c>field</c>) повертається як помилка форми.
    /// </summary>
    public string? Workflow { get; init; }
}

/// <summary>
/// Плитка сторінки <c>metrics</c>: агрегат <see cref="Aggregate"/> по таблиці або вираз над іншими плитками
/// (<c>{{ $tiles.booked / $tiles.slots }}</c>, де <c>booked</c>, <c>slots</c> — <see cref="Key"/> плиток).
/// </summary>
/// <param name="Label">Підпис.</param>
public sealed record PageTile(string Label)
{
    /// <summary>Ключ для виразів інших плиток: латиниця, цифри, _.</summary>
    public string? Key { get; init; }

    /// <summary>Таблиця; без неї береться <see cref="PageDefinition.Table"/> сторінки.</summary>
    public string? Table { get; init; }

    /// <summary><c>count</c>, <c>sum</c>, <c>avg</c>, <c>min</c> або <c>max</c>.</summary>
    public string? Aggregate { get; init; }

    /// <summary>Числова колонка для <c>sum</c>/<c>avg</c>/<c>min</c>/<c>max</c>.</summary>
    public string? Column { get; init; }

    /// <summary>Умови; значення можуть бути виразами з <c>$user</c>.</summary>
    public IReadOnlyList<PageFilter>? Where { get; init; }

    /// <summary>Вираз над <c>$tiles</c> замість агрегату; плитки без ключів у ньому недоступні.</summary>
    public string? Expression { get; init; }
}

/// <summary>Стовпчаста діаграма сторінки <c>metrics</c>: агрегат по групах.</summary>
public sealed record PageChart
{
    /// <summary>Підпис.</summary>
    public string? Label { get; init; }

    /// <summary>Таблиця; без неї — таблиця сторінки.</summary>
    public string? Table { get; init; }

    /// <summary>
    /// Групування: колонка (<c>enum</c>, текст, логічна, <c>ref</c>) або <c>day(колонка)</c>, <c>week(колонка)</c>,
    /// <c>month(колонка)</c> для <c>date</c>/<c>datetime</c>.
    /// </summary>
    public string GroupBy { get; init; } = "";

    /// <summary>Агрегат, типово <c>count</c>.</summary>
    public string? Aggregate { get; init; }

    /// <summary>Числова колонка для агрегатів, крім <c>count</c>.</summary>
    public string? Column { get; init; }

    /// <summary>Умови.</summary>
    public IReadOnlyList<PageFilter>? Where { get; init; }
}

/// <summary>
/// Визначення сторінки (<c>project_pages.definition</c>); які поля потрібні — залежить від типу (docs/projects.md §3).
/// <see cref="Filter"/> діє на сервері в усіх типах: рядок поза фільтром для сторінки не існує.
/// </summary>
public sealed record PageDefinition
{
    /// <summary>Таблиця проєкту або спільна (за назвою).</summary>
    public string Table { get; init; } = "";

    /// <summary>Колонки списку (<c>list</c>).</summary>
    public IReadOnlyList<PageColumn>? Columns { get; init; }

    /// <summary>Умови, які не можна зняти з клієнта.</summary>
    public IReadOnlyList<PageFilter>? Filter { get; init; }

    /// <summary>Колонки пошуку (<c>list</c>).</summary>
    public IReadOnlyList<string>? Search { get; init; }

    /// <summary>Сортування за замовчуванням (<c>list</c>).</summary>
    public PageSort? Sort { get; init; }

    /// <summary>Рядків на сторінці списку, 1–200 (типово 50).</summary>
    public int? PageSize { get; init; }

    /// <summary>Дії над рядком (<c>list</c>, <c>detail</c>).</summary>
    public IReadOnlyList<PageAction>? Actions { get; init; }

    /// <summary>Кнопка «Створити» у списку — сторінка-форма.</summary>
    public PageLink? Create { get; init; }

    /// <summary>Поля картки (<c>detail</c>) чи форми (<c>form</c>).</summary>
    public IReadOnlyList<PageField>? Fields { get; init; }

    /// <summary>Пов'язані списки картки.</summary>
    public IReadOnlyList<PageRelated>? Related { get; init; }

    /// <summary>Що робить форма.</summary>
    public PageSubmit? OnSubmit { get; init; }

    /// <summary>Куди перейти після форми.</summary>
    public PageLink? Then { get; init; }

    /// <summary>Плитки (<c>metrics</c>).</summary>
    public IReadOnlyList<PageTile>? Tiles { get; init; }

    /// <summary>Діаграми (<c>metrics</c>).</summary>
    public IReadOnlyList<PageChart>? Charts { get; init; }

    /// <summary>Фільтри користувача над списком чи календарем (F8-14).</summary>
    public IReadOnlyList<PageUserFilter>? Filters { get; init; }

    /// <summary>Обчислювані колонки списку (F8-14).</summary>
    public IReadOnlyList<PageComputed>? Computed { get; init; }

    /// <summary>Показ списку: <c>table</c> (типово) чи <c>cards</c>.</summary>
    public string? Layout { get; init; }

    /// <summary>Заголовок і підзаголовок картки для <c>layout: cards</c>.</summary>
    public PageCard? Card { get; init; }

    /// <summary>Підлеглі списки під розгорнутим рядком списку (F8-14).</summary>
    public IReadOnlyList<PageRelated>? Expand { get; init; }

    /// <summary>Текст порожнього списку чи календаря.</summary>
    public string? Empty { get; init; }

    /// <summary>Форма «ключ — значення» (<c>form</c>) замість <see cref="Fields"/>.</summary>
    public PageKeyValue? KeyValue { get; init; }

    /// <summary>Налаштування календаря (<c>calendar</c>).</summary>
    public PageCalendar? Calendar { get; init; }

    /// <summary>Блоки (<c>dashboard</c>).</summary>
    public IReadOnlyList<PageBlock>? Blocks { get; init; }
}

/// <summary>Сторінка застосунку.</summary>
/// <param name="Id"><c>project_pages.id</c>.</param>
/// <param name="Key">Ключ — адреса <c>/projects/:id/app/&lt;key&gt;</c>.</param>
/// <param name="Name">Назва в меню застосунку.</param>
/// <param name="Kind"><see cref="ProjectPageKinds"/>.</param>
/// <param name="Definition">Визначення.</param>
/// <param name="Roles">Ролі застосунку, яким видно сторінку; порожньо — усім учасникам.</param>
/// <param name="SortOrder">Порядок у меню.</param>
/// <param name="UpdatedAt">Остання зміна.</param>
public sealed record ProjectPageDto(
    Guid Id, string Key, string Name, string Kind, PageDefinition Definition, IReadOnlyList<string> Roles, int SortOrder, DateTimeOffset UpdatedAt);

/// <summary>Сторінка для збереження (<c>PUT /projects/{id}/pages/{key}</c>, MCP <c>upsert_project_page</c>).</summary>
/// <param name="Name">Назва.</param>
/// <param name="Kind">Тип.</param>
/// <param name="Definition">Визначення.</param>
/// <param name="Roles">Ролі застосунку.</param>
/// <param name="SortOrder">Порядок; <c>null</c> — у кінці (нова) чи без змін.</param>
public sealed record ProjectPageInput(string Name, string Kind, PageDefinition Definition, IReadOnlyList<string>? Roles = null, int? SortOrder = null);

/// <summary>Колонка таблиці сторінки для показу.</summary>
/// <param name="Column">Назва колонки.</param>
/// <param name="Label">Підпис.</param>
/// <param name="Type">Тип колонки (<c>string</c>, <c>datetime</c>…).</param>
/// <param name="Format">Підказка показу.</param>
public sealed record PageViewColumn(string Column, string Label, string Type, string? Format);

/// <summary>Сторінка для показу (<c>GET /projects/{id}/pages/{key}</c>).</summary>
/// <param name="Page">Сторінка.</param>
/// <param name="TableId">Таблиця сторінки; для <c>metrics</c> без таблиці за замовчуванням — порожній <see cref="Guid"/>.</param>
/// <param name="Columns">Колонки списку чи поля картки з типами.</param>
/// <param name="Schema">JSON Schema форми (<c>form</c>) для рушія форм F6-03; для інших типів <c>null</c>.</param>
/// <param name="CanEdit">Чи може користувач змінювати сторінки проєкту (editor).</param>
public sealed record ProjectPageView(ProjectPageDto Page, Guid TableId, IReadOnlyList<PageViewColumn> Columns, JsonObject? Schema, bool CanEdit)
{
    /// <summary>
    /// Варіанти фільтрів користувача <c>select</c> і <c>chips</c> за колонкою (F8-16): значення <c>enum</c> і <c>boolean</c> з підписами,
    /// рядки пов'язаної таблиці для <c>ref</c>, учасники проєкту для <c>user</c>. Фільтр за чутливою колонкою без права читати її не показується.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PageOption>>? FilterOptions { get; init; }

    /// <summary>Часовий пояс застосунку (IANA) для показу дат; <c>UTC</c>, якщо оболонки немає (F8-16).</summary>
    public string Timezone { get; init; } = "UTC";
}

/// <summary>Варіант вибору у фільтрі.</summary>
/// <param name="Value">Значення, що йде в <c>filters[колонка]</c>.</param>
/// <param name="Label">Підпис.</param>
public sealed record PageOption(string Value, string Label);

/// <summary>Рядок на сторінці.</summary>
/// <param name="Key">Ключ рядка.</param>
/// <param name="Data">Значення лише тих колонок, що є на сторінці (чутливі — <c>null</c> без права).</param>
/// <param name="Display">Показані значення колонок <c>ref</c> з <c>display</c>.</param>
/// <param name="Actions">Індекси доступних дій з <see cref="PageDefinition.Actions"/>.</param>
public sealed record PageRow(string Key, JsonObject Data, IReadOnlyDictionary<string, string?> Display, IReadOnlyList<int> Actions);

/// <summary>Рядки списку.</summary>
/// <param name="Items">Рядки.</param>
/// <param name="Page">Номер сторінки.</param>
/// <param name="PageSize">Розмір.</param>
/// <param name="Total">Усього за фільтром і пошуком.</param>
public sealed record PageRows(IReadOnlyList<PageRow> Items, int Page, int PageSize, int Total)
{
    /// <summary>Фільтри користувача, що діяли на цей запит, разом зі значеннями за замовчуванням (F8-16); клієнт показує їх у панелі.</summary>
    public IReadOnlyDictionary<string, PageFilterInput>? Filters { get; init; }
}

/// <summary>Пов'язаний список картки.</summary>
/// <param name="Label">Підпис.</param>
/// <param name="Columns">Колонки.</param>
/// <param name="Rows">Рядки (до 200).</param>
public sealed record PageRelatedRows(string Label, IReadOnlyList<PageViewColumn> Columns, IReadOnlyList<PageRow> Rows);

/// <summary>Картка рядка або значення для форми зміни.</summary>
/// <param name="Row">Рядок.</param>
/// <param name="Related">Пов'язані списки (<c>detail</c>).</param>
public sealed record PageRecord(PageRow Row, IReadOnlyList<PageRelatedRows> Related);

/// <summary>Запит параметрів списку.</summary>
/// <param name="Search">Підрядок пошуку.</param>
/// <param name="Sort">Колонка сортування (з колонок сторінки).</param>
/// <param name="Descending">Спадання.</param>
/// <param name="Page">Номер сторінки від 1.</param>
/// <param name="PageSize">Розмір; <c>null</c> — з визначення.</param>
public sealed record PageRowsQuery(string? Search = null, string? Sort = null, bool? Descending = null, int Page = 1, int? PageSize = null)
{
    /// <summary>
    /// Значення фільтрів користувача (<see cref="PageDefinition.Filters"/>) за колонкою (F8-15). Колонки немає — діє
    /// <see cref="PageUserFilter.Default"/>; колонка з порожнім значенням — фільтр знято.
    /// </summary>
    public IReadOnlyDictionary<string, PageFilterInput>? Filters { get; init; }
}

/// <summary>Значення фільтра користувача з запиту: <c>filters[колонка]=…</c> і <c>filters[колонка][from|to]</c>.</summary>
/// <param name="Values">
/// Значення: одне для <c>select</c> і <c>search</c>, кілька для <c>chips</c>, назва періоду (<c>today</c>, <c>week</c>,
/// <c>month</c>) для <c>period</c>.
/// </param>
/// <param name="From">Початок періоду (дата чи дата й час), включно.</param>
/// <param name="To">Кінець періоду: дата — включно, дата й час — не включно.</param>
public sealed record PageFilterInput(IReadOnlyList<string> Values, string? From = null, string? To = null)
{
    /// <summary>Порожнє значення — фільтр знято.</summary>
    public bool IsEmpty => Values.All(string.IsNullOrEmpty) && string.IsNullOrEmpty(From) && string.IsNullOrEmpty(To);
}

/// <summary>Подія календаря (<c>GET …/calendar</c>, F8-15).</summary>
/// <param name="Start">Початок.</param>
/// <param name="End">Кінець (з тривалості, колонки кінця чи <see cref="PageCalendar.DefaultMinutes"/>).</param>
/// <param name="Row">Рядок: колонки заголовка, підзаголовка, кольору й часу, показ <c>ref</c>, доступні дії.</param>
public sealed record PageCalendarEvent(DateTimeOffset Start, DateTimeOffset End, PageRow Row);

/// <summary>Події календаря за діапазоном.</summary>
/// <param name="From">Початок діапазону.</param>
/// <param name="To">Кінець діапазону (не включно).</param>
/// <param name="Events">Події за часом початку.</param>
/// <param name="Truncated">Подій більше за ліміт — показано перші.</param>
public sealed record PageCalendarEvents(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<PageCalendarEvent> Events, bool Truncated);

/// <summary>Блок сторінки <c>dashboard</c> з даними (<c>GET …/dashboard</c>, F8-15).</summary>
/// <param name="Index">Індекс блоку у визначенні.</param>
/// <param name="Kind"><see cref="PageBlockKinds"/>.</param>
/// <param name="Label">Заголовок.</param>
/// <param name="Width">Ширина з 12.</param>
/// <param name="Source">
/// Ключ сторінки, до якої звертатися за рядками, формою й діями блоку: <see cref="PageBlock.Page"/> або
/// <c>&lt;dashboard&gt;.&lt;індекс&gt;</c> для власного визначення; <c>null</c> — для <c>text</c> і <c>actions</c>.
/// </param>
public sealed record PageDashboardBlock(int Index, string Kind, string? Label, int Width, string? Source)
{
    /// <summary>Сторінка блоку для показу (колонки, схема форми) — для <c>list</c>, <c>calendar</c>, <c>form</c>, <c>tiles</c>.</summary>
    public ProjectPageView? View { get; init; }

    /// <summary>Плитки й діаграми блоку <c>tiles</c>.</summary>
    public PageMetrics? Metrics { get; init; }

    /// <summary>Перші рядки блоку <c>list</c> (до <see cref="PageBlock.Limit"/>).</summary>
    public PageRows? Rows { get; init; }

    /// <summary>Події блоку <c>calendar</c> за сьогодні чи тиждень.</summary>
    public PageCalendarEvents? Events { get; init; }

    /// <summary>Текст блоку <c>text</c> з обчисленими виразами.</summary>
    public string? Text { get; init; }

    /// <summary>Індекси доступних кнопок блоку <c>actions</c>.</summary>
    public IReadOnlyList<int>? Actions { get; init; }
}

/// <summary>Сторінка <c>dashboard</c> з даними блоків.</summary>
/// <param name="Blocks">Видимі блоки в порядку визначення (блоки з хибним <c>when</c> і недоступними сторінками пропущено).</param>
public sealed record PageDashboard(IReadOnlyList<PageDashboardBlock> Blocks);

/// <summary>Результат форми.</summary>
/// <param name="Row">Збережений рядок; для форми зі сценарієм (<c>onSubmit.workflow</c>) — <c>null</c>.</param>
/// <param name="Then">Сторінка, куди перейти; <c>null</c> — лишитися.</param>
/// <param name="Result">Результат сценарію форми (F8-05).</param>
public sealed record PageSubmitResult(PageRow? Row, string? Then, PageActionResult? Result = null);

/// <summary>Значення плитки сторінки <c>metrics</c>.</summary>
/// <param name="Key">Ключ плитки.</param>
/// <param name="Label">Підпис.</param>
/// <param name="Value">Число; <c>null</c> — немає значення (порожня вибірка, ділення на нуль).</param>
public sealed record PageMetricTile(string? Key, string Label, decimal? Value);

/// <summary>Точка діаграми.</summary>
/// <param name="Label">Значення групи (дата початку періоду, значення <c>enum</c>…); порожня група — «—».</param>
/// <param name="Value">Агрегат групи.</param>
public sealed record PageMetricPoint(string Label, decimal? Value);

/// <summary>Діаграма сторінки <c>metrics</c>.</summary>
/// <param name="Label">Підпис.</param>
/// <param name="GroupBy">Групування з визначення.</param>
/// <param name="Points">Групи за зростанням.</param>
public sealed record PageMetricChart(string? Label, string GroupBy, IReadOnlyList<PageMetricPoint> Points);

/// <summary>Показники сторінки <c>metrics</c> (<c>GET /projects/{id}/pages/{key}/metrics</c>).</summary>
/// <param name="Tiles">Плитки в порядку визначення.</param>
/// <param name="Charts">Діаграми в порядку визначення.</param>
public sealed record PageMetrics(IReadOnlyList<PageMetricTile> Tiles, IReadOnlyList<PageMetricChart> Charts);

/// <summary>422 <c>PAGE_FORM_INVALID</c>: значення форми не пройшли перевірку; <see cref="Errors"/> — за полями.</summary>
public sealed class PageFormInvalidException : OpenBpException
{
    /// <summary>Код помилки.</summary>
    public const string ErrorCode = "PAGE_FORM_INVALID";

    /// <param name="errors">Помилки за полями (<c>field</c> — колонка; порожній — уся форма).</param>
    public PageFormInvalidException(IReadOnlyList<FormFieldError> errors)
        : base(ErrorCode, 422, errors is { Count: 1 } ? errors[0].Message : $"Перевірте поля форми: {errors?.Count ?? 0} помилок.")
    {
        Errors = errors ?? [];
    }

    /// <summary>Помилки; у REST — поле <c>errors</c> problem-details.</summary>
    public IReadOnlyList<FormFieldError> Errors { get; }
}
