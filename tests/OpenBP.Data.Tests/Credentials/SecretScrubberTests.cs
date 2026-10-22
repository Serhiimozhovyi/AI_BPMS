using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Data.Credentials;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Data.Tests.Credentials;

public class SecretScrubberTests
{
    private const string Token = "sk-live-123456";

    private static SecretScrubber Scrubber() =>
        new SecretScrubberBuilder()
            .With(new JsonObject
            {
                ["apiKey"] = Token,
                ["nested"] = new JsonObject { ["password"] = "p@ss-word" },
                ["list"] = new JsonArray("list-secret"),
                ["pin"] = 98765,
                ["short"] = "ab",
                ["empty"] = "",
                ["flag"] = true,
            })
            .Build();

    [Fact]
    public void Scrub_ReplacesWholeValuesAndSubstrings()
    {
        var s = Scrubber();

        Assert.Equal(SecretScrubber.Mask, s.Scrub(Token));
        Assert.Equal("Bearer ***", s.Scrub("Bearer " + Token));
        Assert.Equal("a=*** b=***", s.Scrub($"a={Token} b=p@ss-word"));
        Assert.Equal("ok", s.Scrub("ok"));
        Assert.Null(s.Scrub((string?)null));
    }

    [Fact]
    public void Scrub_ShortSecrets_OnlyWholeValue()
    {
        var s = Scrubber();

        Assert.Equal(SecretScrubber.Mask, s.Scrub("ab"));
        Assert.Equal("abc lab", s.Scrub("abc lab"));
    }

    [Fact]
    public void Scrub_NestedObjectsArraysAndNumbers()
    {
        var s = Scrubber();
        var json = new JsonObject
        {
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + Token },
            ["rows"] = new JsonArray(
                new JsonObject { ["deep"] = new JsonArray("x", "list-secret", new JsonObject { ["v"] = "p@ss-word!" }) },
                98765,
                98766),
            ["pinText"] = "pin=98765",
            ["count"] = 3,
            ["ok"] = true,
            ["nothing"] = null,
        };

        var scrubbed = s.Scrub(json)!;

        var expected = JsonNode.Parse("""
            {
              "headers": { "Authorization": "Bearer ***" },
              "rows": [ { "deep": [ "x", "***", { "v": "***!" } ] }, "***", 98766 ],
              "pinText": "pin=***",
              "count": 3,
              "ok": true,
              "nothing": null
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, scrubbed), scrubbed.ToJsonString());
        Assert.Contains(Token, json.ToJsonString(), StringComparison.Ordinal); // оригінал не змінено
    }

    [Fact]
    public void Scrub_LongerSecretFirst()
    {
        var s = new SecretScrubberBuilder().With(new JsonObject { ["a"] = "abcd", ["b"] = "abcdefgh" }).Build();

        Assert.Equal("x *** y", s.Scrub("x abcdefgh y"));
        Assert.DoesNotContain("efgh", s.Scrub("abcdefgh"), StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_StepRecord_InputOutputAndError()
    {
        var s = Scrubber();
        var item = new Item
        {
            Json = new JsonObject { ["text"] = "key " + Token },
            Binary = new Dictionary<string, BinaryRef> { ["file"] = new() { Id = "f1" } },
        };
        var step = new StepRecord
        {
            ExecutionId = Guid.NewGuid(),
            NodeId = "n",
            Status = StepStatus.Error,
            Input = [[item]],
            Output = [[], [new Item { Json = new JsonObject { ["error"] = Token } }]],
            Error = new StepError($"401 for {Token}", "HTTP " + Token, 2),
            StartedAt = DateTimeOffset.UnixEpoch,
            FinishedAt = DateTimeOffset.UnixEpoch,
        };

        var scrubbed = s.Scrub(step);

        var text = JsonSerializer.Serialize(scrubbed, WorkflowJson.Options);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.Equal("key ***", scrubbed.Input[0][0].Json["text"]!.GetValue<string>());
        Assert.Equal("f1", scrubbed.Input[0][0].Binary!["file"].Id);
        Assert.Equal("***", scrubbed.Output[1][0].Json["error"]!.GetValue<string>());
        Assert.Equal("401 for ***", scrubbed.Error!.Message);
        Assert.Equal(2, scrubbed.Error.Attempts);
        Assert.Equal("key " + Token, item.Json["text"]!.GetValue<string>()); // items наступних вузлів не змінено
    }

    [Fact]
    public void Scrub_ExtensionData()
    {
        var s = Scrubber();
        var item = JsonSerializer.Deserialize<Item>($$"""{ "json": {}, "pairedItem": { "note": "{{Token}}" } }""", WorkflowJson.Options)!;

        var scrubbed = s.Scrub([[item]]);

        Assert.DoesNotContain(Token, JsonSerializer.Serialize(scrubbed, WorkflowJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_WithoutSecrets_ReturnsSameInstances()
    {
        var s = new SecretScrubber();
        Item[][] ports = [[new Item { Json = new JsonObject { ["a"] = "b" } }]];
        var step = new StepRecord
        {
            ExecutionId = Guid.NewGuid(),
            NodeId = "n",
            Status = StepStatus.Success,
            StartedAt = DateTimeOffset.UnixEpoch,
            FinishedAt = DateTimeOffset.UnixEpoch,
        };

        Assert.False(s.HasSecrets);
        Assert.Same(ports, s.Scrub(ports));
        Assert.Same(step, s.Scrub(step));
    }

    [Fact]
    public async Task Add_IsThreadSafe()
    {
        var s = new SecretScrubber();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => s.Add(new JsonObject { ["k"] = $"secret-{i:D3}" }))));

        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(SecretScrubber.Mask, s.Scrub($"secret-{i:D3}"));
        }
    }

    private sealed class SecretScrubberBuilder
    {
        private readonly SecretScrubber _scrubber = new();

        public SecretScrubberBuilder With(JsonObject data)
        {
            _scrubber.Add(data);
            return this;
        }

        public SecretScrubber Build() => _scrubber;
    }
}
