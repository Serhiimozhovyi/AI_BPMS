namespace OpenBP.Cli.Tests;

/// <summary>F8-02: <c>openbp run</c> з операціями <c>insertIf</c>, <c>increment</c> і <c>count</c> над таблицею в пам'яті.</summary>
public sealed class CliDataTableTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openbp-cli-tables-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Run_infers_columns_from_where_and_increment()
    {
        var path = Path.Combine(_dir, "tables.json");
        await File.WriteAllTextAsync(path, """
            {
              "formatVersion": 1, "name": "Реєстрація",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_register", "name": "Записати", "type": "core.dataTable", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "table": "Реєстрації", "operation": "insertIf", "maxCount": 8,
                    "where": [{ "column": "event_id", "operator": "eq", "value": "e1" }], "values": { "person": "Олена" } } },
                { "id": "n_visit", "name": "Візит", "type": "core.dataTable", "typeVersion": 1, "position": [400, 0],
                  "parameters": { "table": "Реєстрації", "operation": "increment", "key": "{{ $json._key }}", "column": "visits", "by": 2 } },
                { "id": "n_count", "name": "Скільки", "type": "core.dataTable", "typeVersion": 1, "position": [600, 0],
                  "parameters": { "table": "Реєстрації", "operation": "count", "where": [{ "column": "visits", "operator": "gte", "value": 2 }] } }
              ],
              "connections": [
                { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_register", "input": 0 } },
                { "from": { "node": "n_register", "output": 0 }, "to": { "node": "n_visit", "input": 0 } },
                { "from": { "node": "n_visit", "output": 0 }, "to": { "node": "n_count", "input": 0 } }
              ],
              "pinData": {}
            }
            """);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["run", path], stdout, stderr, CancellationToken.None);

        var output = stdout.ToString();
        Assert.True(code == CliApp.Ok, stderr + output);
        Assert.Contains("\"_inserted\": true", output, StringComparison.Ordinal);
        Assert.Contains("\"visits\": 2", output, StringComparison.Ordinal);
        Assert.Contains("\"count\": 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_skips_tables_it_cannot_infer_and_pinned_nodes_read_them()
    {
        // F8-08: get без умов, назва таблиці й колонки з виразом — таблиць у пам'яті немає, а закріплені вузли працюють.
        var path = Path.Combine(_dir, "pinned.json");
        await File.WriteAllTextAsync(path, """
            {
              "formatVersion": 1, "name": "Оголошення",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_get", "name": "Налаштування", "type": "core.dataTable", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "table": "settings", "operation": "get", "key": "HORIZON_DAYS" } },
                { "id": "n_people", "name": "Учасники", "type": "core.dataTable", "typeVersion": 1, "position": [400, 0],
                  "parameters": { "table": "{{ 'events' }}", "operation": "list",
                    "where": [{ "column": "{{ 'event_id' }}", "operator": "eq", "value": "e1" }] } }
              ],
              "connections": [
                { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_get", "input": 0 } },
                { "from": { "node": "n_get", "output": 0 }, "to": { "node": "n_people", "input": 0 } }
              ],
              "pinData": {
                "n_get": [{ "json": { "_key": "HORIZON_DAYS", "value": "21" } }],
                "n_people": [{ "json": { "_key": "r1", "telegram_id": "1001" } }]
              }
            }
            """);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["run", path, "--pin"], stdout, stderr, CancellationToken.None);

        Assert.True(code == CliApp.Ok, stderr + stdout.ToString());
        Assert.Contains("\"telegram_id\": \"1001\"", stdout.ToString(), StringComparison.Ordinal);
    }
}
