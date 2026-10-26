using System.Text;
using System.Text.Json.Nodes;
using OpenBP.Engine.Webhooks;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>F6-05: <c>core.respondToWebhook</c> — відповідь викликачу webhook-а через <see cref="IWebhookResponder"/>.</summary>
public class RespondToWebhookNodeTests
{
    [Fact]
    public async Task Without_body_responds_with_first_item_json_and_passes_items_through()
    {
        var responder = new Recorder();
        var ctx = Ctx.For(new RespondToWebhookNode(responder), "{}", """{ "dealId": 7 }""", """{ "dealId": 8 }""");

        var output = await new RespondToWebhookNode(responder).ExecuteAsync(ctx, default);

        var (executionId, response) = Assert.Single(responder.Calls);
        Assert.Equal(ctx.ExecutionId, executionId);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Headers["Content-Type"]);
        Assert.Equal("""{"dealId":7}""", Encoding.UTF8.GetString(response.Body));
        Assert.Equal(["""{"dealId":7}""", """{"dealId":8}"""], Ctx.Json(Assert.Single(output)));
    }

    [Fact]
    public async Task String_body_is_text_and_custom_status_and_headers_are_kept_without_hop_by_hop()
    {
        var responder = new Recorder();
        var ctx = Ctx.For(new RespondToWebhookNode(responder),
            """{ "statusCode": 404, "body": "Не знайдено", "headers": { "X-Reason": "deal", "Content-Length": "1", "Connection": "close" } }""", "{}");

        await new RespondToWebhookNode(responder).ExecuteAsync(ctx, default);

        var response = Assert.Single(responder.Calls).Response;
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Headers["Content-Type"]);
        Assert.Equal("deal", response.Headers["x-reason"]);
        Assert.False(response.Headers.ContainsKey("Content-Length"));
        Assert.False(response.Headers.ContainsKey("Connection"));
        Assert.Equal("Не знайдено", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task Object_body_is_json_and_explicit_content_type_wins()
    {
        var responder = new Recorder();
        var ctx = Ctx.For(new RespondToWebhookNode(responder),
            """{ "body": { "ok": true }, "headers": { "content-type": "application/vnd.kit+json" } }""", "{}");

        await new RespondToWebhookNode(responder).ExecuteAsync(ctx, default);

        var response = Assert.Single(responder.Calls).Response;
        Assert.Equal("application/vnd.kit+json", response.Headers["Content-Type"]);
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task Binary_key_responds_with_file_bytes_mime_type_and_disposition()
    {
        var responder = new Recorder();
        var ctx = Ctx.For(new RespondToWebhookNode(responder), """{ "binaryKey": "report" }""");
        var file = await ctx.Binary.PutAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), "application/pdf", "Звіт 1.pdf", default);
        ctx.WithItems(new OpenBP.Shared.Workflow.Item { Json = [], Binary = new() { ["report"] = file } });

        await new RespondToWebhookNode(responder).ExecuteAsync(ctx, default);

        var response = Assert.Single(responder.Calls).Response;
        Assert.Equal("application/pdf", response.Headers["Content-Type"]);
        Assert.Equal("attachment; filename*=UTF-8''%D0%97%D0%B2%D1%96%D1%82%201.pdf", response.Headers["Content-Disposition"]);
        Assert.Equal("%PDF-1.7", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task Missing_file_bad_status_or_no_responder_is_node_error()
    {
        var responder = new Recorder();
        var missing = Ctx.For(new RespondToWebhookNode(responder), """{ "binaryKey": "report" }""", "{}");
        var badStatus = Ctx.For(new RespondToWebhookNode(responder), """{ "statusCode": 99 }""", "{}");

        Assert.Contains("report", (await Assert.ThrowsAsync<CoreNodeException>(() => new RespondToWebhookNode(responder).ExecuteAsync(missing, default))).Message);
        Assert.Contains("99", (await Assert.ThrowsAsync<CoreNodeException>(() => new RespondToWebhookNode(responder).ExecuteAsync(badStatus, default))).Message);
        await Assert.ThrowsAsync<CoreNodeException>(() => new RespondToWebhookNode().ExecuteAsync(Ctx.For(new RespondToWebhookNode(), "{}", "{}"), default));
        Assert.Empty(responder.Calls);
    }

    [Fact]
    public async Task Body_larger_than_limit_is_rejected()
    {
        var responder = new Recorder();
        var ctx = Ctx.For(new RespondToWebhookNode(responder), "{}");
        ctx.Parameters = new JsonObject { ["body"] = new string('x', RespondToWebhookNode.MaxBodyBytes + 1) };
        ctx.WithItems(Ctx.Item("{}"));

        await Assert.ThrowsAsync<CoreNodeException>(() => new RespondToWebhookNode(responder).ExecuteAsync(ctx, default));
        Assert.Empty(responder.Calls);
    }

    private sealed class Recorder : IWebhookResponder
    {
        public List<(Guid ExecutionId, WebhookResponse Response)> Calls { get; } = [];

        public Task<bool> RespondAsync(Guid executionId, WebhookResponse response, CancellationToken ct)
        {
            Calls.Add((executionId, response));
            return Task.FromResult(true);
        }
    }
}
