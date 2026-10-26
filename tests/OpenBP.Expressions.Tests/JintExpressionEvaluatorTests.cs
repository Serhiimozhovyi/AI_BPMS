using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Expressions.Tests;

public class JintExpressionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 22, 30, 15, 250, TimeSpan.Zero);
    private static readonly Guid ExecutionId = Guid.Parse("0a1b2c3d-0000-4000-8000-000000000042");

    private readonly JintExpressionEvaluator _evaluator = new();

    private static Item NewItem(string json) => new() { Json = JsonNode.Parse(json)!.AsObject() };

    private static ExpressionScope Scope(string json = "{}") => new()
    {
        Item = NewItem(json),
        Now = Now,
        ExecutionId = ExecutionId,
    };

    // ---- Шаблони і типи ----

    [Fact]
    public void StringWithoutExpression_ReturnedAsIs()
    {
        var result = _evaluator.Evaluate("просто текст {{ без закриття", Scope());

        Assert.Equal("просто текст {{ без закриття", result!.GetValue<string>());
    }

    [Theory]
    [InlineData("{{ $json.number }}", "42")]
    [InlineData("{{ $json.price }}", "12.5")]
    [InlineData("{{ $json.ok }}", "true")]
    [InlineData("{{ $json.lines }}", """[{"sku":"A","qty":2}]""")]
    [InlineData("{{ $json.customer }}", """{"name":"ТОВ Ромашка"}""")]
    [InlineData("  {{ $json.number + 1 }}  ", "43")]
    [InlineData("{{ { a: 1, b: [1, 2] } }}", """{"a":1,"b":[1,2]}""")]
    public void SingleExpression_PreservesType(string expression, string expectedJson)
    {
        var scope = Scope("""{"number":42,"price":12.5,"ok":true,"lines":[{"sku":"A","qty":2}],"customer":{"name":"ТОВ Ромашка"}}""");

        var result = _evaluator.Evaluate(expression, scope);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), result), result?.ToJsonString());
    }

    [Theory]
    [InlineData("{{ null }}")]
    [InlineData("{{ $json.missing }}")]
    [InlineData("{{ undefined }}")]
    public void SingleExpression_NullOrUndefined_ReturnsNull(string expression)
    {
        Assert.Null(_evaluator.Evaluate(expression, Scope()));
    }

    [Fact]
    public void Template_SubstitutesValuesIntoString()
    {
        var scope = Scope("""{"number":17,"tags":["a","b"],"none":null}""");

        var result = _evaluator.Evaluate("Замовлення №{{ $json.number }}: {{ $json.tags }} {{ $json.none }}|{{ 1 }} і {{ 2 }}", scope);

        Assert.Equal("""Замовлення №17: ["a","b"] |1 і 2""", result!.GetValue<string>());
    }

    [Fact]
    public void Template_TrailingLineComment_DoesNotBreakExpression()
    {
        var result = _evaluator.Evaluate("{{ $json.a // коментар\n }}", Scope("""{"a":5}"""));

        Assert.Equal(5, result!.GetValue<int>());
    }

    [Fact]
    public void EvaluationsDoNotShareState()
    {
        _evaluator.Evaluate("{{ globalThis.leak = 1 }}", Scope());

        var result = _evaluator.Evaluate("{{ typeof leak }}", Scope());

        Assert.Equal("undefined", result!.GetValue<string>());
    }

    // ---- Змінні ----

    [Fact]
    public void Json_ReadsNestedFields()
    {
        var result = _evaluator.Evaluate("{{ $json.body.message.text.toUpperCase() }}", Scope("""{"body":{"message":{"text":"привіт"}}}"""));

        Assert.Equal("ПРИВІТ", result!.GetValue<string>());
    }

    [Fact]
    public void Input_ExposesItemAllFirstLast()
    {
        var input = new[] { NewItem("""{"n":1}"""), NewItem("""{"n":2}"""), NewItem("""{"n":3}""") };
        var scope = Scope("""{"n":2}""") with { Input = input, ItemIndex = 1 };

        var result = _evaluator.Evaluate(
            "{{ [$input.all().length, $input.first().json.n, $input.last().json.n, $input.item.json.n] }}",
            scope);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[3,1,3,2]"), result), result?.ToJsonString());
    }

    [Fact]
    public void Input_ExposesBinaryRefs()
    {
        var item = NewItem("{}");
        item.Binary = new() { ["data"] = new BinaryRef { Id = "f-1", MimeType = "application/pdf", FileName = "рахунок.pdf" } };
        var scope = Scope() with { Input = [item] };

        var result = _evaluator.Evaluate("{{ $input.item.binary.data.fileName }}", scope);

        Assert.Equal("рахунок.pdf", result!.GetValue<string>());
    }

    [Fact]
    public void Binary_ExposesCurrentItemFiles()
    {
        var scope = Scope();
        scope.Item.Binary = new() { ["data"] = new BinaryRef { Id = "f-1", MimeType = "application/pdf", FileName = "рахунок.pdf", FileSize = 29 } };

        var result = _evaluator.Evaluate("{{ [$binary.data.fileName, $binary.data.mimeType, $binary.data.fileSize] }}", scope);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["рахунок.pdf","application/pdf",29]"""), result), result?.ToJsonString());
    }

    [Fact]
    public void Binary_OfItemWithoutFiles_IsEmptyObject()
    {
        var result = _evaluator.Evaluate("{{ [Object.keys($binary).length, $binary.data?.fileName ?? 'немає'] }}", Scope());

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[0,"немає"]"""), result), result?.ToJsonString());
    }

    [Fact]
    public void Node_ByName_ExposesBinaryRefs()
    {
        var invoice = NewItem("""{"name":"rakhunok-17.pdf"}""");
        invoice.Binary = new() { ["data"] = new BinaryRef { Id = "f-17", MimeType = "application/pdf", FileName = "rakhunok-17.pdf" } };
        var scope = Scope() with { Nodes = new Dictionary<string, IReadOnlyList<Item>> { ["Зберегти вкладення"] = [invoice] } };

        var result = _evaluator.Evaluate("{{ $node[\"Зберегти вкладення\"].binary.data.fileName }}", scope);

        Assert.Equal("rakhunok-17.pdf", result!.GetValue<string>());
    }

    [Fact]
    public void Node_ByName_UsesSameItemIndex()
    {
        var nodes = new Dictionary<string, IReadOnlyList<Item>>
        {
            ["Нова заявка"] = [NewItem("""{"chat":{"id":100}}"""), NewItem("""{"chat":{"id":200}}""")],
        };
        var scope = Scope() with { Nodes = nodes, ItemIndex = 1 };

        var result = _evaluator.Evaluate("{{ $node[\"Нова заявка\"].json.chat.id }}", scope);

        Assert.Equal(200, result!.GetValue<int>());
    }

    [Fact]
    public void Node_ByName_FallsBackToFirstItem()
    {
        var nodes = new Dictionary<string, IReadOnlyList<Item>> { ["Тригер"] = [NewItem("""{"id":7}""")] };
        var scope = Scope() with { Nodes = nodes, ItemIndex = 5 };

        var result = _evaluator.Evaluate("{{ $node['Тригер'].json.id }}", scope);

        Assert.Equal(7, result!.GetValue<int>());
    }

    [Fact]
    public void Node_Unknown_Throws()
    {
        var ex = Assert.Throws<ExpressionException>(() => _evaluator.Evaluate("{{ $node['Немає'].json.id }}", Scope()));

        Assert.Equal("{{ $node['Немає'].json.id }}", ex.Expression);
    }

    [Fact]
    public void Now_IsScopeMomentAndMatchesDateNow()
    {
        var result = _evaluator.Evaluate("{{ [$now.toISOString(), Date.now() === $now.getTime(), new Date().getTime() === $now.getTime()] }}", Scope());

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["2026-10-22T22:30:15.250Z",true,true]"""), result), result?.ToJsonString());
    }

    [Fact]
    public void Now_UsesWorkflowTimezone()
    {
        var scope = Scope() with { Timezone = "Europe/Kyiv" };

        var result = _evaluator.Evaluate("{{ [$now.getHours(), $now.getDate()] }}", scope);

        // 22:30 UTC 22.10.2026 — це 01:30 23.10 за Києвом (EEST, UTC+3).
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[1,23]"), result), result?.ToJsonString());
    }

    [Fact]
    public void Now_InTemplate_IsIsoUtc()
    {
        var result = _evaluator.Evaluate("на {{ $now }}", Scope());

        Assert.Equal("на 2026-10-22T22:30:15.250Z", result!.GetValue<string>());
    }

    [Fact]
    public void InvalidDate_InTemplate_IsInvalidDateText()
    {
        var result = _evaluator.Evaluate("дата: {{ new Date('не дата') }}", Scope());

        Assert.Equal("дата: Invalid Date", result!.GetValue<string>());
    }

    [Fact]
    public void UnknownTimezone_Throws()
    {
        var scope = Scope() with { Timezone = "Mars/Olympus" };

        Assert.Throws<ExpressionException>(() => _evaluator.Evaluate("{{ $now }}", scope));
    }

    [Fact]
    public void Env_ExposesOnlyAllowedVariables()
    {
        var scope = Scope() with { Env = new Dictionary<string, string> { ["REGION"] = "ua" } };

        var result = _evaluator.Evaluate("{{ [$env.REGION, typeof $env.PATH] }}", scope);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["ua","undefined"]"""), result), result?.ToJsonString());
    }

    [Fact]
    public void Vars_ExposeInstanceVariables()
    {
        var scope = Scope() with { Vars = new Dictionary<string, string> { ["region"] = "ua", ["api_url"] = "https://erp" } };

        var result = _evaluator.Evaluate("{{ [$vars.region, $vars['api_url'], typeof $vars.missing] }}", scope);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["ua","https://erp","undefined"]"""), result), result?.ToJsonString());
    }

    [Fact]
    public void Execution_ExposesId()
    {
        var result = _evaluator.Evaluate("{{ $execution.id }}", Scope());

        Assert.Equal(ExecutionId.ToString(), result!.GetValue<string>());
    }

    [Fact]
    public void Execution_ExposesResumeUrlOnlyForSingleWaitNode()
    {
        var one = Scope() with { ResumeUrls = new Dictionary<string, string> { ["Чекати відповідь"] = "https://openbp.test/webhook/resume/t1" } };
        Assert.Equal("https://openbp.test/webhook/resume/t1", _evaluator.Evaluate("{{ $execution.resumeUrl }}", one)!.GetValue<string>());
        Assert.Equal("https://openbp.test/webhook/resume/t1", _evaluator.Evaluate("{{ $execution.resumeUrls['Чекати відповідь'] }}", one)!.GetValue<string>());

        var two = Scope() with { ResumeUrls = new Dictionary<string, string> { ["A"] = "https://openbp.test/webhook/resume/a", ["B"] = "https://openbp.test/webhook/resume/b" } };
        Assert.Equal("undefined", _evaluator.Evaluate("{{ typeof $execution.resumeUrl }}", two)!.GetValue<string>());
        Assert.Equal("https://openbp.test/webhook/resume/b", _evaluator.Evaluate("{{ $execution.resumeUrls.B }}", two)!.GetValue<string>());

        Assert.Equal("undefined", _evaluator.Evaluate("{{ typeof $execution.resumeUrl }}", Scope())!.GetValue<string>());
    }

    // ---- Помилки ----

    [Theory]
    [InlineData("{{ $json. }}")]
    [InlineData("{{ throw new Error('x') }}")]
    [InlineData("{{ $json.a.b.c }}")]
    [InlineData("{{ }}")]
    [InlineData("{{ (function () { return 1; }) }}")]
    [InlineData("{{ 10n }}")]
    [InlineData("Текст {{ null.x }}")]
    public void InvalidExpression_ThrowsExpressionException(string expression)
    {
        var ex = Assert.Throws<ExpressionException>(() => _evaluator.Evaluate(expression, Scope()));

        Assert.Equal(expression, ex.Expression);
    }

    // ---- Ліміти ----

    [Fact]
    public void InfiniteLoop_FailsWithin100Ms()
    {
        // Прогрів: перший виклик платить за JIT і завантаження Jint.
        _evaluator.Evaluate("{{ 1 }}", Scope());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var ex = Assert.Throws<ExpressionException>(() => _evaluator.Evaluate("{{ (() => { while (true) {} })() }}", Scope()));

        stopwatch.Stop();
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.True(stopwatch.ElapsedMilliseconds <= 100, $"Тривалість {stopwatch.ElapsedMilliseconds} мс");
    }

    [Fact]
    public void MemoryLimit_Exceeded_Throws()
    {
        // Довгий таймаут, щоб спрацював саме ліміт пам'яті, а не часу.
        var evaluator = new JintExpressionEvaluator(JintExpressionOptions.Default with { Timeout = TimeSpan.FromSeconds(10) });

        var ex = Assert.Throws<ExpressionException>(() => evaluator.Evaluate(
            "{{ (() => { const a = []; while (true) { a.push(new Array(10000).fill(1)); } })() }}",
            Scope()));

        Assert.IsType<Jint.Runtime.MemoryLimitExceededException>(ex.InnerException);
    }

    [Fact]
    public void RecursionLimit_Exceeded_Throws()
    {
        var evaluator = new JintExpressionEvaluator(JintExpressionOptions.Default with { Timeout = TimeSpan.FromSeconds(10) });

        var ex = Assert.Throws<ExpressionException>(() => evaluator.Evaluate("{{ (function f(n) { return f(n + 1); })(0) }}", Scope()));

        Assert.IsType<Jint.Runtime.RecursionDepthOverflowException>(ex.InnerException);
    }

    [Fact]
    public void RecursionWithinLimit_Succeeds()
    {
        var result = _evaluator.Evaluate("{{ (function f(n) { return n === 0 ? 0 : 1 + f(n - 1); })(50) }}", Scope());

        Assert.Equal(50, result!.GetValue<int>());
    }

    // ---- Ін'єкції ----

    [Theory]
    [InlineData("{{ require('fs') }}")]
    [InlineData("{{ require('child_process').execSync('id') }}")]
    [InlineData("{{ import('fs') }}")]
    [InlineData("{{ import fs from 'fs' }}")]
    [InlineData("{{ System.IO.File.ReadAllText('/etc/passwd') }}")]
    [InlineData("{{ System.Diagnostics.Process.Start('sh') }}")]
    [InlineData("{{ importNamespace('System.IO') }}")]
    [InlineData("{{ host.lib('System') }}")]
    [InlineData("{{ eval('1 + 1') }}")]
    [InlineData("{{ Function('return 1')() }}")]
    [InlineData("{{ $json.constructor.constructor('return this')() }}")]
    [InlineData("{{ process.env }}")]
    [InlineData("{{ fetch('http://example.com') }}")]
    public void SandboxEscapeAttempts_Throw(string expression)
    {
        Assert.Throws<ExpressionException>(() => _evaluator.Evaluate(expression, Scope()));
    }
}
