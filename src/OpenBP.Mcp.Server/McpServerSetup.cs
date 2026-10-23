using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace OpenBP.Mcp.Server;

/// <summary>
/// Підключення MCP-сервера OpenBP до хоста (docs/architecture.md §4): Streamable HTTP у stateless-режимі,
/// tools і resources з цього проєкту, помилки application-шару — результатом tool'а з <c>isError</c>.
/// </summary>
public static class McpServerSetup
{
    /// <summary>Ім'я сервера в <c>initialize</c> (<c>serverInfo.name</c>).</summary>
    public const string ServerName = "openbp";

    /// <summary>Реєструє MCP-сервер; сервіси application-шару (<c>IConnectorService</c> тощо) реєструє хост.</summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpMcpServer(this IServiceCollection services)
    {
        services
            .AddMcpServer(o => o.ServerInfo = new Implementation
            {
                Name = ServerName,
                Version = typeof(McpServerSetup).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            })
            // Без серверних сесій: кожен POST автентифікується окремо, клієнт не прив'язаний до екземпляра Api.
            .WithHttpTransport(o => o.Stateless = true)
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpErrors.Filter).AddReadResourceFilter(McpErrors.ResourceFilter))
            .WithTools<ConnectorTools>(McpJson.Options)
            .WithTools<WorkflowTools>(McpJson.Options)
            .WithTools<ExecutionTools>(McpJson.Options)
            .WithTools<TestNodeTool>(McpJson.Options)
            .WithResources<OpenBpResources>();
        return services;
    }

    /// <summary>
    /// Ендпойнт MCP на <paramref name="pattern"/>; доступ — лише користувачам, автентифікованим схемою
    /// <paramref name="authenticationScheme"/> (API-ключ). Інші схеми (cookie UI) тут не діють: без ключа — 401 до MCP-обміну.
    /// </summary>
    /// <param name="endpoints">Маршрути хоста.</param>
    /// <param name="pattern">Шлях, напр. <c>/mcp</c>.</param>
    /// <param name="authenticationScheme">Схема API-ключа хоста.</param>
    public static IEndpointConventionBuilder MapOpenBpMcp(this IEndpointRouteBuilder endpoints, string pattern, string authenticationScheme)
    {
        var policy = new AuthorizationPolicyBuilder(authenticationScheme).RequireAuthenticatedUser().Build();
        return endpoints.MapMcp(pattern).RequireAuthorization(policy);
    }
}
