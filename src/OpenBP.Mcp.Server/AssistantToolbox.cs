using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Application.Assistant;

namespace OpenBP.Mcp.Server;

/// <summary>
/// Інструменти загального помічника (F3-108) — MCP-сервер OpenBP у процесі: той самий набір tools, фільтр помилок і права,
/// що й <c>/mcp</c>, через пару каналів у пам'яті. Сервер отримує провайдер сервісів запиту, тож tools діють від імені
/// користувача, який говорить з помічником.
/// </summary>
/// <param name="options">Налаштування MCP-сервера з <see cref="McpServerSetup.AddOpenBpMcpServer"/>.</param>
/// <param name="services">Сервіси поточного запиту.</param>
/// <param name="loggers">Логи.</param>
public sealed class McpAssistantToolbox(IOptions<McpServerOptions> options, IServiceProvider services, ILoggerFactory loggers) : IAssistantToolbox
{
    /// <inheritdoc />
    public async Task<IAssistantToolSession> OpenAsync(CancellationToken ct)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var stop = new CancellationTokenSource();
        var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), McpServerSetup.ServerName, loggers);
        var server = McpServer.Create(transport, options.Value, loggers, services);
        var running = server.RunAsync(stop.Token);
        try
        {
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), loggers),
                new McpClientOptions { ClientInfo = new Implementation { Name = "openbp-assistant", Version = "1.0.0" } },
                loggers,
                ct).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            return new Session(client, server, running, stop, [.. tools.Select(ToInfo)]);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            stop.Dispose();
            throw;
        }
    }

    private static AssistantToolInfo ToInfo(McpClientTool tool)
    {
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };
        return new AssistantToolInfo(tool.Name, tool.Description, schema, tool.ProtocolTool.Annotations?.ReadOnlyHint == true);
    }

    private sealed class Session(McpClient client, McpServer server, Task running, CancellationTokenSource stop, IReadOnlyList<AssistantToolInfo> tools)
        : IAssistantToolSession
    {
        public IReadOnlyList<AssistantToolInfo> Tools => tools;

        public async Task<AssistantToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            var request = new CallToolRequestParams
            {
                Name = name,
                Arguments = arguments.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            };
            var result = await client.CallToolAsync(request, ct).ConfigureAwait(false);
            JsonNode? content = result.StructuredContent is { } structured ? JsonNode.Parse(structured.GetRawText()) : null;
            var text = content?.ToJsonString(McpJson.Options)
                       ?? string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            return new AssistantToolResult(result.IsError == true, content, text);
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Сервер зупинено разом із сесією.
            }

            await server.DisposeAsync().ConfigureAwait(false);
            stop.Dispose();
        }
    }
}
