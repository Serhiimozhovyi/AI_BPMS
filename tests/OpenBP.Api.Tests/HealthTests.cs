using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenBP.Api.Health;

namespace OpenBP.Api.Tests;

/// <summary>F4-08: анонімні <c>/health/*</c> і формат журналу.</summary>
[Collection(ApiCollection.Name)]
public class HealthTests(ApiFactory api)
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_IsAnonymousAndGreenWithMigratedDb(string path)
    {
        var response = await api.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OtherRoutes_StayClosedForAnonymous()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/workflows")).StatusCode);
    }

    [Theory]
    [InlineData("json", "json")]
    [InlineData("JSON", "json")]
    [InlineData(null, null)]
    public void LogFormat_JsonConsoleWithScopes(string? format, string? formatter)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [LogFormat.Key] = format }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddOpenBpConsole(configuration));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(formatter, provider.GetService<IOptionsMonitor<ConsoleLoggerOptions>>()?.CurrentValue.FormatterName);
        if (formatter is not null)
        {
            Assert.True(provider.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue.IncludeScopes);
        }
    }
}
